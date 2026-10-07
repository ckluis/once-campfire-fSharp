// What `Campfire.App` does between the views and the kit (Phase 5), done here once so the contract between
// them is tested now: a page rendered with `Render.page` is a text and the fragments it recorded, the kit's
// `PageParts.Splice` takes those, and the kit's SHA and pieces for each fragment hang off the view fragment
// and are counted by the fragment cache. `Campfire.Views` doesn't reference `Campfire.Kit` (nor does
// rust/crates/views reference the kit); only this test project does.
module Campfire.Views.Tests.KitIntegrationTests

open System
open System.Buffers
open System.IO
open System.IO.Compression
open System.Text
open Xunit
open Campfire.Views

/// The kit's side of a view fragment: what `Fragment.Attach` holds, found again for every page.
type private KitLink(fragment: Campfire.Kit.Fragment) =
    member _.Fragment = fragment
    interface IFragmentAttachment with
        member _.HeldBytes = fragment.HeldBytes

/// The kit fragment of a view fragment, made the first time a page splices it.
let private kitFragment (fragment: Fragment) : Campfire.Kit.Fragment =
    match fragment.Attach(KitLink(Campfire.Kit.Fragment fragment.Bytes)) with
    | :? KitLink as link -> link.Fragment
    | _ -> failwith "nothing else is attached"

let private splice (page: RecordedPage) : Campfire.Kit.PageParts =
    let fragments = page.Fragments |> Array.map (fun struct (offset, fragment) -> struct (offset, kitFragment fragment))
    match Campfire.Kit.PageParts.Splice(ReadOnlyMemory<byte> page.Text, fragments) with
    | Ok parts -> parts
    | Error _ -> failwith "a page of messages has fragments big enough for parts"

let private gunzip (bytes: byte[]) : byte[] =
    use input = new MemoryStream(bytes)
    use gzip = new GZipStream(input, CompressionMode.Decompress)
    use output = new MemoryStream()
    gzip.CopyTo output
    output.ToArray()

/// A message's partial, as the fragment cache holds it: ~3 KB of markup that depends on the message alone.
let private message (n: int) (w: Out) : unit =
    w.Raw $"<div id=\"message_{n}\" class=\"message\">"
    for i in 1..40 do
        w.Raw "<span class=\"word\">hello "
        w.Text $"there <{n}> & {i}"
        w.Raw "</span> "
    w.Raw "</div>\n"

/// A room page: a layout around the messages the cache hands over.
let private roomPage (cache: FragmentCache) (ids: int list) : RecordedPage =
    FragmentCache.withCache cache (fun () ->
        Render.page 0 (fun w ->
            w.Raw "<html><head><title>Room</title></head><body><div id=\"messages\">\n"
            for n in ids do
                w.Fragment(FragmentCache.fetch (fun key -> key.Append $"messages/{n}") (message n))
            w.Raw "</div></body></html>\n"))

[<Fact>]
let ``a recorded room page splices into the kits page parts and is the page`` () =
    let cache = FragmentCache FragmentCacheLimits.DefaultMaxBytes
    let ids = [ 1..30 ]
    let page = roomPage cache ids
    Assert.Equal(30, page.Fragments.Length)
    let parts = splice page
    let whole = page.ToArray()
    Assert.Equal(whole.Length, parts.BodyLength)
    let plain = ArrayBufferWriter<byte>()
    parts.WritePlain plain
    Assert.Equal<byte[]>(whole, plain.WrittenSpan.ToArray())
    Assert.Equal<byte[]>(whole, gunzip (parts.Gzip 1234u))
    // The next request for the page is the same fragments, so the same ETag and the same stored pieces.
    let again = splice (roomPage cache ids)
    Assert.Equal(parts.Etag(), again.Etag())
    Assert.Equal<byte[]>(parts.Gzip 1234u, again.Gzip 1234u)
    // A page with one more message is another page.
    Assert.True(parts.Etag() <> (splice (roomPage cache [ 1..31 ])).Etag())

[<Fact>]
let ``the cache counts the kits pieces and SHA, and a pages fragments stay while the rest age out`` () =
    // Room for ~20 messages with the pieces the kit keeps for them.
    let budget = 100 * 1024
    let cache = FragmentCache budget
    let page = [ 1..8 ]
    for round in 1..40 do
        // The page is shown (and gzipped, which stores the pieces), then other rooms' messages churn.
        let parts = splice (roomPage cache page)
        parts.Gzip 1234u |> ignore
        parts.Etag() |> ignore
        for n in 1000 + round * 20 .. 1000 + round * 20 + 19 do
            FragmentCache.withCache cache (fun () -> FragmentCache.fetch (fun key -> key.Append $"messages/{n}") (message n)) |> ignore
        Assert.True(cache.Bytes <= budget, $"{cache.Bytes} bytes counted, over the budget of {budget}")
    for n in page do
        let fragment = FragmentCache.withCache cache (fun () -> FragmentCache.read (fun key -> key.Append $"messages/{n}"))
        Assert.NotNull fragment
        Assert.True(fragment.HeldBytes > fragment.Length / 4, "with the kit's SHA and pieces counted in")
    Assert.True(cache.Consistent)
    // What is really held, as the pieces have grown since the entries were last used, is within the budget
    // but for one splice's worth of new pieces on the page's fragments.
    let growth = page.Length * 4 * 700
    Assert.True(cache.ActualBytes <= budget + growth, $"{cache.ActualBytes} bytes really held")
