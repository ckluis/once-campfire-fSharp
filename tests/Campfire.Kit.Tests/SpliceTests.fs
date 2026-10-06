// Ports of the #[cfg(test)] module in rust/crates/kit/src/deflater/splice.rs (its `Generations`
// test is in DeflaterTests, beside the cache it tests)
module Campfire.Kit.Tests.SpliceTests

open System
open System.Buffers
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open Xunit
open Campfire.Kit

let private gunzip (bytes: byte[]) : byte[] =
    use input = new MemoryStream(bytes)
    use gzip = new GZipStream(input, CompressionMode.Decompress)
    use output = new MemoryStream()
    gzip.CopyTo output
    output.ToArray()

let private utf8 (s: string) = Encoding.UTF8.GetBytes s
let private hex (bytes: byte[]) = Convert.ToHexStringLower bytes

let private storedPieces (fragment: Fragment) : FragmentPiece[] =
    match fragment.Known with
    | null -> failwith "a known fragment"
    | known -> known.Pieces

let private message (n: int) : Fragment =
    let body = String.replicate (40 + n % 7) "<button>Boost</button> hello there "
    Fragment("<div id=\"message_" + string n + "\" class=\"message\">" + body + "</div>\n")

let private same (a: obj | null) (b: obj | null) = Object.ReferenceEquals(a, b)

/// A page as a template records it (its text, and where each fragment went), and the body it stands for.
type private Page =
    { Plain: string
      Text: string
      Fragments: (int * Fragment) list }

    static member Empty = { Plain = ""; Text = ""; Fragments = [] }

    member this.AddText(text: string) = { this with Plain = this.Plain + text; Text = this.Text + text }

    member this.AddFragment(fragment: Fragment) =
        { this with
            Plain = this.Plain + Encoding.UTF8.GetString fragment.Bytes
            Fragments = this.Fragments @ [ this.Text.Length, fragment ] }

    member this.Parts() : PageParts =
        let fragments = this.Fragments |> List.map (fun (offset, f) -> struct (offset, f)) |> ResizeArray
        match PageParts.Splice(ReadOnlyMemory<byte>(utf8 this.Text), fragments) with
        | Ok parts -> parts
        | Error _ -> failwith "a fragment big enough for a part"

    member this.Gzip(mtime: uint32) : byte[] = this.Parts().Gzip mtime
    member this.Etag() : string = this.Parts().Etag()

let private page (head: string) (messages: Fragment list) (tail: string) : Page =
    (messages |> List.fold (fun (page: Page) message -> (page.AddText "  ").AddFragment message) (Page.Empty.AddText head)).AddText tail

/// What `WritePlain` writes.
let private plain (parts: PageParts) : byte[] =
    let writer = ArrayBufferWriter<byte>()
    parts.WritePlain writer
    writer.WrittenSpan.ToArray()

[<Fact>]
let ``decodes to the body and reuses pieces`` () =
    let messages = [ for n in 0..29 -> message n ]
    let page = page "<html><head>layout</head><body>" messages "</body></html>"
    let gz = page.Gzip 1234u
    Assert.Equal<byte[]>(utf8 page.Plain, gunzip gz)
    Assert.Equal<byte[]>(BitConverter.GetBytes 1234u, gz[4..7])
    Assert.Equal(3uy, gz[9])
    let piece = (storedPieces messages[5])[0]
    Assert.Equal<byte[]>(gz, page.Gzip 1234u)
    let again = storedPieces messages[5]
    Assert.True(same piece again[0], "the same page is the same stored pieces")

[<Fact>]
let ``a fragment reports what it keeps alive for the fragment cache's budget`` () =
    let messages = [ for n in 0..9 -> message n ]
    let fresh = messages[5]
    Assert.Equal(0, fresh.HeldBytes)
    let page = page "<html><head>layout</head><body>" messages "</body></html>"
    page.Gzip 1234u |> ignore
    let held = fresh.HeldBytes
    let pieces = storedPieces fresh
    // Its SHA-256 and each piece with its glue, not its own bytes.
    Assert.True(held >= 32 + (pieces |> Array.sumBy (fun p -> p.Piece.Deflated.Length)), $"{held} bytes held")
    Assert.True(held < fresh.Length, "the pieces are compressed")
    // Spliced after another neighbour it gains a piece.
    let other = Page.Empty.AddText("<p>").AddFragment(messages[0]).AddFragment(fresh).AddText "</p>"
    other.Gzip 1234u |> ignore
    Assert.True(fresh.HeldBytes > held)

[<Fact>]
let ``the plain body is the page`` () =
    let messages = [ for n in 900..909 -> message n ]
    let small = Fragment "<i>small</i>"
    let page =
        (page "<p>" messages[0..4] "").AddFragment(small).AddText(String.replicate (SpliceLimits.MaxGlue + 1) "x")
        |> fun p -> p.AddFragment(messages[5]).AddFragment(messages[6]).AddText "</p>"
    let parts = page.Parts()
    Assert.Equal(page.Plain.Length, parts.BodyLength)
    Assert.Equal<byte[]>(utf8 page.Plain, plain parts)

/// The ETags these pages got when they were split by searching the body for its fragments, before the
/// fragments' offsets were recorded. (Rust's test also pins the SHA-256 of the gzip members flate2
/// made; .NET's deflate makes other bytes that decode to the same body, so those are checked by
/// decoding and by being the same each time.)
[<Fact>]
let ``the same etags as when split by searching`` () =
    let messages = [ for n in 0..29 -> message n ]
    let whole = page "<html><head>layout</head><body>" messages "</body></html>"
    Assert.Equal("79e220089f196fbabb4a04d3a545749e", whole.Etag())
    Assert.Equal<byte[]>(utf8 whole.Plain, gunzip (whole.Gzip 7u))
    Assert.Equal<byte[]>(whole.Gzip 7u, whole.Gzip 7u)

    let small = Fragment "<i>small</i>"
    let mixed =
        Page.Empty
            .AddText("<p>")
            .AddFragment(messages[0])
            .AddText(String.replicate 300 "x")
            .AddFragment(messages[1])
            .AddText("  ")
            .AddFragment(small)
            .AddText("  ")
            .AddFragment(messages[2])
            .AddText("</p>")
    Assert.Equal("ca77a29a2de60cdd36f203de61f1f9cb", mixed.Etag())
    Assert.Equal<byte[]>(utf8 mixed.Plain, gunzip (mixed.Gzip 7u))

[<Fact>]
let ``a changed layout or neighbour decodes correctly`` () =
    let messages = [ for n in 100..109 -> message n ]
    (page "<p>" messages "</p>").Gzip 0u |> ignore
    for page in [ page "<p>changed" messages "</p>"; page "<p>" messages "</p>changed" ] do
        Assert.Equal<byte[]>(utf8 page.Plain, gunzip (page.Gzip 0u))
    // Drop one message: the one after it now follows a different predecessor.
    let fewer = messages |> List.removeAt 4
    let page' = page "<p>" fewer "</p>"
    Assert.Equal<byte[]>(utf8 page'.Plain, gunzip (page'.Gzip 0u))
    // Different glue between the same fragments.
    let glued = messages |> List.fold (fun (p: Page) m -> (p.AddText "\n    ").AddFragment m) (Page.Empty.AddText "<p>")
    Assert.Equal<byte[]>(utf8 glued.Plain, gunzip (glued.Gzip 0u))

[<Fact>]
let ``small repeated and far apart fragments`` () =
    let messages = [ for n in 200..205 -> message n ]
    let small = Fragment "<i>small</i>"
    let withSmall = (page "<p>" messages[0..0] "  ").AddFragment small
    let withSmall = messages[1..2] |> List.fold (fun (p: Page) m -> (p.AddText "  ").AddFragment m) withSmall |> fun p -> p.AddText "</p>"
    Assert.Equal<byte[]>(utf8 withSmall.Plain, gunzip (withSmall.Gzip 0u))
    // A small fragment is text to the parts.
    let asText = page "<p>" messages[0..0] ("  " + Encoding.UTF8.GetString small.Bytes)
    let asText = messages[1..2] |> List.fold (fun (p: Page) m -> (p.AddText "  ").AddFragment m) asText |> fun p -> p.AddText "</p>"
    Assert.Equal(withSmall.Etag(), asText.Etag())
    let onlySmall = Page.Empty.AddText("<p>").AddFragment(small).AddText "</p>"
    match PageParts.Splice(ReadOnlyMemory<byte>(utf8 onlySmall.Text), ResizeArray(onlySmall.Fragments |> List.map (fun (o, f) -> struct (o, f)))) with
    | Ok _ -> failwith "no part"
    | Error whole -> Assert.Equal<byte[]>(utf8 onlySmall.Plain, whole.ToArray())

    let a, b = message 300, message 301
    let page' =
        Page.Empty
            .AddFragment(a)
            .AddFragment(b)
            .AddFragment(a)
            .AddText(String.replicate (SpliceLimits.MaxGlue + 1) "x")
            .AddFragment(b)
            .AddFragment(a)
    for _ in 0..1 do
        Assert.Equal<byte[]>(utf8 page'.Plain, gunzip (page'.Gzip 0u))

[<Fact>]
let ``a fragment keeps a piece for each predecessor`` () =
    let messages = [ for n in 600..605 -> message n ]
    let room = page "<p>" messages "</p>"
    // The same last message after a different one, as in search results.
    let search = page "<q>" [ messages[1]; messages[5] ] "</q>"
    room.Gzip 0u |> ignore
    search.Gzip 0u |> ignore
    let before = storedPieces messages[5]
    Assert.Equal(2, before.Length) // one after message 604, one after 601
    room.Gzip 0u |> ignore
    search.Gzip 0u |> ignore
    let after = storedPieces messages[5]
    Assert.True(Array.forall2 same before after, "both pages reuse theirs")

[<Fact>]
let ``a piece follows its predecessors bytes not its object`` () =
    let messages = [ for n in 800..802 -> message n ]
    let body = page "<p>" messages "</p>"
    body.Gzip 0u |> ignore
    let stored = storedPieces messages[1]
    // The first message rendered again into a new `Fragment`, with the same bytes.
    let rerendered = page "<p>" [ Fragment(Array.copy messages[0].Bytes); messages[1]; messages[2] ] "</p>"
    Assert.Equal<byte[]>(utf8 rerendered.Plain, gunzip (rerendered.Gzip 0u))
    let after = storedPieces messages[1]
    Assert.Equal(1, after.Length) // no second piece for the same predecessor
    Assert.True(same stored[0] after[0], "the piece after it is reused")

[<Fact>]
let ``a fragment keeps at most four pieces and the oldest goes first`` () =
    // Rust bounds the pieces it remembers in a map keyed by fragment address, aging whole fragments
    // out; here they hang off the fragment, so what is bounded is each fragment's own.
    let target = message 950
    let predecessors = [ for n in 951..958 -> message n ]
    for predecessor in predecessors do
        (page "<p>" [ predecessor; target ] "</p>").Gzip 0u |> ignore
    let kept = storedPieces target
    Assert.Equal(SpliceLimits.PiecesPerFragment, kept.Length)
    let predecessorShas = predecessors[4..] |> List.map (fun p -> p.Known.Sha)
    Assert.Equal<BodyDigest list>(predecessorShas, kept |> Array.map (fun p -> p.Before.Sha) |> List.ofArray)

[<System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)>]
let private pageOfAFragmentThatGoesAway () : WeakReference<Fragment> =
    let fragment = message 970
    (page "<p>" [ fragment; message 971 ] "</p>").Gzip 0u |> ignore
    WeakReference<Fragment>(fragment)

[<Fact>]
let ``what a fragment remembers goes with it`` () =
    let weak = pageOfAFragmentThatGoesAway ()
    for _ in 0..3 do
        GC.Collect()
        GC.WaitForPendingFinalizers()
    let mutable fragment: Fragment = null
    Assert.False(weak.TryGetTarget(&fragment), "nothing keeps a fragment's pieces alive past it")

[<Fact>]
let ``etags follow the content`` () =
    let messages = [ for n in 400..409 -> message n ]
    let body = page "<p>" messages "</p>"
    Assert.Equal(body.Etag(), (page "<p>" messages "</p>").Etag())
    Assert.Equal(32, body.Etag().Length)
    Assert.NotEqual<string>(body.Etag(), (page "<p>" messages "</p>!").Etag())
    Assert.NotEqual<string>(body.Etag(), (page "<q>" messages "</p>").Etag())
    let glued = messages |> List.fold (fun (p: Page) m -> (p.AddText " ").AddFragment m) (Page.Empty.AddText "<p>") |> fun p -> p.AddText "</p>"
    Assert.NotEqual<string>(body.Etag(), glued.Etag())

[<Fact>]
let ``stays close to whole body compression`` () =
    let messages = [ for n in 500..539 -> message n ]
    let page = page (String.replicate 200 "<head>layout</head>") messages (String.replicate 300 "<footer/>")
    use whole = new MemoryStream()
    (
        use gzip = new GZipStream(whole, CompressionLevel.Optimal, true)
        gzip.Write(utf8 page.Plain)
    )
    let spliced = (page.Gzip 0u).Length
    // Each piece costs its flush marker and block header, not a recompressed message.
    Assert.True(
        spliced < int whole.Length + 40 * (messages.Length + 2),
        $"{spliced} bytes spliced vs {whole.Length} whole"
    )

[<Fact>]
let ``a large one-off text is served but not remembered`` () =
    // Incompressible, so its piece is as large as the text: over both stores' bounds.
    let size = max SpliceLimits.MaxStoredText SpliceLimits.MaxStoredTextPiece + 1
    let text = Array.zeroCreate<byte> size
    let mutable state = 0x9e3779b97f4a7c15UL
    for i in 0 .. size - 1 do
        state <- state * 6364136223846793005UL + 1442695040888963407UL
        text[i] <- byte (state >>> 56)
    let fragment = message 990
    let parts =
        match PageParts.Splice(ReadOnlyMemory<byte> text, ResizeArray [ struct (text.Length, fragment) ]) with
        | Ok parts -> parts
        | Error _ -> failwith "a part"
    Assert.Equal<byte[]>(Array.append text fragment.Bytes, gunzip (parts.Gzip 0u))
    let hash = SHA256.HashData text
    let sha: BodyDigest =
        { A = BitConverter.ToUInt64(hash, 0)
          B = BitConverter.ToUInt64(hash, 8)
          C = BitConverter.ToUInt64(hash, 16)
          D = BitConverter.ToUInt64(hash, 24) }
    let remembered = lock SpliceCaches.textShasGate (fun () -> SpliceCaches.textShas.Get({ Memory = ReadOnlyMemory<byte> text }, ignore))
    Assert.True(remembered.IsNone, "hashed, not remembered")
    let stored =
        lock SpliceCaches.textPiecesGate (fun () -> SpliceCaches.textPieces.Get({ Text = sha; Before = Before.nothing }, ignore))
    Assert.True(stored.IsNone, "compressed, not stored")

[<Fact>]
let ``a text part is its texts sha256 the first time and after`` () =
    let text = Array.concat (List.replicate 50 (utf8 "<html><head>layout</head><body>"))
    for text in [ text[0..9]; text[5..]; text ] do
        let expected = SHA256.HashData text
        for _ in 0..1 do
            let sha = SpliceCaches.textSha (ReadOnlyMemory<byte> text)
            Assert.Equal<byte[]>(
                expected,
                Array.concat [ BitConverter.GetBytes sha.A; BitConverter.GetBytes sha.B; BitConverter.GetBytes sha.C; BitConverter.GetBytes sha.D ]
            )

/// Hashes every key alike, as texts whose fast hashes collide.
[<Sealed>]
type private Colliding() =
    interface IEqualityComparer<TextKey> with
        member _.Equals(a, b) = a.Memory.Span.SequenceEqual b.Memory.Span
        member _.GetHashCode _ = 0

[<Fact>]
let ``a text is known by its bytes not its fast hash`` () =
    // A small budget, so texts also age out and come back through the old generation.
    let shas =
        Generations<TextKey, TextSha>(
            8 * 1024,
            (fun key _ -> key.Memory.Length + CacheCosts.EntryOverhead),
            Colliding(),
            (fun _ stored -> stored.Owned)
        )
    let gate = obj ()
    // The same length, all in one bucket.
    let texts = [ for n in 0..29 -> utf8 (String.replicate 40 $"<h1>Room {n:D2}</h1>") ]
    for _ in 0..2 do
        for text in texts do
            let sha = SpliceCaches.textShaIn gate shas (ReadOnlyMemory<byte> text)
            let hash = SHA256.HashData text
            Assert.Equal(BitConverter.ToUInt64(hash, 0), sha.A)
            let found = lock gate (fun () -> shas.Get({ Memory = ReadOnlyMemory<byte> text }, (fun stored -> stored.Sha)))
            Assert.Equal(ValueSome sha, found) // remembered as its own

[<Fact>]
let ``stored pieces keep only what they use`` () =
    let text = utf8 (String.replicate 100_000 "<p>repeated</p>")
    let piece = Compress.deflate ReadOnlySpan<byte>.Empty ReadOnlySpan<byte>.Empty (ReadOnlySpan<byte> text)
    // An array of exactly the compressed bytes, not the buffer they were produced in.
    Assert.True(piece.Length < 64 * 1024, $"{piece.Length} bytes for {text.Length}")

[<Fact>]
let ``a piece compressed against a dictionary needs it to decode`` () =
    // The dictionary trick (.NET's DeflateStream takes no preset dictionary): the piece's back
    // references reach into the dictionary, so decoding the dictionary's own bytes first gives the text.
    let dictionary = utf8 (String.replicate 200 "<button>Boost</button> hello there ")
    let text = utf8 (String.replicate 50 "<button>Boost</button> hello there ")
    let alone = Compress.deflate ReadOnlySpan<byte>.Empty ReadOnlySpan<byte>.Empty (ReadOnlySpan<byte> text)
    let chained = Compress.deflate (ReadOnlySpan<byte> dictionary) ReadOnlySpan<byte>.Empty (ReadOnlySpan<byte> text)
    Assert.True(chained.Length <= alone.Length, $"{chained.Length} chained vs {alone.Length} alone")
    // gzip member: the dictionary's pieces, then this one.
    let first = Compress.deflate ReadOnlySpan<byte>.Empty ReadOnlySpan<byte>.Empty (ReadOnlySpan<byte> dictionary)
    use output = new MemoryStream()
    output.Write(ReadOnlySpan<byte>([| 0x1fuy; 0x8buy; 8uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 3uy |]))
    output.Write(ReadOnlySpan<byte> first)
    output.Write(ReadOnlySpan<byte> chained)
    output.Write(ReadOnlySpan<byte>([| 3uy; 0uy |]))
    let crc = Crc32.concatenated (ReadOnlySpan<Crc>([| Crc32.ofBytes (ReadOnlySpan<byte> dictionary); Crc32.ofBytes (ReadOnlySpan<byte> text) |]))
    output.Write(ReadOnlySpan<byte>(BitConverter.GetBytes crc))
    output.Write(ReadOnlySpan<byte>(BitConverter.GetBytes(uint32 (dictionary.Length + text.Length))))
    Assert.Equal<byte[]>(Array.append dictionary text, gunzip (output.ToArray()))

[<Fact>]
let ``crcs concatenate`` () =
    let bytes = Array.init 100_000 (fun n -> byte ((uint32 n * 2654435761u) >>> 24))
    let cutsets: int list list = [ []; [ 0; 0; 1 ]; [ 1; 2; 3; 4; 5 ]; [ 1000; 1001; 70_000; 99_999; 100_000 ] ]
    for cuts in cutsets do
        let bounds = [ 0 ] @ cuts @ [ bytes.Length ]
        let crcs = bounds |> List.pairwise |> List.map (fun (a, b) -> Crc32.ofBytes (ReadOnlySpan<byte>(bytes, a, b - a))) |> Array.ofList
        Assert.Equal(Deflater.crc32Update 0u (ReadOnlySpan<byte> bytes), Crc32.concatenated (ReadOnlySpan<Crc> crcs))

[<Fact>]
let ``the carry-less multiplication agrees with the bitwise one`` () =
    // `multiply` is the instruction version where the CPU has one, `multiplySlowly` is zlib's `multmodp`.
    let random = Random 20261005
    let values = Array.zeroCreate<byte> 4
    let next () =
        random.NextBytes values
        BitConverter.ToUInt32(values, 0)
    let edge = [ 0u; 1u; 0x80000000u; 0xFFFFFFFFu; 0xedb88320u; 0x40000000u ]
    for a in edge do
        for b in edge do
            Assert.Equal(Crc32.multiplySlowly a b, Crc32.multiplyFast a b)
    for _ in 1..200_000 do
        let a, b = next (), next ()
        Assert.Equal(Crc32.multiplySlowly a b, Crc32.multiplyFast a b)

[<Fact>]
let ``texts that hash alike are told apart`` () =
    // The fast hash covers every byte, whatever the length: texts of every length around the lane and
    // word steps, differing in their last byte, hash differently (a collision here would be bad luck,
    // not a bug, but none of these should collide).
    let hashes = System.Collections.Generic.HashSet<int>()
    for length in [ 0; 1; 7; 8; 9; 31; 32; 33; 40; 100; 1000; 40000 ] do
        let text = Array.init length (fun i -> byte (i * 7))
        Assert.True(hashes.Add(TextKey.Hash(ReadOnlySpan<byte> text)))
        if length > 0 then
            let changed = Array.copy text
            changed[length - 1] <- changed[length - 1] + 1uy
            Assert.NotEqual(TextKey.Hash(ReadOnlySpan<byte> text), TextKey.Hash(ReadOnlySpan<byte> changed))

[<Fact>]
let ``texts that share a recent slot each get their own sha256`` () =
    // The same length and the same first and last bytes, so the same slot: only a compare of the whole
    // text tells them apart, as it must (a stored piece is found by the SHA-256).
    let make (middle: byte) = Array.concat [ "AAAAAAAA"B; Array.create 100 middle; "BBBBBBBB"B ]
    let one, two = make 1uy, make 2uy
    let sha (bytes: byte[]) =
        let hash = SHA256.HashData bytes
        BitConverter.ToUInt64(hash, 0)
    for _ in 1..4 do
        Assert.Equal(sha one, (SpliceCaches.textSha (ReadOnlyMemory<byte> one)).A)
        Assert.Equal(sha two, (SpliceCaches.textSha (ReadOnlyMemory<byte> two)).A)
