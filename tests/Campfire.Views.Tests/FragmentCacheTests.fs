// Ports of the #[cfg(test)] module in rust/crates/views/src/fragment_cache.rs, and the Phase 4
// requirement of the plan: the cache's budget counts what each fragment keeps alive for the kit (its
// compressed pieces and its SHA), not only its own bytes.
module Campfire.Views.Tests.FragmentCacheTests

open System
open System.Globalization
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open Campfire.RailsCompat
open Campfire.Views

let private big = 1 <<< 20
let private overhead = FragmentCacheLimits.PerEntryOverhead

let private fragment (size: int) (c: char) : Fragment = Fragment(String(c, size))

/// What a one-letter key holding `payload` bytes costs.
let private entry (payload: int) : int = 1 + payload + overhead

let private time (text: string) : Timestamp = (Timestamps.tryParse text).Value

/// The keys built with the framework's formatting, which the digit-by-digit ones must match byte for
/// byte (`mod formatted` in the Rust tests): `{:06}` puts a negative sub-second part's sign inside the width.
module private Formatted =
    let private epoch = DateTimeOffset.UnixEpoch.UtcTicks

    let cacheVersion (updatedAt: Timestamp) : string =
        let micros = ((updatedAt.UtcTicks - epoch) % 10_000_000L) / 10L
        let padded =
            if micros < 0L then "-" + (-micros).ToString(CultureInfo.InvariantCulture).PadLeft(5, '0')
            else micros.ToString(CultureInfo.InvariantCulture).PadLeft(6, '0')
        updatedAt.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + padded

    let cacheKeyWithVersion (table: string) (id: int64) (updatedAt: Timestamp) : string =
        $"{table}/{id}-{cacheVersion updatedAt}"

/// Timestamps with the calendar's edges and every sub-second precision. (jiff's `MIN`/`MAX` and the
/// year -1 are before `DateTimeOffset`'s range, and no record is that old.)
let private timestamps: Timestamp list =
    [ "1970-01-01T00:00:00Z"
      "1970-01-01T00:00:00.000001Z"
      "1999-12-31T23:59:59.999999Z"
      "2000-02-29T12:34:56.5Z"
      "2024-02-29T23:59:59.000999Z"
      "2024-06-01T12:00:00.000123Z"
      "2024-06-01T12:00:00.000123456Z"
      "2024-12-31T23:59:59.1Z"
      "2038-01-19T03:14:08Z"
      "2100-03-01T00:00:00.00001Z"
      "9999-12-30T21:59:59.999999999Z"
      "1969-12-31T23:59:59.5Z"
      "1900-01-01T00:00:00Z"
      "0001-01-01T00:00:00.25Z"
      "2024-11-03T01:30:00.123456-07:00" ]
    |> List.map time

[<Fact>]
let ``keys match their formatted versions byte for byte`` () =
    let ids = [| 0L; 1L; 7L; 10L; 99L; 100L; 12_345L; 1_000_000_000L; Int64.MaxValue; -1L; Int64.MinValue |]
    timestamps
    |> List.iteri (fun i at ->
        Assert.Equal(Formatted.cacheVersion at, FragmentCache.cacheVersion at)
        let id = ids[i % ids.Length]
        Assert.Equal(Formatted.cacheKeyWithVersion "messages" id at, FragmentCache.cacheKeyWithVersion "messages" id at)
        let key = KeyBuf.Of "existing/"
        FragmentCache.pushRecordFragmentKey key "messages/_message" "0123456789abcdef" "messages" id at
        Assert.Equal(
            "existing/views/messages/_message:0123456789abcdef/" + Formatted.cacheKeyWithVersion "messages" id at,
            key.ToString()))

[<Fact>]
let ``the first rendering is reused`` () =
    let cache = FragmentCache big
    Assert.Equal("first", (cache.Fetch("a", fun () -> Fragment "first")).ToString())
    Assert.Equal("first", (cache.Fetch("a", fun () -> Fragment "second")).ToString())
    Assert.Equal("other", (cache.Fetch("b", fun () -> Fragment "other")).ToString())

[<Fact>]
let ``entries count their key payload and overhead`` () =
    let cache = FragmentCache big
    cache.Fetch("a", fun () -> fragment 100 'x') |> ignore
    cache.Fetch("bb", fun () -> fragment 10 'y') |> ignore
    Assert.Equal((1 + 100 + overhead) + (2 + 10 + overhead), cache.Bytes)
    cache.Fetch("a", fun () -> failwith "unreachable") |> ignore
    Assert.Equal(entry 100 + (2 + 10 + overhead), cache.Bytes)
    cache.Clear()
    Assert.Equal(0, cache.Bytes)

[<Fact>]
let ``the byte limit is enforced`` () =
    let max = 10 * entry 1000
    let cache = FragmentCache max
    for i in 0..999 do
        cache.Fetch(string (i % 3), fun () -> fragment 1000 'x') |> ignore
        cache.Fetch($"k{i}", fun () -> fragment 1000 'x') |> ignore
        Assert.True(cache.Bytes <= max, $"{cache.Bytes} bytes after {i} writes")
    Assert.True(cache.Count < 20)
    Assert.True(cache.Bytes > max / 2, "pruning stops at three quarters, not empty")
    for hot in 0..2 do
        Assert.True((cache.TryGet<Fragment>(string hot)).IsSome, $"entry {hot} is used every sixth write")
    Assert.True cache.Consistent

[<Fact>]
let ``going over prunes to three quarters least recently used first`` () =
    let cache = FragmentCache(4 * entry 100)
    for key in [ "a"; "b"; "c"; "d" ] do
        cache.Fetch(key, fun () -> fragment 100 'x') |> ignore
    cache.Fetch("a", fun () -> failwith "a is still stored") |> ignore
    Assert.Equal(4, cache.Count)
    // Five entries is over; three quarters of the limit holds three.
    cache.Fetch("e", fun () -> fragment 100 'x') |> ignore
    Assert.Equal(3, cache.Count)
    Assert.True(cache.Bytes <= 3 * entry 100)
    Assert.True((cache.TryGet<Fragment> "b").IsNone, "b was least recently used")
    Assert.True((cache.TryGet<Fragment> "c").IsNone, "c went next")
    for key in [ "a"; "d"; "e" ] do
        Assert.True((cache.TryGet<Fragment> key).IsSome, $"{key} survives (a was used after d)")

[<Fact>]
let ``stored fragments hold no spare capacity`` () =
    // A writer reserves room for a page up front; the fragment kept is exactly what was written.
    let cache = FragmentCache big
    let stored =
        FragmentCache.withCache cache (fun () ->
            FragmentCache.fetch (fun key -> key.Append "a") (fun w ->
                w.Raw(String(' ', 0))
                w.Raw "<p>hi</p>"))
    Assert.Equal("<p>hi</p>".Length, stored.Bytes.Length)
    Assert.Equal(entry "<p>hi</p>".Length, cache.Bytes)

[<Fact>]
let ``a value larger than the store is returned but not kept`` () =
    let cache = FragmentCache(entry 10)
    Assert.Equal(String('x', 100), (cache.Fetch("a", fun () -> fragment 100 'x')).ToString())
    Assert.Equal(0, cache.Count)
    Assert.Equal(0, cache.Bytes)

[<Fact>]
let ``a value larger than a quarter of the store doesnt evict the rest`` () =
    let cache = FragmentCache(8 * entry 100)
    for key in [ "a"; "b"; "c" ] do
        cache.Fetch(key, fun () -> fragment 100 'x') |> ignore
    let bigText = String('y', 3 * entry 100)
    Assert.Equal(bigText, (cache.Fetch("big", fun () -> Fragment bigText)).ToString())
    Assert.Equal(3, cache.Count)
    Assert.True((cache.TryGet<Fragment> "big").IsNone)
    for key in [ "a"; "b"; "c" ] do
        Assert.True((cache.TryGet<Fragment> key).IsSome, $"{key} is still stored")
    Assert.Equal(3 * entry 100, cache.Bytes)

[<Fact>]
let ``concurrent use stays within the limit`` () =
    let max = 64 * entry 200
    let cache = FragmentCache max
    let work t =
        Task.Run(fun () ->
            for i in 0..4999 do
                let hot = cache.Fetch(string (i % 8), fun () -> fragment 200 'x')
                Assert.Equal(String('x', 200), hot.ToString())
                cache.Fetch($"cold/{t}/{i}", fun () -> fragment 200 'y') |> ignore
                Assert.True(cache.Bytes <= max))
    Task.WaitAll [| for t in 0..7 -> work t |]
    Assert.True(cache.Count > 0 && cache.Bytes <= max)
    Assert.True(cache.Consistent, "every entry is in the recency order once and the sizes add up")

[<Fact>]
let ``racing renders all return the first stored fragment`` () =
    let cache = FragmentCache big
    use barrier = new Barrier 8
    let results =
        Task.WhenAll [| for t in 0..7 -> Task.Run(fun () -> cache.Fetch("k", fun () -> barrier.SignalAndWait(); Fragment(string t))) |]
        |> fun task -> task.Result
    Assert.All(results, fun value -> Assert.Same(results[0], value))
    Assert.Same(results[0], (cache.TryGet<Fragment> "k").Value)
    Assert.Equal(1, cache.Count)

[<Fact>]
let ``jbuilder values count their json`` () =
    let value = Value.Array [ Value.String "ab"; Value.String "c" ]
    Assert.Equal("[\"ab\",\"c\"]".Length, MessagesJson.serializedSize value)

/// A key writer for the key `name`.
let private named (name: string) : KeyBuf -> unit = fun key -> key.Append name

[<Fact>]
let ``nested fragments use the same store`` () =
    let cache = FragmentCache big
    let outer =
        FragmentCache.withCache cache (fun () ->
            FragmentCache.fetch (named "outer") (fun w ->
                w.Raw "["
                w.Fragment(FragmentCache.fetch (named "inner") (fun w -> w.Raw "x"))
                w.Raw "]"))
    Assert.Equal("[x]", outer.ToString())
    Assert.Equal(2, cache.Count)
    Assert.Equal("[x]", (cache.TryGet<Fragment> "outer").Value.ToString())
    Assert.Equal("x", (cache.TryGet<Fragment> "inner").Value.ToString())
    Assert.Null(FragmentCache.current ())
    Assert.Equal("uncached", (FragmentCache.fetch (named "outer") (fun w -> w.Raw "uncached")).ToString())

[<Fact>]
let ``fragments can be looked up before rendering`` () =
    let cache = FragmentCache big
    Assert.Null(FragmentCache.withCache cache (fun () -> FragmentCache.read (named "a")))
    FragmentCache.withCache cache (fun () -> FragmentCache.fetch (named "ab") (fun w -> w.Raw "rendered")) |> ignore
    Assert.Equal("rendered", (FragmentCache.withCache cache (fun () -> FragmentCache.read (named "ab"))).ToString())
    Assert.Null(FragmentCache.withCache cache (fun () -> FragmentCache.read (named "a")))
    Assert.Null(FragmentCache.read (named "ab"))

[<Fact>]
let ``failures are not stored`` () =
    let cache = FragmentCache big
    let size (_: string) = 4
    Assert.True((cache.TryFetchValue<string, string>("k", size, fun () -> Error "boom")).IsError)
    Assert.Equal(Ok "1", cache.TryFetchValue<string, string>("k", size, fun () -> Ok "1"))
    Assert.Equal(Ok "1", cache.TryFetchValue<string, string>("k", size, fun () -> Ok "2"))

[<Fact>]
let ``keys use usec versions`` () =
    let at = time "2024-06-01T12:00:00.000123Z"
    Assert.Equal("messages/1-20240601120000000123", FragmentCache.cacheKeyWithVersion "messages" 1L at)

[<Fact>]
let ``the store follows a request across its awaits`` () =
    let cache = FragmentCache big
    let seen =
        (FragmentCache.scoped cache (fun () ->
            task {
                do! Task.Delay 5
                return not (isNull (FragmentCache.current ()))
            }))
            .Result
    Assert.True seen
    Assert.Null(FragmentCache.current ())

// The plan's Phase 4 requirement. Rust holds a fragment's SHA-256 and compressed pieces in a bounded
// map of the kit's, and its test `fragments_a_page_keeps_showing_stay_while_the_rest_age_out` holds the
// total at or under the budget while 300 other fragments churn. Here the kit's SHA and pieces hang off
// the fragment (`Fragment.Attach`), so the cache's budget must count them.

/// What the kit attaches to a fragment a page splices: a SHA-256 and a piece for each predecessor the
/// fragment has been seen with (at most four), about as big as the fragment compressed.
type private Pieces(fragmentBytes: int) =
    let mutable pieces = 0
    member _.AddPiece() = if pieces < 4 then pieces <- pieces + 1
    interface IFragmentAttachment with
        member _.HeldBytes = 32 + pieces * (fragmentBytes / 4 + 64)

/// A page's fragments spliced once more after a different neighbour: each gains a piece.
let private splice (fragments: Fragment list) =
    for fragment in fragments do
        match fragment.Attach(Pieces fragment.Length) with
        | :? Pieces as pieces -> pieces.AddPiece()
        | _ -> failwith "the test attaches nothing else"

[<Fact>]
let ``fragments a page keeps showing stay while the rest age out, pieces and SHA included`` () =
    let size = 2000
    // Room for a few dozen fragments.
    let budget = 64 * 1024
    let cache = FragmentCache budget
    let keys = [ for i in 900..902 -> $"page/{i}" ]
    let page = keys |> List.map (fun key -> cache.Fetch(key, fun () -> fragment size 'p'))
    splice page
    let others = [ for n in 1000..1299 -> $"other/{n}" ]
    for (n, key) in List.indexed others do
        cache.Fetch(key, fun () -> fragment size 'o') |> ignore
        if n % 10 = 0 then
            // The page, shown again: its fragments are used, and the kit splices them after another neighbour.
            for (key, original) in List.zip keys page do
                Assert.Same(original, cache.Fetch(key, fun () -> failwith $"{key} was dropped"))
            splice page
        Assert.True(cache.Bytes <= budget, $"{cache.Bytes} bytes held, over the budget of {budget}")
        // What is really held, pieces and SHAs as they have grown since the entries were last used, is
        // within the budget but for what one splice of the page adds.
        let growth = page.Length * (size / 4 + 64)
        Assert.True(cache.ActualBytes <= budget + growth, $"{cache.ActualBytes} bytes really held")
    for (key, original) in List.zip keys page do
        match cache.TryGet<Fragment> key with
        | ValueSome now -> Assert.Same(original, now)
        | ValueNone -> failwith $"{key} is shown on every page and stays"
    Assert.True((cache.TryGet<Fragment> others[0]).IsNone, "one not seen again ages out")
    Assert.True cache.Consistent
    // The pieces and SHAs are counted: the page's fragments cost more than their bytes.
    let pagesCost = page |> List.sumBy (fun fragment -> fragment.HeldBytes)
    Assert.True(pagesCost > page.Length * size, $"{pagesCost} bytes held by the page's fragments, expected pieces and SHAs among them")
    Assert.True(cache.Bytes >= pagesCost, "and the cache counts what they hold")

[<Fact>]
let ``a fragments pieces are counted when it is next used, and go with it`` () =
    let cache = FragmentCache(20 * entry 1000)
    let first = cache.Fetch("a", fun () -> fragment 1000 'x')
    Assert.Equal(entry 1000, cache.Bytes)
    splice [ first ]
    Assert.Equal(entry 1000, cache.Bytes)
    // Used again, the entry costs what the fragment now holds.
    let again = cache.Fetch("a", fun () -> failwith "stored")
    Assert.Same(first, again)
    Assert.Equal(entry first.HeldBytes, cache.Bytes)
    Assert.True(first.HeldBytes > 1000)
    // Churn pushes it out, pieces and all.
    for i in 0..99 do
        cache.Fetch($"k{i}", fun () -> fragment 1000 'y') |> ignore
    Assert.True((cache.TryGet<Fragment> "a").IsNone)
    Assert.True(cache.Consistent)
