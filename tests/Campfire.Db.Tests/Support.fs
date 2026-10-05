/// What the model tests share: a database loaded with the reference fixtures (`fixtures :all`), a
/// recording event sink and a controllable clock (`ActiveSupport::Testing::TimeHelpers`).
module Campfire.Db.Tests.Support

open System
open System.IO
open Campfire.RailsCompat.Clock
open Campfire.Db

/// Unwraps an expected success.
let unwrap (result: Result<'T, DbError>) : 'T =
    match result with
    | Ok value -> value
    | Error e -> failwith (DbError.display e)

/// A fixture's id, by label.
let id (label: string) : int64 = Fixtures.identify label

/// A distinct time for each `n`, with microseconds.
let at (n: int64) : Timestamp = Timestamp.FromMicrosecond(1_790_000_000_000_000L + n * 1_000_123L)

/// A directory for one test's databases, removed when the test is done.
type TempDir() =
    let dir = Directory.CreateTempSubdirectory "campfire-db-test"
    member _.Path : string = dir.FullName
    member _.File(name: string) : string = Path.Combine(dir.FullName, name)

    interface IDisposable with
        member _.Dispose() =
            try
                dir.Delete true
            with _ ->
                ()

/// The environment tests run in: a recording sink, a controllable clock, BCrypt at its minimum cost.
let testEnv (sink: RecordingSink) (clock: TestClock) : Env =
    { Clock = clock
      Sink = sink
      RichText = BasicRichText()
      BcryptCost = 4 }

type TestDb(?fixtures: bool) =
    let dir = new TempDir()
    let sink = RecordingSink()
    let clock = TestClock()
    let db =
        let config = { Config.create (dir.File "test.sqlite3") with Readers = 2; Environment = "test" }
        Database.Open(config, testEnv sink clock)

    do
        if defaultArg fixtures true then
            db.WriteBlocking(fun tx ->
                Fixtures.load tx.Conn (Fixtures.referenceDir ()) { Now = tx.Now(); BcryptCost = 4 } |> ignore)
            |> unwrap

    member _.Db : Database = db
    member _.Sink : RecordingSink = sink
    member _.Clock : TestClock = clock
    member _.Dir : TempDir = dir

    member _.Write(f: Tx -> 'T) : 'T = db.WriteBlocking f |> unwrap

    member _.TryWrite(f: Tx -> 'T) : Result<'T, DbError> = db.WriteBlocking f

    member _.Read(f: Conn -> 'T) : 'T = db.ReadBlocking f |> unwrap

    member _.Now() : Timestamp = Timestamp.FromDateTimeOffset(clock.Now())

    member _.Events() : Event list = sink.Events()

    /// `travel_to Membership::Connectable::CONNECTION_TTL.from_now + 1`
    member _.Travel(seconds: int64) : unit = clock.Travel(TimeSpan.FromSeconds(float seconds))

    interface IDisposable with
        member _.Dispose() =
            (db :> IDisposable).Dispose()
            (dir :> IDisposable).Dispose()
