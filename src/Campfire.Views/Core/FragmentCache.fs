// Port of rust/crates/views/src/fragment_cache.rs
//
// Fragment caching: `cache record do ... end` in ERB and `json.cache! record do ... end` in
// Jbuilder. The first rendering of a record version is what every later render reuses, whoever
// renders it: a broadcast renders without a request, and the pages that show the same message
// afterwards repeat that rendering byte for byte. So nothing in a fragment may depend on the
// request unless its key does (a message's "Copy link" carries a path, not the request's host).
//
// `FragmentCache` is the process's store. The reference keeps fragments in Redis
// (`config.cache_store = :redis_cache_store`, `config/environments/production.rb`), whose
// `config/redis.conf` sets no `maxmemory`, so it grows with every message. An in-process store
// can't do that, so this one is bounded by bytes the way Rails bounds its in-process store,
// `ActiveSupport::Cache::MemoryStore`: each entry counts its key, its payload and `PerEntryOverhead`
// bytes, and when a write takes the total past the limit, least recently used entries go until it's
// back to three quarters of it (`MemoryStore#prune`). Reads count as uses. An entry larger than a
// quarter of the limit is returned but not kept, so that one huge fragment can't flush everything
// else. Templates reach the store that's current in the calling context: the app enters it for every
// request (`FragmentCache.scoped`) and for renders outside one (`FragmentCache.withCache`). Without a
// current store, fragments render uncached (`perform_caching = false`).
//
// A fragment's payload is what it keeps alive: its bytes and whatever the page-splicing layer has
// attached to it (the kit's SHA-256 and compressed pieces, `Fragment.HeldBytes`). Rust bounds those
// in a map of the kit's; here they hang off the `Fragment`, so this cache bounds them, by counting
// them in the entry's size each time the entry is used and dropping the whole `Fragment` (and with
// it the attachment) when the entry goes. (Phase 4's requirement: the budget counts the pieces and
// the SHA, and `fragments a page keeps showing stay while the rest age out` holds the total.)
//
// Keys follow `ActionView::Helpers::CacheHelper#fragment_name_with_digest`:
// `views/<template>:<digest>/<record cache_key_with_version>[/<extra>]`, where the digest covers
// the template and the partials it renders (see `FragmentCache.digest`).
namespace Campfire.Views

open System
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Tasks
open Campfire.RailsCompat

/// The store's default limit: `MemoryStore`'s default `size`, 32 MB.
module FragmentCacheLimits =
    [<Literal>]
    let DefaultMaxBytes = 33554432

    /// What an entry costs beyond its key and payload (`MemoryStore::PER_ENTRY_OVERHEAD`).
    [<Literal>]
    let PerEntryOverhead = 240

/// A key under construction, in a buffer every key of a thread reuses: keys are built for every
/// cached record on every request, hit or miss, and must not allocate. A key built while another is
/// in use (a fragment rendering the fragments nested in it) gets a buffer of its own.
[<Sealed; AllowNullLiteral>]
type KeyBuf private () =
    [<ThreadStatic; DefaultValue>]
    static val mutable private spare: KeyBuf

    let mutable chars: char[] = Array.zeroCreate 256
    let mutable length = 0

    static member Rent() : KeyBuf =
        match KeyBuf.spare with
        | null -> KeyBuf()
        | spare ->
            KeyBuf.spare <- null
            spare.Clear()
            spare

    static member Return(key: KeyBuf) : unit = KeyBuf.spare <- key

    /// A key for tests and tools that want one from a string.
    static member Of(text: string) : KeyBuf =
        let key = KeyBuf()
        key.Append text
        key

    member _.Clear() = length <- 0

    member private _.Reserve(count: int) =
        if chars.Length - length < count then Array.Resize(&chars, max (chars.Length * 2) (length + count))

    member this.Append(text: string) : unit =
        this.Reserve text.Length
        text.CopyTo(0, chars, length, text.Length)
        length <- length + text.Length

    member this.Append(c: char) : unit =
        this.Reserve 1
        chars[length] <- c
        length <- length + 1

    /// Appends `value` in decimal, as `{value}` would.
    member this.AppendInteger(value: int64) : unit =
        if value < 0L then this.Append '-'
        let magnitude = if value = Int64.MinValue then 9223372036854775808UL else uint64 (abs value)
        let mutable digits = 1
        let mutable rest = magnitude / 10UL
        while rest > 0UL do
            digits <- digits + 1
            rest <- rest / 10UL
        this.AppendPadded(magnitude, digits)

    /// Appends the last `width` decimal digits of `value`, zero-padded.
    member this.AppendPadded(value: uint64, width: int) : unit =
        this.Reserve width
        let mutable rest = value
        for i in length + width - 1 .. -1 .. length do
            chars[i] <- char (int '0' + int (rest % 10UL))
            rest <- rest / 10UL
        length <- length + width

    member _.Span: ReadOnlySpan<char> = ReadOnlySpan<char>(chars, 0, length)
    member _.Length = length
    override _.ToString() = String(chars, 0, length)

/// One entry: a node of the recency list (the oldest first).
[<Sealed>]
type internal Entry(key: string, value: obj, keyBytes: int, size: int) =
    member _.Key = key
    member _.Value = value
    member _.KeyBytes = keyBytes
    member val Size = size with get, set
    member val Older: Entry | null = null with get, set
    member val Newer: Entry | null = null with get, set

/// A byte-bounded in-process fragment store.
[<Sealed; AllowNullLiteral>]
type FragmentCache(maxBytes: int) =
    let values = Dictionary<string, Entry>(StringComparer.Ordinal)
    let lookup = values.GetAlternateLookup<ReadOnlySpan<char>>()
    let gate = obj ()
    let mutable oldest: Entry | null = null
    let mutable newest: Entry | null = null
    /// The sum of every entry's `Size`.
    let mutable bytes = 0

    let unlink (entry: Entry) =
        match entry.Older with
        | null -> oldest <- entry.Newer
        | older -> older.Newer <- entry.Newer
        match entry.Newer with
        | null -> newest <- entry.Older
        | newer -> newer.Older <- entry.Older
        entry.Older <- null
        entry.Newer <- null

    let append (entry: Entry) =
        entry.Older <- newest
        entry.Newer <- null
        match newest with
        | null -> oldest <- entry
        | last -> last.Newer <- entry
        newest <- entry

    let touch (entry: Entry) =
        if not (obj.ReferenceEquals(entry, newest)) then
            unlink entry
            append entry

    let remove (entry: Entry) =
        values.Remove entry.Key |> ignore
        unlink entry
        bytes <- bytes - entry.Size

    /// `MemoryStore#prune(@max_size * 0.75)`.
    let pruneIfOver () =
        if bytes > maxBytes then
            let target = maxBytes / 4 * 3
            let mutable going = true
            while going && bytes > target do
                match oldest with
                | null -> going <- false
                | entry -> remove entry

    /// A use: the entry is the newest, and a fragment's size is what it holds now (pages splicing it
    /// have attached pieces since it was last used).
    let used (entry: Entry) =
        touch entry
        match entry.Value with
        | :? Fragment as fragment ->
            let size = entry.KeyBytes + fragment.HeldBytes + FragmentCacheLimits.PerEntryOverhead
            if size <> entry.Size then
                bytes <- bytes + (size - entry.Size)
                entry.Size <- size
                pruneIfOver ()
        | _ -> ()

    /// A store that keeps at most `maxBytes` of entries (as `CacheSize` and `PerEntryOverhead` count
    /// them).
    static member DefaultMaxBytes = FragmentCacheLimits.DefaultMaxBytes

    member _.MaxBytes = maxBytes

    member _.Count: int = lock gate (fun () -> values.Count)

    member this.IsEmpty = this.Count = 0

    /// The bytes the entries account for.
    member _.Bytes: int = lock gate (fun () -> bytes)

    member _.Clear() : unit =
        lock gate (fun () ->
            values.Clear()
            oldest <- null
            newest <- null
            bytes <- 0)

    /// The value `key` holds, if it is a `'T` (a use, for eviction).
    member _.TryGet<'T when 'T: not struct>(key: ReadOnlySpan<char>) : 'T voption =
        Monitor.Enter gate
        try
            let mutable entry: Entry = Unchecked.defaultof<Entry>
            if lookup.TryGetValue(key, &entry) then
                match entry.Value with
                | :? 'T as value ->
                    used entry
                    ValueSome value
                | _ -> ValueNone
            else
                ValueNone
        finally
            Monitor.Exit gate

    member this.TryGet<'T when 'T: not struct>(key: string) : 'T voption = this.TryGet<'T>(key.AsSpan())

    /// The fragment `key` holds, or null.
    member this.GetFragment(key: ReadOnlySpan<char>) : Fragment =
        match this.TryGet<Fragment> key with
        | ValueSome fragment -> fragment
        | ValueNone -> null

    /// Stores `value` unless `key` already holds one of its type, and returns what `key` holds. A
    /// value past a quarter of the limit is returned and not kept.
    member _.Write<'T when 'T: not struct>(key: ReadOnlySpan<char>, value: 'T, payloadBytes: int) : 'T =
        let keyBytes = Encoding.UTF8.GetByteCount key
        Monitor.Enter gate
        try
            let mutable existing: Entry = Unchecked.defaultof<Entry>
            let mutable stored = ValueNone
            if lookup.TryGetValue(key, &existing) then
                match existing.Value with
                | :? 'T as found ->
                    used existing
                    stored <- ValueSome found
                | _ -> remove existing
            match stored with
            | ValueSome stored -> stored
            | ValueNone ->
                let size = keyBytes + payloadBytes + FragmentCacheLimits.PerEntryOverhead
                if size > maxBytes / 4 then
                    value
                else
                    let entry = Entry(key.ToString(), nonNull (box value), keyBytes, size)
                    values[entry.Key] <- entry
                    append entry
                    bytes <- bytes + size
                    pruneIfOver ()
                    value
        finally
            Monitor.Exit gate

    member this.Write<'T when 'T: not struct>(key: string, value: 'T, payloadBytes: int) : 'T = this.Write<'T>(key.AsSpan(), value, payloadBytes)

    /// `Rails.cache.fetch(key) { render }` for a rendered fragment, shared with the store.
    member this.Fetch(key: string, render: unit -> Fragment) : Fragment =
        match this.TryGet<Fragment> key with
        | ValueNone ->
            let fragment = render ()
            this.Write(key, fragment, fragment.HeldBytes)
        | ValueSome fragment -> fragment

    /// `Rails.cache.fetch(key) { value }` for any other value (Jbuilder caches the hash it built, not
    /// its JSON); `size` is what it costs as a payload (the length of its JSON).
    member this.FetchValue<'T when 'T: not struct>(key: string, size: 'T -> int, compute: unit -> 'T) : 'T =
        match this.TryGet<'T> key with
        | ValueNone ->
            let value = compute ()
            this.Write(key, value, size value)
        | ValueSome value -> value

    /// `FetchValue` where computing can fail: nothing is stored then. When two renders of a key
    /// race, the first one stored is what both return.
    member this.TryFetchValue<'T, 'E when 'T: not struct>(key: string, size: 'T -> int, compute: unit -> Result<'T, 'E>) : Result<'T, 'E> =
        match this.TryGet<'T> key with
        | ValueNone ->
            // Computed unlocked: a fragment renders the fragments nested in it through this store.
            match compute () with
            | Ok value -> Ok(this.Write(key, value, size value))
            | Error e -> Error e
        | ValueSome value -> Ok value

    /// The recency order of the keys, oldest first (tests).
    member internal _.KeysByRecency: string list =
        lock gate (fun () ->
            let keys = List<string>()
            let mutable at = oldest
            while not (isNull at) do
                let entry = nonNull at
                keys.Add entry.Key
                at <- entry.Newer
            List.ofSeq keys)

    /// What the entries hold as they are now, fragments' attachments included, rather than as they were last
    /// counted (tests).
    member internal _.ActualBytes: int =
        lock gate (fun () ->
            let mutable sum = 0
            let mutable at = oldest
            while not (isNull at) do
                let entry = nonNull at
                sum <-
                    sum
                    + (match entry.Value with
                       | :? Fragment as fragment -> entry.KeyBytes + fragment.HeldBytes + FragmentCacheLimits.PerEntryOverhead
                       | _ -> entry.Size)
                at <- entry.Newer
            sum)

    /// Whether every entry is in the order once and the sizes add up (tests).
    member internal _.Consistent: bool =
        lock gate (fun () ->
            let mutable count = 0
            let mutable sum = 0
            let mutable at = oldest
            while not (isNull at) do
                let entry = nonNull at
                count <- count + 1
                sum <- sum + entry.Size
                at <- entry.Newer
            count = values.Count && sum = bytes)

module FragmentCache =
    let private currentStore = new AsyncLocal<FragmentCache>()

    /// This context's current store, if any.
    let current () : FragmentCache = currentStore.Value

    /// Runs `f` with `cache` as the current store.
    let withCache (cache: FragmentCache) (f: unit -> 'R) : 'R =
        let previous = currentStore.Value
        currentStore.Value <- cache
        try
            f ()
        finally
            currentStore.Value <- previous

    /// Runs `f`, which starts a handler's asynchronous work, with `cache` as the current store for
    /// everything that work runs, on whichever thread it continues (the execution context carries
    /// it): `Scoped`, which makes a request's handler see the store whenever it is polled.
    let scoped (cache: FragmentCache) (f: unit -> Task<'R>) : Task<'R> =
        let previous = currentStore.Value
        currentStore.Value <- cache
        try
            f ()
        finally
            currentStore.Value <- previous

    /// A fragment rendered into a writer of its own, as exact bytes.
    let inline renderFragment ([<InlineIfLambda>] render: Out -> unit) : Fragment =
        let out = Out.Rent 1024
        try
            render out
            Fragment(out.ToArray())
        finally
            Out.Return out

    /// `cache key do render end` against the current store (uncached without one), for the key
    /// `key` writes.
    let inline fetch ([<InlineIfLambda>] key: KeyBuf -> unit) ([<InlineIfLambda>] render: Out -> unit) : Fragment =
        match current () with
        | null -> renderFragment render
        | cache ->
            let buffer = KeyBuf.Rent()
            try
                key buffer
                match cache.GetFragment buffer.Span with
                | null ->
                    let fragment = renderFragment render
                    cache.Write(buffer.Span, fragment, fragment.HeldBytes)
                | fragment -> fragment
            finally
                KeyBuf.Return buffer

    /// The fragment the key `key` writes holds in the current store, if any, shared rather than
    /// copied. For callers that gather a fragment's inputs only on a miss, as `cache key do ... end`
    /// evaluates its block only then.
    let inline read ([<InlineIfLambda>] key: KeyBuf -> unit) : Fragment =
        match current () with
        | null -> null
        | cache ->
            let buffer = KeyBuf.Rent()
            try
                key buffer
                cache.GetFragment buffer.Span
            finally
                KeyBuf.Return buffer

    /// `json.cache! key do ... end` against the current store (uncached without one), for the key
    /// `key` writes. `size` is what a value costs as a payload (`serialized_size`).
    let tryFetchValue<'T, 'E when 'T: not struct>
        (key: KeyBuf -> unit)
        (size: 'T -> int)
        (compute: unit -> Result<'T, 'E>)
        : Result<'T, 'E> =
        match current () with
        | null -> compute ()
        | cache ->
            let buffer = KeyBuf.Rent()
            try
                key buffer
                match cache.TryGet<'T> buffer.Span with
                | ValueNone ->
                    match compute () with
                    | Ok value -> Ok(cache.Write(buffer.Span, value, size value))
                    | Error e -> Error e
                | ValueSome value -> Ok value
            finally
                KeyBuf.Return buffer

    /// The template digest part of a key: a stable hash of the template sources a fragment renders
    /// (`ActionView::Digestor` digests the template and its dependency tree). Only its stability
    /// within the process matters: the store doesn't outlive it, and ETags hash the fragments'
    /// content, not their keys. The sources here are the names of the template modules a fragment
    /// renders (the templates are compiled in, and change only with the process).
    let digest (sources: string[]) : string =
        // FNV-1a over the UTF-8 of each source and a separator.
        let mutable hash = 14695981039346656037UL
        for source in sources do
            for b in Encoding.UTF8.GetBytes source do
                hash <- (hash ^^^ uint64 b) * 1099511628211UL
            hash <- (hash ^^^ 0xFFUL) * 1099511628211UL
        hash.ToString("x16")

    let private epochTicks = DateTimeOffset.UnixEpoch.UtcTicks

    /// Appends `cacheVersion` to `key`: `%Y%m%d%H%M%S` and six digits of microseconds, in UTC.
    let pushCacheVersion (key: KeyBuf) (updatedAt: Timestamp) : unit =
        let utc = updatedAt.UtcDateTime
        let sinceEpoch = updatedAt.UtcTicks - epochTicks
        // The sub-second part has the timestamp's sign (jiff's `subsec_microsecond`).
        let microseconds = (sinceEpoch % 10_000_000L) / 10L
        key.AppendPadded(uint64 utc.Year, 4)
        key.AppendPadded(uint64 utc.Month, 2)
        key.AppendPadded(uint64 utc.Day, 2)
        key.AppendPadded(uint64 utc.Hour, 2)
        key.AppendPadded(uint64 utc.Minute, 2)
        key.AppendPadded(uint64 utc.Second, 2)
        if microseconds < 0L then
            // Before 1970 `{:06}` prints the negative sub-second part with its sign inside the width;
            // no record is that old, so this just keeps those bytes the same.
            key.Append '-'
            let magnitude = uint64 -microseconds
            key.AppendPadded(magnitude, (if magnitude >= 100000UL then 6 else 5))
        else
            key.AppendPadded(uint64 microseconds, 6)

    /// `Time#to_fs(:usec)` of a record's `updated_at`: its `cache_version`.
    let cacheVersion (updatedAt: Timestamp) : string =
        let key = KeyBuf.Of ""
        pushCacheVersion key updatedAt
        key.ToString()

    /// Appends `record.cache_key_with_version` to `key`.
    let pushCacheKeyWithVersion (key: KeyBuf) (table: string) (id: int64) (updatedAt: Timestamp) : unit =
        key.Append table
        key.Append '/'
        key.AppendInteger id
        key.Append '-'
        pushCacheVersion key updatedAt

    /// `record.cache_key_with_version`: `"messages/1-20240601120000000000"`.
    let cacheKeyWithVersion (table: string) (id: int64) (updatedAt: Timestamp) : string =
        let key = KeyBuf.Of ""
        pushCacheKeyWithVersion key table id updatedAt
        key.ToString()

    /// Appends a record fragment's key, `views/<template>:<digest>/<record cache_key_with_version>`,
    /// to `key` (`CacheHelper#fragment_name_with_digest`).
    let pushRecordFragmentKey (key: KeyBuf) (template: string) (digest: string) (table: string) (id: int64) (updatedAt: Timestamp) : unit =
        key.Append "views/"
        key.Append template
        key.Append ':'
        key.Append digest
        key.Append '/'
        pushCacheKeyWithVersion key table id updatedAt
