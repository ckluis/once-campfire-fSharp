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
