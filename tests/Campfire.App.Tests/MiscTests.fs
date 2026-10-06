// Port of the small test modules of rust/crates/campfire: concerns.rs, rich_text.rs, controllers/sessions.rs,
// active_storage.rs and main.rs.
module Campfire.App.Tests.MiscTests

open System
open System.IO
open Microsoft.Data.Sqlite
open Xunit
open Campfire.App
open Campfire.App.Presenters
open Campfire.App.Controllers
open Campfire.App.Tests.Support
open Campfire.Db
open Campfire.Kit
open Campfire.RailsCompat
open Campfire.RailsCompat.Clock
open Campfire.Storage

// --- concerns.rs ---------------------------------------------------------------------------------------------

[<Fact>]
let ``before builders`` () =
    let before = Before.Default |> Before.allowBotAccess |> Before.skipForgeryProtection
    Assert.Equal(Required, before.Authentication)
    Assert.False before.DenyBots
    Assert.False before.ForgeryProtection
    Assert.Equal(RequireUnauthenticated, (Before.requireUnauthenticatedAccess Before.Default).Authentication)

// --- rich_text.rs --------------------------------------------------------------------------------------------

[<Fact>]
let ``a body whose plain text raises has none`` () =
    use loggers = new CapturingLoggers()
    let richText = AppRichText(Secrets.create "test-secret", SystemClock(), loggers.Logger "rich_text") :> Campfire.Db.RichText
    use conn = Conn.OpenInMemory()
    // Rails raises `ArgumentError: invalid base64` reading the mention.
    let html = """Hey <action-text-attachment sgid="!!!" content-type="application/vnd.campfire.mention"></action-text-attachment> there"""
    Assert.Equal("", richText.ToPlainText(conn, html))
    Assert.Equal("Hey there", richText.ToPlainText(conn, "Hey <b>there</b>"))

// --- controllers/sessions.rs ---------------------------------------------------------------------------------------

[<Fact>]
let ``counts within a fixed window`` () =
    let start = DateTimeOffset.Parse "2024-06-01T12:00:00Z"
    let key = "rate-limit:sessions:test-window"
    for expected in 1..11 do
        Assert.Equal(uint64 expected, Sessions.increment key (start.AddSeconds(float expected)))
    // The window started at the first hit and doesn't slide.
    Assert.Equal(1UL, Sessions.increment key (start.AddSeconds 181.0))

// --- active_storage.rs ---------------------------------------------------------------------------------------------

/// A unique index refusing a blob or attachment row inside `User.create!` is rescued as
/// `ActiveRecord::RecordNotUnique`, like the user row's own.
[<Fact>]
let ``storage errors keep sqlites own`` () =
    use connection = new SqliteConnection("Data Source=:memory:")
    connection.Open()
    use setup = connection.CreateCommand()
    setup.CommandText <- "CREATE TABLE blobs (key TEXT UNIQUE); INSERT INTO blobs VALUES ('a')"
    setup.ExecuteNonQuery() |> ignore
    use insert = connection.CreateCommand()
    insert.CommandText <- "INSERT INTO blobs VALUES ('a')"
    let unique = Assert.Throws<SqliteException>(fun () -> insert.ExecuteNonQuery() |> ignore)
    Assert.True(DbError.isRecordNotUnique (ActiveStorage.storageError (StorageError.Sql unique)))
    match ActiveStorage.storageError StorageError.FileNotFound with
    | Other _ -> ()
    | other -> failwith $"{other}"

[<Fact>]
let ``byte ranges are not read into memory`` () =
    task {
        let path = Path.GetTempFileName()
        try
            File.WriteAllBytes(path, Array.init 200_000 (fun i -> byte (i % 256)))
            let range start stop = FileServer.File(path, start, stop)

            match ActiveStorage.partsBody [ range 10UL 199_999UL ] with
            | Body.File body -> Assert.Equal((10L, 199_990L), (body.Offset, body.Len))
            | other -> failwith $"a single range should be a file body, got {other}"

            let parts = [ FileServer.Bytes "<"B; range 0UL 2UL; FileServer.Bytes ">"B; range 100_000UL 170_000UL ]
            let length = ActiveStorage.partsLen parts
            match ActiveStorage.partsBody parts with
            | Body.Stream stream ->
                use stream = stream
                use streamed = new MemoryStream()
                do! stream.CopyToAsync streamed
                let contents = File.ReadAllBytes path
                let expected = Array.concat [ "<"B; contents[0..2]; ">"B; contents[100_000..170_000] ]
                Assert.Equal<byte[]>(expected, streamed.ToArray())
                Assert.Equal(length, uint64 streamed.Length)
            | other -> failwith $"several ranges should stream, got {other}"
        finally
            File.Delete path
    }

[<Fact>]
let ``json times have milliseconds`` () =
    Assert.Equal("2026-03-02T16:00:00.123Z", ActiveStorage.jsonTime "2026-03-02 16:00:00.123456")
    Assert.Equal("2026-03-02T16:00:00.000Z", ActiveStorage.jsonTime "2026-03-02 16:00:00")

// --- main.rs -------------------------------------------------------------------------------------------------------

[<Fact>]
let ``transparent huge pages are disabled`` () =
    if not (OperatingSystem.IsLinux()) then
        Assert.Skip "transparent huge pages are a Linux setting (bin/linux-tests App runs this in the toolchain image)"
    Program.disableTransparentHugePages ()
    Assert.True(Program.transparentHugePagesDisabled ())
