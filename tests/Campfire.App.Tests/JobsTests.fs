// Port of rust/crates/campfire/src/jobs/tests.rs
module Campfire.App.Tests.JobsTests

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Campfire.App
open Campfire.App.Tests.Support
open Campfire.Db

/// A handler that reports each event it performs, after `gate` lets it through (when given).
let private reporting (performed: ChannelWriter<Event>) (gate: SemaphoreSlim option) : JobHandler<AppState> =
    fun _app event ->
        task {
            match gate with
            | Some gate -> do! gate.WaitAsync()
            | None -> ()
            performed.TryWrite event |> ignore
            return Ok()
        }

let private nextPerformed (performed: ChannelReader<Event>) : Task<Event> =
    task {
        use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)
        return! performed.ReadAsync(timeout.Token).AsTask()
    }

let private webhook (messageId: int64) : Event = Event.DeliverWebhook(1L, messageId)

[<Fact>]
let ``a busy or full kind doesnt hold up the others`` () =
    task {
        use! test = bootEmpty ()
        use logs = new CapturingLoggers()
        let performed = Channel.CreateUnbounded<Event>()
        let slowBot = new SemaphoreSlim(0)
        let registry = Registry<AppState>()
        registry.Handle(JobKind.DeliverWebhook, reporting performed.Writer (Some slowBot))
        registry.Handle(JobKind.PurgeBlob, reporting performed.Writer None)
        let jobs = Jobs(2, logs.Logger "jobs")
        let runner = JobRunner.start jobs test.App.Cable test.App registry 1
        let sink = jobs :> EventSink

        // One webhook runs (stuck on the slow bot), two wait, and the rest are dropped.
        sink.Emit(webhook 1L)
        while jobs.Queued JobKind.DeliverWebhook > 0 do
            do! Task.Yield()
        for messageId in 2L .. 10L do
            sink.Emit(webhook messageId)
        sink.Emit(Event.PurgeBlob 7L)
        let! purged = nextPerformed performed.Reader
        Assert.Equal(Event.PurgeBlob 7L, purged)
        Assert.Contains("job queue is full, dropping job job=Bot::WebhookJob", logs.Text)

        for _ in 1..3 do
            slowBot.Release() |> ignore
            match! nextPerformed performed.Reader with
            | Event.DeliverWebhook _ -> ()
            | other -> failwith $"{other}"
        do! runner.Shutdown(TimeSpan.FromSeconds 5.0)
        Assert.False(performed.Reader.TryRead() |> fst)
    }

[<Fact>]
let ``a panicking job is logged and its worker carries on`` () =
    task {
        use! test = bootEmpty ()
        use logs = new CapturingLoggers()
        let jobs = Jobs(JobLimits.QueueCapacity, logs.Logger "jobs")
        let runner = JobRunner.start jobs test.App.Cable test.App (Registry<AppState>()) 1

        let finished = TaskCompletionSource()
        jobs.PerformLater("Exploding", fun () -> failwith "kaboom")
        jobs.PerformLater("After", fun () -> task { finished.SetResult(); return Ok() })
        let! first = Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds 5.0))
        Assert.True(obj.ReferenceEquals(first, finished.Task), "the worker carried on")
        do! runner.Shutdown(TimeSpan.FromSeconds 5.0)

        Assert.Contains("job panicked job=Exploding panic=kaboom", logs.Text)
    }

[<Fact>]
let ``a failed job is logged with its cause`` () =
    task {
        use! test = bootEmpty ()
        use logs = new CapturingLoggers()
        let jobs = Jobs(JobLimits.QueueCapacity, logs.Logger "jobs")
        let runner = JobRunner.start jobs test.App.Cable test.App (Registry<AppState>()) 1

        let finished = TaskCompletionSource()
        jobs.PerformLater("Failing", fun () -> Task.FromResult(Error "purging the blob: disk full"))
        jobs.PerformLater("After", fun () -> task { finished.SetResult(); return Ok() })
        let! first = Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds 5.0))
        Assert.True(obj.ReferenceEquals(first, finished.Task), "the worker carried on")
        do! runner.Shutdown(TimeSpan.FromSeconds 5.0)

        Assert.Contains("job failed job=Failing error=purging the blob: disk full", logs.Text)
    }

[<Fact>]
let ``shutdown performs what is queued and then takes no more`` () =
    task {
        use! test = bootEmpty ()
        use logs = new CapturingLoggers()
        let performed = Channel.CreateUnbounded<Event>()
        let registry = Registry<AppState>()
        registry.Handle(JobKind.DeliverWebhook, reporting performed.Writer None)
        let jobs = Jobs(JobLimits.QueueCapacity, logs.Logger "jobs")
        let runner = JobRunner.start jobs test.App.Cable test.App registry 2
        let sink = jobs :> EventSink

        for messageId in 1L .. 5L do
            sink.Emit(webhook messageId)
        do! runner.Shutdown(TimeSpan.FromSeconds 5.0)
        sink.Emit(webhook 6L)

        let ids =
            [ let mutable event = Unchecked.defaultof<Event>
              while performed.Reader.TryRead(&event) do
                  match event with
                  | Event.DeliverWebhook(_, messageId) -> messageId
                  | other -> failwith $"{other}" ]
            |> List.sort
        Assert.Equal<int64 list>([ 1L; 2L; 3L; 4L; 5L ], ids)
        Assert.Contains("job runner stopped, dropping job", logs.Text)
    }
