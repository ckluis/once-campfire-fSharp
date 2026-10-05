// Port of rust/crates/db/src/database.rs
//
// One writer thread that owns the write connection and takes a bounded queue of work, plus
// reader threads that take reads from one queue, each with a free reader connection. Each write
// runs in `BEGIN IMMEDIATE` (`default_transaction_mode: immediate` in
// `reference/config/database.yml`), then its after-commit work runs in order, outside the
// transaction, the way Active Record runs `after_commit` callbacks.
//
// Reads run on their own threads rather than the thread pool, where a read that found every
// connection busy would park a pool thread until one came free. Here a waiting read costs a
// queue entry, reads leave the queue in the order they were queued (with several readers, two
// reads taken one after the other may still start in either order), and a reader that finishes
// a read takes the next one without a hand-off to another thread.
//
// A read that finds a connection free, with no read queued ahead of it, skips the queue and runs
// on the calling task's thread instead (see `Database.Read`).
//
// WAL checkpoints run on a checkpointer thread with a connection of its own, not on the writer.
// Rails keeps SQLite's auto-checkpoint: once a commit leaves the WAL at 1,000 pages or more,
// that commit checkpoints (PASSIVE) before returning, fsyncing the WAL and then the database
// (with the WAL header's fsync when the next write restarts the WAL, ~12 ms here, every ~64
// message posts), and every write queued behind it waits. Here the writer's commits only note
// the WAL's size, and every 1,000 pages it grows wake the checkpointer, which runs the same
// PASSIVE checkpoint while writes carry on appending to the WAL. (Rust notes the size in
// `sqlite3_wal_hook`, which the .NET bindings don't expose. The writer connection has SQLite's
// auto-checkpoint turned off, and the writer asks `PRAGMA wal_checkpoint(NOOP)`, which does no
// checkpointing and takes no locks, for the size after each commit.)
//
// Durability is the same as Rails': `journal_mode=wal` with `synchronous=normal`, so a commit
// doesn't fsync, and what was committed since the WAL was last synced can be lost to a power
// failure (never to a crash of the process); every checkpoint syncs the WAL, and one runs for
// every 1,000 pages written, as in Rails.
//
// The trade-off is WAL size. SQLite only restarts the WAL from its beginning once a checkpoint
// has caught up with it entirely, which a background checkpoint never does while writes keep
// coming. So the WAL grows past 1,000 pages under sustained writes (it restarts in the first
// lull), and at `WalLimitPages` (~40 MB) the writer checkpoints it itself with RESTART,
// stalling writes as Rails' commits do, but once per 10,000 pages instead of per 1,000.
//
// Errors: a model raises `DbException` (or SQLite raises `SqliteException`) where Rust returns
// `Err` (a stored value a `Row` accessor can't read, such as a NULL or a datetime that doesn't parse,
// raises `DbException` too, as rusqlite's `row.get` returns `Err` for it), and the `Database` methods return it as a `Result` at the boundary. Any other exception
// is a bug in the closure: the write rolls back, and the exception reaches the caller.
namespace Campfire.Db

open System
open System.Collections.Generic
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open Campfire.RailsCompat.Clock

/// Everything models need besides the connection: the clock, where side effects go, and
/// the Action Text adapter.
type Env =
    { Clock: SharedClock
      Sink: EventSink
      RichText: RichText
      /// BCrypt cost for `has_secure_password`. Rails uses `BCrypt::Engine.cost` (12), or
      /// `MIN_COST` (4) in the test environment.
      BcryptCost: int }

    /// `Time.current`, at the microseconds a `datetime(6)` column keeps.
    member this.Now() : Timestamp = Timestamp.FromDateTimeOffset(this.Clock.Now())

module internal Log =
    let error (message: string) : unit = eprintfn "ERROR campfire_db: %s" message
    let warn (message: string) : unit = eprintfn "WARN campfire_db: %s" message

/// A write in progress: the writer connection inside a transaction (or, while after-commit
/// work runs, outside one), the environment, and the queued after-commit work.
[<NoComparison; NoEquality>]
type Tx internal (conn: Conn, env: Env, inTransaction: bool) =
    let queue = ResizeArray<Choice<Tx -> unit, Event>>()

    member _.Conn : Conn = conn

    member _.Env : Env = env

    /// `Time.current`
    member _.Now() : Timestamp = env.Now()

    member _.RichText : RichText = env.RichText

    /// Emits an event right away, even though the transaction may still roll back, for the
    /// side effects Rails performs mid-transaction.
    member _.EmitNow(event: Event) : unit = env.Sink.Emit event

    /// Emits an event once the transaction commits (`after_commit`), or right away when
    /// already running after commit.
    member _.EmitAfterCommit(event: Event) : unit =
        if inTransaction then queue.Add(Choice2Of2 event) else env.Sink.Emit event

    /// Queues database work to run after commit, in its own implicit transaction.
    member _.AfterCommit(hook: Tx -> unit) : unit =
        if inTransaction then
            queue.Add(Choice1Of2 hook)
        else
            let tx = Tx(conn, env, false)
            try
                hook tx
            with e ->
                Log.error $"after_commit hook failed: {e.Message}"

    member _.InTransaction : bool = inTransaction

    member internal _.TakeAfterCommit() : Choice<Tx -> unit, Event> list =
        let items = List.ofSeq queue
        queue.Clear()
        items

module DbRun =
    /// What a read or write may raise on purpose, as a `Result`: the `DbError` it stands for. Any other
    /// exception is a bug in the closure and goes on.
    let internal attempt (f: unit -> 'T) : Result<'T, DbError> =
        try
            Ok(f ())
        with
        | DbException e -> Error e
        | :? Microsoft.Data.Sqlite.SqliteException as e -> Error(Sqlite e)

    /// Runs `f` in `BEGIN IMMEDIATE`, commits, then runs the after-commit queue. An error from
    /// `f`, an exception in it, or a failed commit rolls back and discards the queue. An error from
    /// an after-commit hook is raised after the rest of the queue has run (Rails raises it from the
    /// save that committed).
    let runWrite (conn: Conn) (env: Env) (f: Tx -> 'T) : 'T =
        conn.Execute("BEGIN IMMEDIATE", [||]) |> ignore
        let tx = Tx(conn, env, true)
        let value =
            try
                let value = f tx
                conn.Execute("COMMIT", [||]) |> ignore
                value
            with _ ->
                if not conn.IsAutocommit then
                    try
                        conn.Execute("ROLLBACK", [||]) |> ignore
                    with _ ->
                        ()
                reraise ()

        let after = Tx(conn, env, false)
        let mutable firstError: exn option = None
        for item in tx.TakeAfterCommit() do
            match item with
            | Choice2Of2 event -> env.Sink.Emit event
            | Choice1Of2 hook ->
                try
                    hook after
                with e ->
                    Log.error $"after_commit hook failed: {e.Message}"
                    if firstError.IsNone then firstError <- Some e
        match firstError with
        | Some e -> ExceptionDispatchInfo.Capture(e).Throw()
        | None -> ()
        value

type Config =
    { Path: string
      Readers: int
      /// Bound on queued writes; senders wait when it's full.
      WriteQueue: int
      /// Load the schema into an empty database (`db:prepare`).
      Prepare: bool
      /// `ar_internal_metadata.environment` when preparing.
      Environment: string }

module Config =
    let create (path: string) : Config =
        { Path = path; Readers = 8; WriteQueue = 256; Prepare = true; Environment = "production" }

module internal Pragmas =
    /// Prepared statements each connection keeps. A page like the room show runs more than 16.
    [<Literal>]
    let StatementCacheCapacity = 256

    /// SQLite's default `wal_autocheckpoint`, which Rails keeps: a checkpoint per 1,000 WAL pages.
    [<Literal>]
    let AutocheckpointPages = 1000

    /// The WAL size at which the writer checkpoints and restarts the WAL itself, because writes never
    /// paused long enough for a background checkpoint to catch up. Below `journal_size_limit`.
    [<Literal>]
    let WalLimitPages = 10_000

    let openConnection (path: string) (reader: bool) : Conn =
        let conn = Conn.Open(path, StatementCacheCapacity)
        try
            Schema.configureConnection conn
            if reader then conn.ExecuteBatch "PRAGMA query_only = ON"
            conn
        with _ ->
            (conn :> IDisposable).Dispose()
            reraise ()

/// `PRAGMA wal_checkpoint`, which reports a checkpoint it couldn't finish (another checkpoint
/// running, or readers still on old frames past the busy timeout) in its `busy` column, not as an
/// error.
module internal Checkpoint =
    let run (conn: Conn) (mode: string) : unit =
        try
            match conn.QueryRow($"PRAGMA wal_checkpoint({mode})", [||], fun r -> r.Int64 0) with
            | 0L -> ()
            | _ -> Log.warn $"WAL checkpoint couldn't finish ({mode})"
        with e ->
            Log.warn $"WAL checkpoint failed ({mode}): {e.Message}"

    /// The WAL's size in frames: what `sqlite3_wal_hook` reports after a commit. NOOP mode checkpoints
    /// nothing and takes no locks (SQLite 3.51+).
    let walPages (conn: Conn) : int = int (conn.QueryRow("PRAGMA wal_checkpoint(NOOP)", [||], fun r -> r.Int64 1))

/// The writer's side of the checkpointer thread, which runs a PASSIVE checkpoint on its own
/// connection each time it's woken.
type internal Checkpoints(path: string) =
    let conn = Pragmas.openConnection path false
    let wake = new SemaphoreSlim(0, 1)
    let running = obj ()
    let mutable stopped = false
    /// The WAL's size in pages when the checkpointer was last woken.
    let mutable wokenAt = 0

    let thread =
        Thread(
            (fun () ->
                while (wake.Wait(); not stopped) do
                    // Held while the checkpointer runs, so the writer's RESTART waits for it rather
                    // than being refused (SQLite runs one checkpoint at a time) and letting the WAL
                    // grow past its limit.
                    lock running (fun () -> Checkpoint.run conn "PASSIVE")),
            Name = "campfire-db-checkpointer",
            IsBackground = true
        )

    do thread.Start()

    /// Wakes the checkpointer for every `AutocheckpointPages` the WAL grows.
    member _.WalGrewTo(pages: int) : unit =
        if pages < wokenAt then wokenAt <- 0 // the WAL restarted
        // While a checkpoint is still due (it hasn't taken the last wake), the next commit tries again.
        if pages - wokenAt >= Pragmas.AutocheckpointPages && wake.CurrentCount = 0 then
            try
                wake.Release() |> ignore
                wokenAt <- pages
            with :? SemaphoreFullException ->
                ()

    /// A RESTART checkpoint on the writer connection, between writes: it copies what the
    /// checkpointer hasn't, and waits for readers so that the next write restarts the WAL. It waits
    /// for a running PASSIVE checkpoint first, which would otherwise make SQLite refuse it.
    member _.RestartWal(writer: Conn) : unit = lock running (fun () -> Checkpoint.run writer "RESTART")

    member _.Stop() : unit =
        stopped <- true
        try
            wake.Release() |> ignore
        with :? SemaphoreFullException ->
            ()
        thread.Join()
        (conn :> IDisposable).Dispose()

/// The reads waiting for a reader thread, in order, and the reader connections not in use.
type internal ReadQueue() =
    let gate = obj ()
    let reads = Queue<Conn -> unit>()
    let connections = Stack<Conn>()
    /// Reader threads waiting for a read and a connection to run it on.
    let mutable idle = 0
    let mutable closed = false

    /// Wakes a reader thread when one is waiting and there's a read and a connection for it. Called
    /// with the lock held.
    let wakeAReader () =
        if idle > 0 && reads.Count > 0 && connections.Count > 0 then Monitor.Pulse gate

    member _.Push(read: Conn -> unit) : unit =
        lock gate (fun () ->
            reads.Enqueue read
            wakeAReader ())

    /// A connection for a read on the calling thread, unless none is free or reads are queued
    /// (they go first).
    member _.TakeConnection() : Conn option =
        lock gate (fun () -> if reads.Count = 0 && connections.Count > 0 then Some(connections.Pop()) else None)

    /// Adds a connection (at start-up), or returns one taken by `TakeConnection`.
    member _.GiveBack(conn: Conn) : unit =
        lock gate (fun () ->
            connections.Push conn
            wakeAReader ())

    /// For a reader thread, which gives back the connection of the read it `finished`: the next
    /// read and a connection to run it on, once there are both; `None` once the queue is closed
    /// and empty.
    member _.Next(finished: Conn option) : ((Conn -> unit) * Conn) option =
        lock gate (fun () ->
            finished |> Option.iter connections.Push
            let mutable result = None
            let mutable exit = false
            while result.IsNone && not exit do
                if reads.Count > 0 && connections.Count > 0 then
                    result <- Some(reads.Dequeue(), connections.Pop())
                elif closed && reads.Count = 0 then
                    exit <- true
                else
                    idle <- idle + 1
                    Monitor.Wait gate |> ignore
                    idle <- idle - 1
            result)

    member _.Close() : unit =
        lock gate (fun () ->
            closed <- true
            Monitor.PulseAll gate)

    member _.Queued : int = lock gate (fun () -> reads.Count)

    member _.DisposeConnections() : unit =
        lock gate (fun () ->
            while connections.Count > 0 do
                (connections.Pop() :> IDisposable).Dispose())

/// A write for the writer thread; true when it committed.
type internal WriteJob = Conn -> Env -> bool

/// The bounded queue of work for the writer thread.
type internal WriteQueue(capacity: int) =
    let gate = obj ()
    let items = Queue<WriteJob>()
    let slots = new SemaphoreSlim(capacity, capacity)
    let mutable closed = false

    let enqueue (job: WriteJob) : bool =
        lock gate (fun () ->
            if closed then
                slots.Release() |> ignore
                false
            else
                items.Enqueue job
                Monitor.Pulse gate
                true)

    /// Waits for room in the queue without holding a thread.
    member _.EnqueueAsync(job: WriteJob) : Task<bool> =
        task {
            do! slots.WaitAsync()
            return enqueue job
        }

    member _.Enqueue(job: WriteJob) : bool =
        slots.Wait()
        enqueue job

    /// For the writer thread: the next job, or None once closed and drained.
    member _.Take() : WriteJob option =
        lock gate (fun () ->
            while items.Count = 0 && not closed do
                Monitor.Wait gate |> ignore
            if items.Count > 0 then
                let job = items.Dequeue()
                slots.Release() |> ignore
                Some job
            else
                None)

    member _.Close() : unit =
        lock gate (fun () ->
            closed <- true
            Monitor.PulseAll gate)

/// The database handle: dispose it to stop its threads and close its connections.
[<NoComparison; NoEquality>]
type Database
    private
    (
        env: Env,
        path: string,
        writeQueue: WriteQueue,
        readers: ReadQueue,
        readerThreads: Thread list,
        checkpoints: Checkpoints,
        writerThread: Thread,
        writer: Conn
    ) =
    // Not a `Monitor`, which the thread already holding it could enter again: a `ReadBlocking` inside
    // another gets a connection of its own.
    let blockingGate = new SemaphoreSlim(1, 1)
    let mutable blockingReader: Conn option = None
    let mutable disposed = false

    /// Opens (and with `Config.Prepare` prepares) the database at `config.Path`, and starts its writer,
    /// checkpointer and reader threads.
    static member Open(config: Config, env: Env) : Database =
        let conn = Pragmas.openConnection config.Path false
        try
            if config.Prepare then Schema.prepare conn config.Environment env.Clock |> ignore
        with _ ->
            (conn :> IDisposable).Dispose()
            reraise ()
        let checkpoints = Checkpoints config.Path
        // In place of the auto-checkpoint, which the writer's commits note the WAL size for instead
        // (Rust installs `sqlite3_wal_hook`, which the .NET bindings don't expose).
        conn.ExecuteBatch "PRAGMA wal_autocheckpoint = 0"

        let queue = WriteQueue(max config.WriteQueue 1)
        let writerThread =
            Thread(
                (fun () ->
                    let mutable running = true
                    while running do
                        match queue.Take() with
                        | None -> running <- false
                        | Some job ->
                            // A write that raises must not take the writer down with it. `runWrite`
                            // rolls back as the exception goes; the rollback here is a backstop for a
                            // job that fails some other way.
                            let committed =
                                try
                                    job conn env
                                with _ ->
                                    if not conn.IsAutocommit then
                                        try
                                            conn.ExecuteBatch "ROLLBACK TRANSACTION"
                                        with _ ->
                                            ()
                                    false
                            if committed then
                                match (try Checkpoint.walPages conn with _ -> 0) with
                                | 0 -> ()
                                | pages when pages >= Pragmas.WalLimitPages -> checkpoints.RestartWal conn
                                | pages -> checkpoints.WalGrewTo pages),
                Name = "campfire-db-writer",
                IsBackground = true
            )
        writerThread.Start()

        let readers = ReadQueue()
        let threads = ResizeArray<Thread>()
        try
            for _ in 1 .. max config.Readers 1 do
                readers.GiveBack(Pragmas.openConnection config.Path true)
                let thread =
                    Thread(
                        (fun () ->
                            let mutable finished = None
                            let mutable running = true
                            while running do
                                match readers.Next finished with
                                | None -> running <- false
                                | Some(read, conn) ->
                                    // A read that fails fails its caller's read, not the reader.
                                    try
                                        read conn
                                    with _ ->
                                        ()
                                    finished <- Some conn),
                        Name = "campfire-db-reader",
                        IsBackground = true
                    )
                thread.Start()
                threads.Add thread
        with _ ->
            // A connection failing to open stops the threads started before.
            readers.Close()
            reraise ()
        new Database(env, config.Path, queue, readers, List.ofSeq threads, checkpoints, writerThread, conn)

    member _.Env : Env = env

    member _.Path : string = path

    /// Reader threads still running; zero once disposed.
    member internal _.ReaderThreadsAlive : int = readerThreads |> List.filter (fun t -> t.IsAlive) |> List.length

    /// Reads waiting for a reader thread.
    member internal _.QueuedReads : int = readers.Queued

    /// Runs `f` as one immediate transaction on the writer thread.
    member _.Write(f: Tx -> 'T) : Task<Result<'T, DbError>> =
        task {
            let reply = TaskCompletionSource<Result<'T, DbError>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let job (conn: Conn) (env: Env) : bool =
                let mutable committed = false
                try
                    reply.SetResult(
                        DbRun.attempt (fun () ->
                            let value = DbRun.runWrite conn env f
                            committed <- true
                            value)
                    )
                with e ->
                    reply.SetException e
                committed
            let! queued = writeQueue.EnqueueAsync job
            if queued then return! reply.Task else return Error WriterGone
        }

    /// `Write` for synchronous callers (not from inside an async task).
    member _.WriteBlocking(f: Tx -> 'T) : Result<'T, DbError> =
        use replied = new ManualResetEventSlim(false)
        let outcome: Result<Result<'T, DbError>, exn> option ref = ref None
        let job (conn: Conn) (env: Env) : bool =
            let mutable committed = false
            outcome.Value <-
                try
                    Some(
                        Ok(
                            DbRun.attempt (fun () ->
                                let value = DbRun.runWrite conn env f
                                committed <- true
                                value)
                        )
                    )
                with e ->
                    Some(Error e)
            replied.Set()
            committed
        if writeQueue.Enqueue job then
            replied.Wait()
            match outcome.Value with
            | Some(Ok result) -> result
            | Some(Error e) ->
                ExceptionDispatchInfo.Capture(e).Throw()
                Error WriterGone
            | None -> Error WriterGone
        else
            Error WriterGone

    /// Runs `f` on a reader connection: right here, on the calling task's thread, when one is free
    /// and no read is queued for one, since a read on a warm page cache takes less time than the
    /// hop to a reader thread and back; otherwise as `ReadOffloaded` does. At most as
    /// many pool threads as there are readers are ever inside `f`.
    ///
    /// Reads whose cost grows with the whole database rather than with a page (search, every
    /// user, all of a user's messages) use `ReadOffloaded`, to keep them off the pool's threads.
    member this.Read(f: Conn -> 'T) : Task<Result<'T, DbError>> =
        match readers.TakeConnection() with
        | None -> this.ReadOffloaded f
        | Some conn ->
            task {
                // A read that raises a bug still gives its connection back.
                let result =
                    try
                        DbRun.attempt (fun () -> f conn)
                    finally
                        readers.GiveBack conn
                // Give the pool's other tasks their turn, as the hop to another thread did.
                do! Task.Yield()
                return result
            }

    /// Runs `f` on a reader thread, the next one free. Once queued, `f` runs even if its caller
    /// stops waiting (a request dropped when its client goes away): some reads broadcast
    /// what a write committed, like messages#create's.
    member _.ReadOffloaded(f: Conn -> 'T) : Task<Result<'T, DbError>> =
        let reply = TaskCompletionSource<Result<'T, DbError>>(TaskCreationOptions.RunContinuationsAsynchronously)
        readers.Push(fun conn ->
            try
                reply.SetResult(DbRun.attempt (fun () -> f conn))
            with e ->
                reply.SetException e)
        reply.Task

    /// `Read` for synchronous callers (tests), on the calling thread, so that `f` may borrow. It
    /// keeps a reader connection for these reads, and opens another for one that finds it in use:
    /// a `ReadBlocking` inside another would otherwise wait for itself.
    member _.ReadBlocking(f: Conn -> 'T) : Result<'T, DbError> =
        if blockingGate.Wait 0 then
            try
                let conn =
                    match blockingReader with
                    | Some conn -> conn
                    | None ->
                        let conn = Pragmas.openConnection path true
                        blockingReader <- Some conn
                        conn
                DbRun.attempt (fun () -> f conn)
            finally
                blockingGate.Release() |> ignore
        else
            use conn = Pragmas.openConnection path true
            DbRun.attempt (fun () -> f conn)

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true
                writeQueue.Close()
                writerThread.Join()
                checkpoints.Stop()
                (writer :> IDisposable).Dispose()
                readers.Close()
                for thread in readerThreads do
                    thread.Join()
                readers.DisposeConnections()
                blockingReader |> Option.iter (fun conn -> (conn :> IDisposable).Dispose())
