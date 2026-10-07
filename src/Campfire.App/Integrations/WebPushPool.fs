// Port of rust/crates/campfire/src/integrations/web_push/pool.rs
//
// `WebPush::Pool` (reference/lib/web_push/pool.rb) with the invalid-subscription handler from
// reference/config/initializers/web_push.rb: up to 50 deliveries at once and 10,000 waiting (more are dropped, like
// `Concurrent::RejectedExecutionError`, and logged), and one worker that destroys expired or unusable subscriptions in
// order.
namespace Campfire.App.Integrations

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Campfire.Db
open Campfire.RichText

/// `Concurrent::ThreadPoolExecutor.new(max_threads: 50, max_queue: 10000)`
module private PoolLimits =
    [<Literal>]
    let MaxThreads = 50

    [<Literal>]
    let MaxQueue = 10000

    /// Deliveries running or waiting.
    [<Literal>]
    let QueueSlots = MaxThreads + MaxQueue

[<Sealed>]
type WebPushPool(net: Network, vapid: VapidConfig, invalidSubscriptionHandler: int64 -> Result<unit, string>, logger: ILogger) =
    let running = new SemaphoreSlim(PoolLimits.MaxThreads)
    /// A delivery holds one of these until it finishes, or unwinds.
    let slots = new SemaphoreSlim(PoolLimits.QueueSlots)
    /// Deliveries dropped since the queue was last accepting them.
    let mutable dropped = 0
    let mutable shutDown = false
    let invalidations = new BlockingCollection<int64>()

    let invalidator =
        let thread =
            Thread(
                (fun () ->
                    for id in invalidations.GetConsumingEnumerable() do
                        logger.LogInformation("Destroying push subscription: {Id}", id)
                        try
                            match invalidSubscriptionHandler id with
                            | Ok() -> ()
                            | Error message -> logger.LogError("Error in WebPush::Pool.invalid_subscription_handler: {Message}", message)
                        with e ->
                            logger.LogError("Error in WebPush::Pool.invalid_subscription_handler: {Panic}", e.Message)),
                IsBackground = true,
                Name = "web_push-invalidation"
            )
        thread.Start()
        thread

    member private _.Deliver(notification: Notification) : Task =
        task {
            match! WebPush.deliver net vapid notification with
            | Ok _ -> ()
            | Error error when DeliveryError.invalidatesSubscription error ->
                if not invalidations.IsAddingCompleted then
                    try
                        invalidations.Add notification.Subscription.Id
                    with :? InvalidOperationException ->
                        ()
            | Error error -> logger.LogError("Error in WebPush::Pool.deliver: {Class} {Error}", DeliveryError.className error, DeliveryError.message error)
        }

    member this.DeliverLater(notification: Notification) : unit =
        if Volatile.Read(&shutDown) then
            logger.LogWarning "WebPush::Pool is shut down, dropping a notification"
        elif not (slots.Wait 0) then
            if Interlocked.Increment(&dropped) = 1 then logger.LogError "WebPush::Pool is full, dropping notifications"
        else
            match Interlocked.Exchange(&dropped, 0) with
            | 0 -> ()
            | count -> logger.LogError("WebPush::Pool dropped {Count} notifications while it was full", count)
            // Released when the delivery finishes, or when it raises.
            Task.Run(fun () ->
                task {
                    try
                        do! running.WaitAsync()
                        try
                            do! this.Deliver notification
                        finally
                            running.Release() |> ignore
                    with e ->
                        logger.LogError("WebPush::Pool delivery panicked: {Panic}", e.Message)
                    slots.Release() |> ignore
                }
                :> Task)
            |> ignore

    /// `queue(payload, subscriptions)`: in id order (`find_each`), each subscription's notification is built here
    /// (counting its badge) and delivered on the pool.
    member this.Queue(conn: Conn, payload: PushPayload, subscriptions: PushSubscription list) : unit =
        for subscription in subscriptions |> List.sortBy (fun s -> s.Id) do
            this.DeliverLater(WebPush.Notification.build conn subscription payload)

    /// Waits (up to a second, like `wait_for_termination(1)`) for queued deliveries, then stops the invalidation
    /// worker once it has drained.
    member _.Shutdown() : Task =
        task {
            Volatile.Write(&shutDown, true)
            // Every slot free means nothing is queued or running.
            let deadline = DateTime.UtcNow.AddSeconds 1.0
            while slots.CurrentCount < PoolLimits.QueueSlots && DateTime.UtcNow < deadline do
                do! Task.Delay 5
            invalidations.CompleteAdding()
            do! Task.Run(fun () -> invalidator.Join())
        }

    member _.Vapid: VapidConfig = vapid

    /// Queued or running deliveries.
    member _.Pending: int = PoolLimits.QueueSlots - slots.CurrentCount

module WebPushPool =
    /// `Room::PushMessageJob#perform` / `Room::MessagePusher#push`: the payload goes to the subscriptions of
    /// everyone involved in everything, then to mentioned users involved in mentions. Badges are counted here;
    /// delivery happens on the pool.
    let pushMessage (pool: WebPushPool) (conn: Conn) (richText: Campfire.Db.RichText) (message: Message) (now: Timestamp) : PushPayload =
        let payload, everything, mentions = PushSubscription.pushesFor conn richText message now
        pool.Queue(conn, payload, everything)
        pool.Queue(conn, payload, mentions)
        payload
