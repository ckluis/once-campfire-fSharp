// Port of the #[cfg(test)] modules of rust/crates/storage/src: filename.rs, disk.rs, json.rs,
// analyze.rs and storage.rs.
module Campfire.Storage.Tests.UnitTests

open System
open System.IO
open Xunit
open Campfire.RailsCompat
open Campfire.Storage

// --- filename.rs ---------------------------------------------------------------------------------

[<Fact>]
let ``filename matches ruby file semantics`` () =
    let cases =
        [ ("foo.", ".", "foo")
          ("a.b.", ".", "a.b")
          (".bashrc", "", ".bashrc")
          ("..", "", "..")
          ("...", "", "...")
          (".a.b", ".b", ".a")
          ("a..b", ".b", "a.")
          ("a/b.c/d", "", "d")
          ("a.b/", ".b", "a")
          ("x.tar.gz", ".gz", "x.tar")
          ("a/.b", "", ".b")
          (".", "", ".")
          ("", "", "")
          ("\u00e9.png", ".png", "\u00e9")
          ("a. b", ". b", "a") ]
    Assert.Equal(15, cases.Length)
    for name, ext, baseName in cases do
        let filename = Filename.create name
        Assert.True((Filename.extensionWithDelimiter filename = ext), $"extname({name})")
        Assert.True((Filename.baseName filename = baseName), $"base({name})")

// --- disk.rs -------------------------------------------------------------------------------------

[<Fact>]
let ``an upload with a checksum is verified`` () =
    use root = TempDir.Create()
    let service = DiskService.create root.Path "local"
    let upload (key: string) (data: string) (checksum: string option) =
        use reader = new MemoryStream(Text.Encoding.UTF8.GetBytes data)
        DiskService.upload service key reader checksum
    let checksum (data: string) = Key.checksum (Text.Encoding.UTF8.GetBytes data)

    Assert.Equal(Ok(), upload "matching" "bytes" (Some(checksum "bytes")))
    Assert.Equal<byte[]>("bytes"B, (DiskService.download service "matching").Value)

    match upload "mismatched" "bytes" (Some(checksum "other bytes")) with
    | Error StorageError.Integrity -> ()
    | other -> failwith $"{other}"
    Assert.False(DiskService.exist service "mismatched")

    Assert.Equal(Ok(), upload "unchecked" "bytes" None)
    Assert.Equal<byte[]>("bytes"B, (DiskService.download service "unchecked").Value)

// --- json.rs -------------------------------------------------------------------------------------

[<Fact>]
let ``json leaves js line separators raw`` () =
    // ActiveSupport::JSON.encode and ActiveRecord::Coders::JSON in the reference.
    Assert.Equal("\"a\u2028b\u2029c\\u003c\\u003e\\u0026\"", JsonValue.encode (Value.String "a\u2028b\u2029c<>&"))

[<Fact>]
let ``json preserves order and escapes like active support`` () =
    let json = (JsonValue.parse """{"z":1,"a":[true,null,2.0,1e16,1e-5],"s":"<a & b>"}""").Value
    Assert.Equal("""{"z":1,"a":[true,null,2.0,1e+16,0.00001],"s":"\u003ca \u0026 b\u003e"}""", JsonValue.encode json)

// --- analyze.rs ----------------------------------------------------------------------------------

[<Fact>]
let ``video metadata matches the analyzer`` () =
    let probe =
        (JsonValue.parse
            """{"streams":[{"codec_type":"video","width":320,"height":180,"display_aspect_ratio":"16:9","duration":"65.840000"}],"format":{"duration":"65.9"}}""")
            .Value
    Assert.Equal(
        """{"width":320.0,"height":180.0,"duration":65.84,"display_aspect_ratio":[16,9],"audio":false,"video":true}""",
        JsonValue.encode (Analyze.videoMetadata probe).Value
    )

[<Fact>]
let ``rotated video swaps dimensions`` () =
    let probe =
        (JsonValue.parse
            """{"streams":[{"codec_type":"video","width":1920,"height":1080,"side_data_list":[{"side_data_type":"Display Matrix","rotation":-90}]},{"codec_type":"audio"}],"format":{"duration":"3.5"}}""")
            .Value
    Assert.Equal(
        """{"width":1080.0,"height":1920.0,"duration":3.5,"angle":-90,"audio":true,"video":true}""",
        JsonValue.encode (Analyze.videoMetadata probe).Value
    )

// --- storage.rs ----------------------------------------------------------------------------------

/// `b"partial".chain(Broken)`: yields its bytes, then fails.
type private Broken(prefix: byte[]) =
    inherit Stream()
    let mutable position = 0
    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())
    override _.Position with get () = raise (NotSupportedException()) and set _ = raise (NotSupportedException())
    override _.Flush() = ()
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())

    override _.Read(buffer: byte[], offset: int, count: int) =
        if position < prefix.Length then
            let n = min count (prefix.Length - position)
            Array.blit prefix position buffer offset n
            position <- position + n
            n
        else
            raise (IOException "the disk went away")

[<Fact>]
let ``a copy that fails leaves no file behind`` () =
    use root = TempDir.Create()
    let verifier = RailsCompat.appVerifier (Secrets.create "test") "ActiveStorage"
    let storage = Storage.create (DiskService.create root.Path "local") verifier
    let blob = NewBlob.unfurl (Text.Encoding.UTF8.GetBytes "partial and more") (Filename.create "a.txt") None "local" true
    use reader = new Broken(Text.Encoding.UTF8.GetBytes "partial")

    match Storage.stage storage blob reader with
    | Error(StorageError.Io error) -> Assert.Equal("the disk went away", error.Message)
    | other -> failwith $"a broken copy was staged: {other}"
    Assert.False(DiskService.exist storage.Service blob.Key)

// --- What Rust leaves to the app's tests: the paths through Storage that fail --------------------

let private testStorage (root: TempDir) : Storage =
    Storage.create (DiskService.create root.Path "local") (RailsCompat.appVerifier (Secrets.create "test") "ActiveStorage")

let private at = (Timestamps.tryParse "2026-09-26T12:00:00Z").Value

[<Fact>]
let ``opening a blob whose file is missing is file not found`` () =
    use root = TempDir.Create()
    use conn = openDatabase ()
    let storage = testStorage root
    let blob = (Storage.createAndUpload storage conn "bytes"B (Filename.create "a.txt") None at).Value
    DiskService.delete storage.Service blob.Key |> ignore
    match Storage.openBlob storage blob with
    | Error StorageError.FileNotFound -> ()
    | other -> failwith $"{other}"

[<Fact>]
let ``opening a blob whose file changed is an integrity error`` () =
    use root = TempDir.Create()
    use conn = openDatabase ()
    let storage = testStorage root
    let blob = (Storage.createAndUpload storage conn "bytes"B (Filename.create "a.txt") None at).Value
    File.WriteAllText(Storage.pathFor storage blob, "other bytes")
    match Storage.openBlob storage blob with
    | Error StorageError.Integrity -> ()
    | other -> failwith $"{other}"

[<Fact>]
let ``an opened blob is a copy named for its id and extension`` () =
    use root = TempDir.Create()
    use conn = openDatabase ()
    let storage = testStorage root
    let blob = (Storage.createAndUpload storage conn "bytes"B (Filename.create "a.txt") None at).Value
    let copy = (Storage.openBlob storage blob).Value
    let path = copy.Path
    let name = nonNull (Path.GetFileName path)
    Assert.True(name.StartsWith $"ActiveStorage-{blob.Id}-" && name.EndsWith ".txt", name)
    Assert.Equal<byte[]>("bytes"B, File.ReadAllBytes path)
    (copy :> IDisposable).Dispose()
    Assert.False(File.Exists path)

[<Fact>]
let ``a blob is stored by its key and described by its row`` () =
    use root = TempDir.Create()
    use conn = openDatabase ()
    let storage = testStorage root
    let blob = (Storage.createAndUpload storage conn "hello"B (Filename.create "hello.txt") None at).Value
    Assert.Equal(Some "text/plain", blob.ContentType)
    Assert.Equal("2026-09-26 12:00:00", blob.CreatedAt)
    Assert.Equal(Some "XUFAKrxLKna5cZ2REBfFkg==", blob.Checksum)
    Assert.Equal(Path.Combine(root.Path, blob.Key.Substring(0, 2), blob.Key.Substring(2, 2), blob.Key), Storage.pathFor storage blob)
    Assert.Equal(Some blob, (Blob.findByKey conn blob.Key).Value)
    Assert.Equal(Some blob, (Blob.find conn blob.Id).Value)
    Assert.Equal("{\"identified\":true}", JsonValue.encode blob.Metadata)

[<Fact>]
let ``timestamps are written as Active Record writes them`` () =
    let at = (Timestamps.tryParse "2026-09-26T12:00:00.5Z").Value
    Assert.Equal("2026-09-26 12:00:00.500000", Blob.formatTimestamp at)
    Assert.Equal("2026-09-26 12:00:00", Blob.formatTimestamp (at.AddMilliseconds -500.0))

[<Fact>]
let ``a variant record is inserted once`` () =
    use root = TempDir.Create()
    use conn = openDatabase ()
    let storage = testStorage root
    let blob = (Storage.createAndUpload storage conn "bytes"B (Filename.create "a.txt") None at).Value
    let first = (Blob.insertVariantRecord conn blob.Id "digest").Value
    Assert.True first.IsSome
    Assert.Equal(None, (Blob.insertVariantRecord conn blob.Id "digest").Value)
    Assert.Equal(first, (Blob.findVariantRecord conn blob.Id "digest").Value)

[<Fact>]
let ``folders come from the first four characters of the key`` () =
    let service = DiskService.create "/root" "local"
    Assert.Equal("/root/ab/cd/abcdef", DiskService.pathFor service "abcdef")
    Assert.Equal("/root/ab/c/abc", DiskService.pathFor service "abc")
    Assert.Equal("/root/a/a", DiskService.pathFor service "a")
    Assert.Equal("/root/ab/ab", DiskService.pathFor service "ab")
    // The cut is by bytes, as Rust's `str::get` makes it: whole characters stay whole, and a cut that
    // would split one finds no folder.
    Assert.Equal("/root/\u00e9/xy/\u00e9xyz", DiskService.pathFor service "\u00e9xyz")
    Assert.Equal("/root/\u00e9/\u00e9/\u00e9\u00e9\u00e9\u00e9", DiskService.pathFor service "\u00e9\u00e9\u00e9\u00e9")
    Assert.Equal("/root/x\u00e9yz/x\u00e9yz", DiskService.pathFor service "x\u00e9yz")

[<Fact>]
let ``deleting a prefix removes what starts with it`` () =
    use root = TempDir.Create()
    let service = DiskService.create root.Path "local"
    for key in [ "variants/abc/one"; "variants/abc/two"; "variants/abd/other" ] do
        use reader = new MemoryStream("x"B)
        Assert.Equal(Ok(), DiskService.upload service key reader None)
    Assert.Equal(Ok(), DiskService.deletePrefixed service "variants/abc/")
    Assert.False(DiskService.exist service "variants/abc/one")
    Assert.False(DiskService.exist service "variants/abc/two")
    Assert.True(DiskService.exist service "variants/abd/other")

// --- sql.rs's stand-in for rusqlite -----------------------------------------------------------------

/// Microsoft.Data.Sqlite retries a busy statement for CommandTimeout (30 s by default) after SQLite's
/// busy handler gives up; rusqlite doesn't, so a locked database fails after busy_timeout.
[<Fact>]
let ``a locked database fails after the busy timeout, not after the command timeout`` () =
    let path = Path.Combine(Path.GetTempPath(), $"campfire-storage-busy-{Guid.NewGuid():N}.sqlite3")
    try
        let open' () =
            let conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False")
            conn.Open()
            conn
        use holder = open' ()
        use waiter = open' ()
        let run (conn: Microsoft.Data.Sqlite.SqliteConnection) (sql: string) =
            use command = conn.CreateCommand()
            command.CommandText <- sql
            command.ExecuteNonQuery() |> ignore
        run holder "CREATE TABLE things (id INTEGER PRIMARY KEY)"
        run waiter "PRAGMA busy_timeout = 100"
        run holder "BEGIN IMMEDIATE"
        let started = Diagnostics.Stopwatch.StartNew()
        let result = Sql.execute waiter "INSERT INTO things (id) VALUES (?1)" [| box 1L |]
        let elapsed = started.Elapsed
        run holder "ROLLBACK"
        match result with
        | Error(StorageError.Sql(:? Microsoft.Data.Sqlite.SqliteException as e)) -> Assert.Equal(5, e.SqliteErrorCode) // SQLITE_BUSY
        | other -> failwith $"expected SQLITE_BUSY, got {other}"
        Assert.True(elapsed < TimeSpan.FromSeconds 5.0, $"a locked statement took {elapsed.TotalSeconds:F1} s to fail")
    finally
        for suffix in [ ""; "-wal"; "-shm"; "-journal" ] do
            try
                File.Delete(path + suffix)
            with _ ->
                ()
