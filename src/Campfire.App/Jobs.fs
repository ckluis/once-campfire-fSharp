// Port of rust/crates/campfire/src/jobs.rs
//
// The in-process job runner that replaces Resque (see plans/rust-conversion.md, "Jobs").
//
// Models emit `Event`s at the point Rails would `perform_later` (the database's `EventSink`); `Jobs`
// puts each on its kind's **bounded** queue without blocking the writer thread, and each kind has its
// own workers, so a kind that's slow (webhooks to a slow bot) or full can't hold up the others (pushes,
// purges). Nothing retries (`retry_on` is commented out in `reference/app/jobs/application_job.rb`); a
// failure or exception is logged. Queued work is lost if the process crashes, which the plan accepts. On
// shutdown the runner stops taking new work, performs what's queued and waits for it up to a deadline.
//
// Handlers are looked up in a `Registry`. Core registers `RemoveBannedContent` and `PurgeBlob`
// (`CoreJobs`, which needs the app); integrations register `PushMessage` and `DeliverWebhook`.
// `DisconnectUser` is not a job in Rails (it's a synchronous Action Cable broadcast), so it goes
// straight to the cable server.
//
// Rust's handlers are `async` functions returning `anyhow::Result<()>`; here a handler returns a
// `Task<Result<unit, string>>`, where the `Error` is the failure's message with its causes, as
// `{error:#}` prints it. An exception a handler raises is logged as a panic is.
namespace Campfire.App

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Campfire.Db
open Campfire.App.Channels

/// How many jobs of each kind may wait before new ones of that kind are dropped (and logged).
module JobLimits =
    [<Literal>]
    let QueueCapacity = 1024

type JobKind =
    | PushMessage
    | DeliverWebhook
    | RemoveBannedContent
    | PurgeBlob
    /// Work enqueued with `Jobs.PerformLater`.
    | AdHoc

module JobKind =
    let all: JobKind list = [ PushMessage; DeliverWebhook; RemoveBannedContent; PurgeBlob; AdHoc ]

    /// `None` for an event that isn't a job.
    let ofEvent (event: Event) : JobKind option =
        match event with
        | Event.PushMessage _ -> Some PushMessage
        | Event.DeliverWebhook _ -> Some DeliverWebhook
        | Event.RemoveBannedContent _ -> Some RemoveBannedContent
        | Event.PurgeBlob _ -> Some PurgeBlob
        | Event.DisconnectUser _ -> None

    /// The Rails job class, for logs.
    let name (kind: JobKind) : string =
        match kind with
        | PushMessage -> "Room::PushMessageJob"
        | DeliverWebhook -> "Bot::WebhookJob"
        | RemoveBannedContent -> "RemoveBannedContentJob"
        | PurgeBlob -> "ActiveStorage::PurgeJob"
        | AdHoc -> "AdHoc"

/// Performs one kind of job.
type JobHandler<'App> = 'App -> Event -> Task<Result<unit, string>>

/// Which handler performs which kind of job.
[<Sealed>]
type Registry<'App>() =
    let handlers = Dictionary<JobKind, JobHandler<'App>>()

    member _.Handle(kind: JobKind, handler: JobHandler<'App>) : unit = handlers[kind] <- handler

    member _.TryGet(kind: JobKind) : JobHandler<'App> voption =
        match handlers.TryGetValue kind with
        | true, handler -> ValueSome handler
        | _ -> ValueNone

type internal Work =
    | EventWork of JobKind * Event
    | AdHocWork of name: string * run: (unit -> Task<Result<unit, string>>)

module internal Work =
    let kind (work: Work) : JobKind =
        match work with
        | EventWork(kind, _) -> kind
        | AdHocWork _ -> AdHoc

    let name (work: Work) : string =
        match work with
        | EventWork(kind, _) -> JobKind.name kind
        | AdHocWork(name, _) -> name

/// The enqueueing side: the database's event sink, and `PerformLater` for everything else.
[<Sealed>]
type Jobs(capacity: int, logger: ILogger) =
    let queues =
        JobKind.all
        |> List.map (fun kind ->
            let options = BoundedChannelOptions(max capacity 1)
            options.FullMode <- BoundedChannelFullMode.Wait
            options.SingleWriter <- false
            options.SingleReader <- false
            kind, Channel.CreateBounded<Work> options)
        |> dict
    let mutable cable: Cable | null = null
    let mutable stopped = false

    member internal _.Queue(kind: JobKind) : Channel<Work> = queues[kind]

    member internal _.Queued(kind: JobKind) : int = queues[kind].Reader.Count

    member internal _.Logger = logger

    /// Closes every queue to new work (the runner's shutdown): what is already queued stays for the
    /// workers to drain.
    member internal _.Close() : unit =
        stopped <- true
        for queue in queues.Values do
            queue.Writer.TryComplete() |> ignore

    member internal _.Enqueue(work: Work) : unit =
        let name = Work.name work
        if queues[Work.kind work].Writer.TryWrite work then
            if logger.IsEnabled LogLevel.Debug then logger.LogDebug("enqueued job={Job}", name)
        elif stopped then
            logger.LogWarning("job runner stopped, dropping job job={Job}", name)
        else
            logger.LogError("job queue is full, dropping job job={Job}", name)

    /// Enqueues best-effort work (`SomeJob.perform_later`). Dropped with an error log when its queue
    /// is full. `work` runs on a worker, once the job's turn comes.
    member this.PerformLater(name: string, work: unit -> Task<Result<unit, string>>) : unit = this.Enqueue(AdHocWork(name, work))

    member internal _.SetCable(server: Cable) : unit = cable <- server

    interface EventSink with
        member this.Emit(event: Event) : unit =
            match JobKind.ofEvent event, event with
            | Some kind, event -> this.Enqueue(EventWork(kind, event))
            // `ActionCable.server.remote_connections.where(current_user: user).disconnect`: a pub/sub
            // broadcast in Rails, done right away. Before boot finishes there are no connections to
            // disconnect.
            | None, Event.DisconnectUser(userId, reconnect) ->
                match cable with
                | null -> ()
                | cable -> Revocation.disconnectUser cable userId reconnect |> ignore
            | None, event -> logger.LogWarning("not a job, dropping event {Event}", sprintf "%A" event)

/// The running job runner.
[<Sealed>]
type Runner internal (jobs: Jobs, workers: Task list) =
    /// Stops taking new work, performs what's already queued, and waits for running jobs until
    /// `deadline`, after which they're abandoned (logged).
    member _.Shutdown(deadline: TimeSpan) : Task =
        task {
            jobs.Close()
            let finished = Task.WhenAll workers
            let! first = Task.WhenAny(finished, Task.Delay deadline)
            if not (obj.ReferenceEquals(first, finished)) then jobs.Logger.LogWarning "jobs still running at shutdown were abandoned"
        }

module JobRunner =
    /// `Error` carrying the exception's message and its causes, as `{error:#}` prints them.
    let private chain (error: exn) : string =
        let messages = ResizeArray<string>()
        let mutable current: exn | null = error
        while not (isNull current) do
            let e = nonNull current
            messages.Add e.Message
            current <- e.InnerException
        String.Join(": ", messages)

    let private perform (app: 'App) (registry: Registry<'App>) (logger: ILogger) (work: Work) : Task =
        task {
            let name = Work.name work
            try
                let! result =
                    match work with
                    | AdHocWork(_, run) -> run ()
                    | EventWork(kind, event) ->
                        match registry.TryGet kind with
                        | ValueSome handler -> handler app event
                        | ValueNone -> Task.FromResult(Error $"no handler registered for {event}")
                match result with
                | Ok() -> logger.LogInformation("performed job={Job}", name)
                | Error error -> logger.LogError("job failed job={Job} error={Error}", name, error)
            with e ->
                logger.LogError("job panicked job={Job} panic={Panic}", name, chain e)
        }

    /// One of a kind's workers: performs that kind's jobs, one at a time, until its queue has closed
    /// and drained.
    let private work (queue: Channel<Work>) (app: 'App) (registry: Registry<'App>) (logger: ILogger) : Task =
        task {
            let reader = queue.Reader
            let mutable more = true
            while more do
                let! available = reader.WaitToReadAsync()
                if not available then
                    more <- false
                else
                    let mutable item = Unchecked.defaultof<Work>
                    while reader.TryRead(&item) do
                        do! perform app registry logger item
        }

    /// Starts performing queued jobs: `concurrency` workers for each kind of job. `cable` is where
    /// `DisconnectUser` events go.
    let start (jobs: Jobs) (cable: Cable) (app: 'App) (registry: Registry<'App>) (concurrency: int) : Runner =
        jobs.SetCable cable
        let workers =
            [ for kind in JobKind.all do
                  for _ in 1 .. max concurrency 1 do
                      // Each worker runs on the pool; the queue hands every job to exactly one.
                      Task.Run(fun () -> work (jobs.Queue kind) app registry jobs.Logger) ]
        Runner(jobs, workers)
