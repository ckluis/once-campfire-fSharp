// Port of the tests in rust/crates/db/src/database.rs
//
// Where Rust's tokio tests count blocking-pool threads, these check that reads waiting for a reader
// are queue entries and no threads; where a panicking closure comes back as an error, an exception
// reaches the caller here (see the note at the top of Database.fs).
module Campfire.Db.Tests.DatabaseTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Campfire.RailsCompat.Clock
open Campfire.Db
open Campfire.Db.Tests.Support

let private mainFileLen (path: string) : int64 = FileInfo(path).Length

let private openWithReaders (dir: TempDir) (readers: int) : Database =
    let config = { Config.create (dir.File "test.sqlite3") with Readers = readers }
    Database.Open(config, Testing.defaultEnv ())

let private selectOne (conn: Conn) : int64 = conn.QueryRow("SELECT 1", [||], fun r -> r.Int64 0)

/// Awaits `work` for up to 10 seconds, so that reads which never run fail these tests (saying
/// `what` was awaited) rather than hang them.
let private within (what: string) (work: Task<'T>) : Task<'T> =
    task {
        let! finished = Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds 10.0))
        if not (obj.ReferenceEquals(finished, work)) then failwith $"timed out after 10 s waiting for: {what}"
        return! work
    }

let private withinUnit (what: string) (work: Task) : Task =
    task {
        let! finished = Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds 10.0))
        if not (obj.ReferenceEquals(finished, work)) then failwith $"timed out after 10 s waiting for: {what}"
        do! work
    }

/// Waits until `count` reads are queued for a reader.
let private untilQueued (db: Database) (count: int) : Task =
    withinUnit
        $"{count} reads queued"
        (task {
            while db.QueuedReads < count do
                do! Task.Yield()
        })

/// Awaits reads spawned as tasks, each of which must succeed.
let private allSucceed (what: string) (reads: Task<Result<unit, DbError>> list) : Task =
    withinUnit
        what
        (task {
            for read in reads do
                let! result = read
                unwrap result
        })

/// Occupies every reader thread until the returned event is set.
let private holdEveryReader (db: Database) (readers: int) : Task<ManualResetEventSlim * Task<Result<unit, DbError>> list> =
    task {
        let release = new ManualResetEventSlim(false)
        use started = new SemaphoreSlim(0)
        let holders =
            [ for _ in 1..readers ->
                  db.ReadOffloaded(fun _ ->
                      started.Release() |> ignore
                      release.Wait()) ]
        do!
            withinUnit
                $"{readers} readers each started a read"
                (task {
                    for _ in 1..readers do
                        do! started.WaitAsync()
                })
        return release, holders
    }

[<Fact>]
let ``now is truncated to microseconds`` () =
    let at = DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000.0).AddTicks(1_234_569L)
    let env = { Testing.defaultEnv () with Clock = TestClock.FrozenAt at }
    Assert.Equal("2023-11-14 22:13:20.123456", env.Now().ToDb())

[<Fact>]
let ``a panicking blocking read leaves its connection usable`` () =
    use dir = new TempDir()
    use db = openWithReaders dir 1
    for _ in 1..3 do
        Assert.Throws<Exception>(fun () -> db.ReadBlocking(fun _ -> failwith "a bug in a read") |> ignore) |> ignore
    Assert.Equal(1L, unwrap (db.ReadBlocking selectOne))

/// A `ReadBlocking` inside another opens a connection rather than waiting for its caller's.
[<Fact>]
let ``blocking reads nest`` () =
    use dir = new TempDir()
    use db = openWithReaders dir 1
    let sum =
        Task.Run(fun () -> unwrap (db.ReadBlocking(fun outer -> selectOne outer + unwrap (db.ReadBlocking selectOne))))
    Assert.True(sum.Wait(TimeSpan.FromSeconds 10.0), "the inner read waited for the outer read's connection")
    Assert.Equal(2L, sum.Result)

/// Reads that wait for a reader take no thread: other work still runs while ten reads wait for busy
/// readers.
[<Fact>]
let ``waiting reads hold no threads`` () =
    task {
        use dir = new TempDir()
        use db = openWithReaders dir 2
        let! release, holders = holdEveryReader db 2

        let ran = ref 0
        let waiting =
            [ for _ in 1..10 ->
                  db.Read(fun _ ->
                      Interlocked.Increment ran |> ignore) ]
        do! untilQueued db 10

        let! otherWork = within "other work while reads waited" (Task.Run(fun () -> "done"))
        Assert.Equal("done", otherWork)
        Assert.Equal(0, ran.Value) // no reader came free

        release.Set()
        do! allSucceed "the waiting reads ran once the readers came free" (holders @ waiting)
        Assert.Equal(10, ran.Value)
    }

[<Fact>]
let ``reads run in the order they were queued`` () =
    task {
        use dir = new TempDir()
        use db = openWithReaders dir 1
        let! release, holders = holdEveryReader db 1

        let order = ResizeArray<int>()
        let reads = ResizeArray<Task<Result<unit, DbError>>>()
        for n in 0..9 do
            reads.Add(db.Read(fun _ -> lock order (fun () -> order.Add n)))
            do! untilQueued db (n + 1)

        release.Set()
        do! allSucceed "the queued reads ran once the reader came free" (holders @ List.ofSeq reads)
        Assert.Equal<int list>([ 0..9 ], List.ofSeq order)
    }

/// A queued read runs even when its caller stops waiting for it, because some reads broadcast what a
/// write committed (messages#create's): a request dropped as its client goes away must not lose the
/// broadcast.
[<Fact>]
let ``a read given up while it waits still runs`` () =
    task {
        use dir = new TempDir()
        use db = openWithReaders dir 1
        let! release, holders = holdEveryReader db 1

        let ran = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let abandoned = db.Read(fun _ -> ran.SetResult())
        let! first = Task.WhenAny(abandoned, Task.Delay(TimeSpan.FromMilliseconds 50.0))
        Assert.False(obj.ReferenceEquals(first, abandoned), "the reader is busy")
        Assert.Equal(1, db.QueuedReads) // the abandoned read is still queued

        release.Set()
        do! allSucceed "the holding read finished" holders
        do! withinUnit "the abandoned read ran" ran.Task
    }

[<Fact>]
let ``a panicking read fails and leaves its reader running`` () =
    task {
        use dir = new TempDir()
        use db = openWithReaders dir 1
        for _ in 1..3 do
            let! _ = Assert.ThrowsAsync<Exception>(fun () -> db.Read(fun _ -> failwith "a bug in a read") :> Task)
            let! _ = Assert.ThrowsAsync<Exception>(fun () -> db.ReadOffloaded(fun _ -> failwith "a bug in a read") :> Task)
            ()
        let! result = within "a read after the panics" (db.Read selectOne)
        Assert.Equal(1L, unwrap result)
    }

/// No readers configured still starts one.
[<Fact>]
let ``zero configured readers still read`` () =
    task {
        use dir = new TempDir()
        use db = openWithReaders dir 0
        let! result = within "the read" (db.Read selectOne)
        Assert.Equal(1L, unwrap result)
    }

/// Disposing the database stops its reader threads (Rust stops them with the last handle).
[<Fact>]
let ``the reader threads stop with the database`` () =
    use dir = new TempDir()
    let db = openWithReaders dir 3
    Assert.Equal(3, db.ReaderThreadsAlive)
    (db :> IDisposable).Dispose()
    Assert.Equal(0, db.ReaderThreadsAlive)

[<Fact>]
let ``a panicking write rolls back`` () =
    use conn = Conn.OpenInMemory()
    conn.ExecuteBatch "CREATE TABLE things (id INTEGER)"
    Assert.Throws<Exception>(
        Action(fun () ->
            DbRun.runWrite conn (Testing.defaultEnv ()) (fun tx ->
                tx.Conn.Execute("INSERT INTO things VALUES (1)", [||]) |> ignore
                failwith "a bug in a write")
            |> ignore)
    )
    |> ignore
    Assert.True(conn.IsAutocommit, "the transaction was left open")
    Assert.Equal(0L, conn.Count("SELECT COUNT(*) FROM things", [||]))

[<Fact>]
let ``a panicking write leaves the writer usable`` () =
    use dir = new TempDir()
    use db = openWithReaders dir 1
    unwrap (db.WriteBlocking(fun tx -> tx.Conn.ExecuteBatch "CREATE TABLE things (id INTEGER)"))

    Assert.Throws<Exception>(fun () ->
        db.WriteBlocking(fun tx ->
            tx.Conn.Execute("INSERT INTO things VALUES (1)", [||]) |> ignore
            failwith "a bug in a write")
        |> ignore)
    |> ignore

    unwrap (db.WriteBlocking(fun tx -> tx.Conn.Execute("INSERT INTO things VALUES (2)", [||]) |> ignore))
    let ids = db.ReadBlocking(fun conn -> conn.QueryRow("SELECT group_concat(id) FROM things", [||], fun r -> r.Text 0))
    Assert.Equal("2", unwrap ids)

[<Fact>]
let ``a read runs on the calling thread while a reader is free`` () =
    task {
        use dir = new TempDir()
        use db = openWithReaders dir 1
        let caller = Thread.CurrentThread.ManagedThreadId
        let! inline' = db.Read(fun _ -> Thread.CurrentThread.ManagedThreadId)
        Assert.Equal(caller, unwrap inline')
        let! offloaded = db.ReadOffloaded(fun _ -> Thread.CurrentThread.ManagedThreadId)
        Assert.NotEqual(caller, unwrap offloaded)
    }

/// A connection that comes free while reads are queued is theirs, so a read on the calling thread
/// queues behind them instead of taking it.
[<Fact>]
let ``queued reads get a free connection first`` () =
    let queue = ReadQueue()
    queue.GiveBack(Conn.OpenInMemory())
    queue.Push(fun _ -> ())
    Assert.True(queue.TakeConnection().IsNone)
    let _, conn = (queue.Next None).Value
    queue.GiveBack conn
    Assert.True(queue.TakeConnection().IsSome)
    queue.DisposeConnections()

/// A read queued while the only connection is in use on the calling thread gets it back.
[<Fact>]
let ``a read waits for a connection in use on the calling thread`` () =
    task {
        use dir = new TempDir()
        use db = openWithReaders dir 1
        use release = new ManualResetEventSlim(false)
        let started = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let holder =
            Task.Run<Result<unit, DbError>>(
                Func<Task<Result<unit, DbError>>>(fun () ->
                    db.Read(fun _ ->
                        started.SetResult()
                        release.Wait()))
            )
        do! withinUnit "the inline read started" started.Task
        let waiting = Task.Run<Result<int64, DbError>>(Func<Task<Result<int64, DbError>>>(fun () -> db.Read selectOne))
        do! untilQueued db 1
        release.Set()
        let! held = within "the inline read finished" holder
        unwrap held
        let! result = within "the queued read ran" waiting
        Assert.Equal(1L, unwrap result)
    }

/// Commits never checkpoint on the writer: the WAL reaching the auto-checkpoint threshold wakes the
/// checkpointer, which copies it into the database file on its own.
[<Fact>]
let ``the checkpointer copies the wal into the database`` () =
    use dir = new TempDir()
    let path = dir.File "test.sqlite3"
    use db = openWithReaders dir 1
    unwrap (db.WriteBlocking(fun tx -> tx.Conn.ExecuteBatch "CREATE TABLE filler (data BLOB)"))
    let before = mainFileLen path

    // ~1,200 pages of 4 KiB, over a few commits.
    for _ in 1..6 do
        unwrap (
            db.WriteBlocking(fun tx ->
                tx.Conn.ExecuteBatch
                    "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 200) INSERT INTO filler SELECT randomblob(3900) FROM n")
        )

    let deadline = DateTime.UtcNow + TimeSpan.FromSeconds 10.0
    while mainFileLen path < before + 1000L * 4096L do
        Assert.True(DateTime.UtcNow < deadline, "the WAL was never checkpointed")
        Thread.Sleep 10

/// Before 3.51.3, a checkpoint that starts just as another connection's commit restarts the WAL can
/// leave that commit out of the database (https://sqlite.org/wal.html#walresetbug). The checkpointer
/// and the writer are two such connections, on separate threads.
[<Fact>]
let ``the bundled sqlite has the wal reset fix`` () =
    Assert.True(SQLitePCL.raw.sqlite3_libversion_number () >= 3_051_003, SQLitePCL.raw.sqlite3_libversion().utf8_to_string ())

/// Writes that never pause still get the WAL restarted, at `WalLimitPages`.
[<Fact>]
let ``the wal stays bounded under sustained writes`` () =
    use dir = new TempDir()
    let path = dir.File "test.sqlite3"
    use db = openWithReaders dir 1
    unwrap (db.WriteBlocking(fun tx -> tx.Conn.ExecuteBatch "CREATE TABLE filler (data BLOB)"))

    // ~25,000 pages, 500 per commit.
    for _ in 1..50 do
        unwrap (
            db.WriteBlocking(fun tx ->
                tx.Conn.ExecuteBatch
                    "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 500) INSERT INTO filler SELECT randomblob(3900) FROM n")
        )
    let wal = mainFileLen (path + "-wal")
    Assert.True(wal < (int64 Pragmas.WalLimitPages + 1000L) * 4200L, $"WAL of {wal} bytes")
