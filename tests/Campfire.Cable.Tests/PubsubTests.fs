// Port of the tests in rust/crates/cable/src/pubsub.rs
module Campfire.Cable.Tests.PubsubTests

open System
open Xunit
open Campfire.Cable
open Campfire.Cable.Socket

let private recv (subscriber: Subscriber) : Result<string, RecvStatus> =
    let mutable frame = Unchecked.defaultof<Frame>
    match subscriber.TryRecv(&frame) with
    | RecvStatus.Got -> Ok frame.Text
    | status -> Error status

let private frameOf (subscriber: Subscriber) : Frame =
    let mutable frame = Unchecked.defaultof<Frame>
    Assert.Equal(RecvStatus.Got, subscriber.TryRecv(&frame))
    frame

[<Fact>]
let ``delivers to every subscriber and cleans up`` () =
    let hub = Hub(8)
    let a = hub.Subscribe("room", null, Wake())
    let b = hub.Subscribe("room", null, Wake())
    Assert.Equal(2, hub.Broadcast("room", "1"))
    Assert.Equal(Ok "1", recv a)
    Assert.Equal(Ok "1", recv b)
    (a :> IDisposable).Dispose()
    Assert.Equal(1, hub.StreamCount)
    (b :> IDisposable).Dispose()
    Assert.Equal(0, hub.StreamCount)
    Assert.Equal(0, hub.Broadcast("room", "2"))

[<Fact>]
let ``wraps payloads once per identifier`` () =
    let hub = Hub(8)
    let identifier = "\"{\\\"channel\\\":\\\"RoomChannel\\\"}\""
    let a = hub.Subscribe("room", identifier, Wake())
    let b = hub.Subscribe("room", identifier, Wake())
    let other = hub.Subscribe("room", "\"other\"", Wake())
    let raw = hub.Subscribe("room", null, Wake())
    Assert.Equal(4, hub.Broadcast("room", """{"id":1}"""))

    let a, b = frameOf a, frameOf b
    Assert.Equal("""{"identifier":"{\"channel\":\"RoomChannel\"}","message":{"id":1}}""", a.Text)
    Assert.Same(a, b) // one frame shared by both subscribers
    Assert.Equal(Ok """{"identifier":"other","message":{"id":1}}""", recv other)
    Assert.Equal(Ok """{"id":1}""", recv raw)

    (other :> IDisposable).Dispose()
    (raw :> IDisposable).Dispose()
    Assert.Equal(1, hub.GroupCount "room")

[<Fact>]
let ``slow subscribers see lag instead of gaps`` () =
    let hub = Hub(2)
    let slow = hub.Subscribe("room", null, Wake())
    for i in 0..4 do
        hub.Broadcast("room", string i) |> ignore
    Assert.Equal(Error RecvStatus.Lagged, recv slow)

[<Fact>]
let ``a subscriber that keeps up never lags, and one that fell behind reads on from the oldest kept`` () =
    let hub = Hub(4)
    let steady = hub.Subscribe("room", null, Wake())
    let slow = hub.Subscribe("room", null, Wake())
    for i in 0..99 do
        hub.Broadcast("room", string i) |> ignore
        Assert.Equal(Ok(string i), recv steady)
    Assert.Equal(Error RecvStatus.Lagged, recv slow)
    // As tokio's receiver does after `Lagged`: the oldest message still in the ring comes next.
    Assert.Equal(Ok "96", recv slow)

[<Fact>]
let ``a broadcast sets the wake of every subscriber`` () =
    task {
        let hub = Hub(8)
        let wakes = [ for _ in 1..3 -> Wake() ]
        let subscribers = [ for wake in wakes -> hub.Subscribe("room", null, wake) ]
        hub.Broadcast("room", "x") |> ignore
        for wake in wakes do
            let! woke = wake.WaitAsync()
            Assert.True woke
        ignore subscribers
    }

[<Fact>]
let ``a wake waits for a set, which counts once however many there were`` () =
    task {
        let wake = Wake()
        let waiting = wake.WaitAsync()
        Assert.False waiting.IsCompleted
        wake.Set()
        let! _ = waiting
        wake.Set()
        wake.Set()
        Assert.True(wake.WaitAsync().IsCompleted)
        Assert.False(wake.WaitAsync().IsCompleted)
    }

[<Fact>]
let ``stopped subscribers read nothing`` () =
    let hub = Hub(8)
    let subscriber = hub.Subscribe("room", null, Wake())
    hub.Broadcast("room", "1") |> ignore
    (subscriber :> IDisposable).Dispose()
    Assert.True subscriber.Stopped
    Assert.Equal(Error RecvStatus.Empty, recv subscriber)
    Assert.Equal(0, hub.StreamCount)

[<Fact>]
let ``concurrent broadcasts and subscribers lose nothing`` () =
    let hub = Hub(1 <<< 16)
    let subscribers = [| for _ in 1..8 -> hub.Subscribe("room", null, Wake()) |]
    let senders = 4
    let each = 5000
    let tasks =
        [| for s in 0 .. senders - 1 ->
               System.Threading.Tasks.Task.Run(fun () ->
                   for i in 0 .. each - 1 do
                       hub.Broadcast("room", $"{s}:{i}") |> ignore) |]
    System.Threading.Tasks.Task.WaitAll tasks
    for subscriber in subscribers do
        let seen = Collections.Generic.List<string>()
        let mutable reading = true
        while reading do
            match recv subscriber with
            | Ok text -> seen.Add text
            | Error status ->
                Assert.Equal(RecvStatus.Empty, status)
                reading <- false
        Assert.Equal(senders * each, seen.Count)
        // Each sender's messages arrive in the order it sent them.
        for s in 0 .. senders - 1 do
            let own = seen |> Seq.filter (fun t -> t.StartsWith $"{s}:") |> Seq.map (fun t -> int (t.Substring(t.IndexOf ':' + 1))) |> Seq.toArray
            Assert.Equal<int[]>([| 0 .. each - 1 |], own)
