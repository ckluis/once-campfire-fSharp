// Port of rust/crates/kit/src/front/cache.rs
//
// Thruster's response cache: `internal/cache_handler.go`, `cacheable_response.go`,
// `memory_cache.go` and `variant.go`.
//
// GET and HEAD responses that say `public` with a positive `s-max-age` (sic) or `max-age`, and no
// `no-cache`, are kept in memory until they expire and served again with `X-Cache: hit`, to any
// client whose request has the same method, path, query, host and `Vary`ing headers. Everything
// else passes through with `X-Cache: miss`, or `X-Cache: bypass` for requests that can't be
// cached at all.
//
// Unlike Thruster, an entry's size includes its key and bookkeeping, so CACHE_SIZE bounds the
// memory the cache holds, and requests with very long URIs aren't cached at all: otherwise a
// stream of cacheable URIs padded with junk query parameters would be charged for their small
// responses while holding their large keys.
namespace Campfire.Kit

open System
open System.Collections.Concurrent
open Microsoft.AspNetCore.Http

module FrontCacheLimits =
    /// The longest path and query a cacheable request may have. Campfire's own cacheable URLs
    /// (assets, avatars, QR codes) are far shorter.
    [<Literal>]
    let MaxCacheableUri = 2048

    /// What an entry costs beyond its key and response: the map slot, the entry and the cached
    /// response, and the key's place in the eviction list.
    [<Literal>]
    let EntryOverhead = 256

/// A response as the cache keeps it (`CacheableResponse`).
[<Sealed; AllowNullLiteral>]
type CachedResponse
    (status: int, headers: struct (string * string)[], body: ReadOnlyMemory<byte>, variant: struct (string * string)[]) =
    let etag =
        let mutable found: string | null = null
        for struct (name, value) in headers do
            if isNull found && name.Equals("etag", StringComparison.OrdinalIgnoreCase) then found <- value
        found
    let vary =
        let mutable found = ""
        for struct (name, value) in headers do
            if found = "" && name.Equals("vary", StringComparison.OrdinalIgnoreCase) then found <- value
        found

    member _.Status = status

    /// The headers, each value of a repeated one on a line of its own.
    member _.Headers = headers
    member _.Body = body

    /// The request's values of the headers the response `Vary`s on, when it was stored.
    member _.Variant = variant

    /// The first `ETag` value, or null.
    member _.Etag: string | null = etag

    /// The first `Vary` value, or "".
    member _.Vary: string = vary

    /// How much of the cache it takes up stored under `key`: its body, headers, variant, the key and
    /// the entry's overhead.
    member _.Size(key: string) : int =
        let mutable total = body.Length + key.Length + FrontCacheLimits.EntryOverhead
        for struct (n, v) in headers do
            total <- total + n.Length + v.Length
        for struct (n, v) in variant do
            total <- total + n.Length + v.Length
        total

[<Sealed; AllowNullLiteral>]
type internal CacheEntry(value: CachedResponse, expiresAt: int64, size: int64, lastAccessedAt: int64) =
    /// Written by readers without a lock: a stale value only makes eviction's sampling a little less exact.
    member val LastAccessedAt = lastAccessedAt with get, set
    member _.ExpiresAt = expiresAt
    member _.Value = value
    member _.Size = size

/// `MemoryCache`: a size-bounded map that evicts by sampling. Times are milliseconds on any
/// monotonic clock (the handler's is `Environment.TickCount64`); lookups don't take the lock.
[<Sealed>]
type MemoryCache(capacity: int64, maxItemSize: int64) =
    let gate = obj ()
    let items = ConcurrentDictionary<string, CacheEntry>(StringComparer.Ordinal)
    /// The keys again, for sampling.
    let keys = ResizeArray<string>()
    let mutable size = 0L

    /// Samples 5 random items and evicts the least recently used, or the first expired one found.
    member private _.EvictOldestItem(now: int64) =
        let mutable oldest = -1
        let mutable oldestAt = 0L
        let mutable expiredFound = false
        let mutable sampled = 0
        while not expiredFound && sampled < 5 do
            let index = Random.Shared.Next keys.Count
            let item = items[keys[index]]
            if item.ExpiresAt < now then
                oldest <- index
                expiredFound <- true
            elif oldest < 0 || item.LastAccessedAt < oldestAt then
                oldest <- index
                oldestAt <- item.LastAccessedAt
            sampled <- sampled + 1
        let key = keys[oldest]
        keys[oldest] <- keys[keys.Count - 1]
        keys.RemoveAt(keys.Count - 1)
        match items.TryRemove key with
        | true, item -> size <- size - item.Size
        | _ -> ()

    member _.Get(key: string, now: int64) : CachedResponse =
        match items.TryGetValue key with
        | true, item when item.ExpiresAt >= now ->
            item.LastAccessedAt <- now
            item.Value
        | _ -> null

    member this.Set(key: string, value: CachedResponse, expiresAt: int64, now: int64) : unit =
        let itemSize = int64 (value.Size key)
        if itemSize <= maxItemSize && itemSize <= capacity then
            lock gate (fun () ->
                let limit = capacity - itemSize
                while size > limit && keys.Count > 0 do
                    this.EvictOldestItem now
                match items.TryGetValue key with
                | true, existing -> size <- size - existing.Size
                | _ -> keys.Add key
                items[key] <- CacheEntry(value, expiresAt, itemSize, now)
                size <- size + itemSize)

    /// How much the entries take up, for tests.
    member _.Size = lock gate (fun () -> size)

    /// The keys held, for tests.
    member _.Keys = lock gate (fun () -> keys.ToArray())

module FrontCache =
    /// The target as the client sent it, split at `?`: `(path, query, authority)`, as the router and
    /// the params parser see it, still %-encoded (Thruster keys on the decoded path, which would give
    /// `/a%2Fb` and `/a/b` one entry, and on the query re-encoded by Go's `url.Values.Encode`, which
    /// drops any pair containing `;` that Rack keeps).
    let splitTarget (rawTarget: string | null) : struct (string * string * string | null) =
        let target =
            match rawTarget with
            | null
            | "" -> "/"
            | target -> target
        let mutable authority: string | null = null
        let mutable rest = target
        if rest[0] <> '/' && rest <> "*" then
            // Absolute form: `http://host/path?query`.
            match rest.IndexOf("://", StringComparison.Ordinal) with
            | -1 -> ()
            | at ->
                let afterScheme = rest.Substring(at + 3)
                match afterScheme.IndexOf '/' with
                | -1 ->
                    authority <- afterScheme
                    rest <- "/"
                | slash ->
                    authority <- afterScheme.Substring(0, slash)
                    rest <- afterScheme.Substring slash
        match rest.IndexOf '?' with
        | -1 -> struct (rest, "", authority)
        | q -> struct (rest.Substring(0, q), rest.Substring(q + 1), authority)

    /// `shouldCacheRequest`: GET or HEAD, not an upgrade, not a range; and (unlike Thruster) not a
    /// very long URI. `uriLength` is the length of the path and query.
    let shouldCacheRequest (meth: string) (connection: string | null) (upgrade: string | null) (range: string | null) (uriLength: int) : bool =
        let allowedMethod = String.Equals(meth, "GET", StringComparison.Ordinal) || String.Equals(meth, "HEAD", StringComparison.Ordinal)
        let isUpgrade = String.Equals(connection, "Upgrade", StringComparison.Ordinal) || String.Equals(upgrade, "websocket", StringComparison.Ordinal)
        let isRange = not (String.IsNullOrEmpty range)
        allowedMethod && not isUpgrade && not isRange && uriLength <= FrontCacheLimits.MaxCacheableUri

    /// `\bprefix(\d+)\b`, leftmost: `matched` says whether anything matched, and the value is the
    /// digits as a number (none if they don't fit).
    let private maxAge (cacheControl: string) (prefix: string) : struct (bool * int64 voption) =
        let isWord (c: char) = Char.IsAsciiLetterOrDigit c || c = '_'
        let mutable result = struct (false, ValueNone)
        let mutable from = 0
        let mutable searching = true
        while searching && from <= cacheControl.Length - prefix.Length do
            match cacheControl.IndexOf(prefix, from, StringComparison.Ordinal) with
            | -1 -> searching <- false
            | i ->
                let digitsAt = i + prefix.Length
                let mutable stop = digitsAt
                while stop < cacheControl.Length && Char.IsAsciiDigit cacheControl[stop] do
                    stop <- stop + 1
                let boundedBefore = i = 0 || not (isWord cacheControl[i - 1])
                let boundedAfter = stop >= cacheControl.Length || not (isWord cacheControl[stop])
                if boundedBefore && stop > digitsAt && boundedAfter then
                    let parsed =
                        match Int64.TryParse(cacheControl.AsSpan(digitsAt, stop - digitsAt), Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
                        | true, seconds -> ValueSome seconds
                        | _ -> ValueNone
                    result <- struct (true, parsed)
                    searching <- false
                else
                    from <- i + 1
        result

    /// `CacheStatus` (before the body's size is known): how long the response may be cached, if at
    /// all. `cacheControl` and `vary` are the first values of those headers, or "".
    let cacheLifetime (status: int) (cacheControl: string) (vary: string) : TimeSpan voption =
        if status < 200 || status > 399 || status = 304 then
            ValueNone
        elif vary.Contains '*' then
            ValueNone
        elif not (Deflater.hasWord cacheControl "public") || Deflater.hasWord cacheControl "no-cache" then
            ValueNone
        else
            let struct (sMatched, sSeconds) = maxAge cacheControl "s-max-age="
            let struct (matched, seconds) = if sMatched then struct (sMatched, sSeconds) else maxAge cacheControl "max-age="
            match matched, seconds with
            | true, ValueSome seconds when seconds > 0L -> ValueSome(TimeSpan.FromSeconds(float seconds))
            | _ -> ValueNone

    /// `wasNotModified`: the request's `If-None-Match` names the cached `ETag`.
    let wasNotModified (cached: CachedResponse) (ifNoneMatch: string | null) : bool =
        match cached.Etag, ifNoneMatch with
        | null, _
        | _, null -> false
        | "", _ -> false
        | etag, ifNoneMatch ->
            let mutable found = false
            for candidate in ifNoneMatch.Split ',' do
                if candidate.Trim() = etag then found <- true
            found

/// `Variant`: the cache key for a request, given the headers its response varies on.
[<Sealed; AllowNullLiteral>]
type Variant(meth: string, path: string, query: string, host: string, requestHeaders: IHeaderDictionary) =
    let baseKey = String.Concat(meth, "\n", path, "\n", query, "\n", host)
    let mutable names: string[] = Array.empty

    /// A request's variant. `rawTarget` is the target as the client sent it.
    static member OfRequest(meth: string, rawTarget: string | null, headers: IHeaderDictionary) : Variant =
        let struct (path, query, authority) = FrontCache.splitTarget rawTarget
        let host =
            match headers.Host.ToString() with
            | "" -> (match authority with null -> "" | a -> a)
            | host -> host
        Variant(meth, path, query, host, headers)

    /// The key of a request before its response's `Vary` is known: its method, path, query and host.
    static member BaseKey(meth: string, rawTarget: string | null, headers: IHeaderDictionary) : string =
        let struct (path, query, authority) = FrontCache.splitTarget rawTarget
        Variant.BaseKeyOf(meth, path, query, authority, headers)

    /// `BaseKey` for a target already split by `FrontCache.splitTarget`.
    static member BaseKeyOf(meth: string, path: string, query: string, authority: string | null, headers: IHeaderDictionary) : string =
        let host =
            match headers.Host.ToString() with
            | "" -> (match authority with null -> "" | a -> a)
            | host -> host
        String.Concat(meth, "\n", path, "\n", query, "\n", host)

    /// The request's first value of a header, or "".
    member private _.RequestValue(name: string) : string =
        let values = requestHeaders[name]
        if values.Count = 0 then "" else (match values[0] with null -> "" | value -> value)

    /// `SetResponseHeader`: vary on the response's `Vary` names (canonical, sorted). `vary` is the
    /// first value of the response's `Vary` header, or "".
    member _.SetResponseHeaders(vary: string) : unit =
        names <-
            if vary = "" then
                Array.empty
            else
                let names = vary.Split ',' |> Array.map (fun n -> n.Trim().ToLowerInvariant())
                Array.Sort(names, StringComparer.Ordinal)
                names

    member this.CacheKey() : string =
        if names.Length = 0 then
            baseKey
        else
            let key = Text.StringBuilder(baseKey)
            for name in names do
                key.Append('\n').Append(name).Append('=').Append(this.RequestValue name) |> ignore
            key.ToString()

    /// Whether a stored response's variant headers match this request's.
    member this.Matches(variant: struct (string * string)[]) : bool =
        let mutable all = true
        for name in names do
            let mutable stored = ""
            for struct (n, v) in variant do
                if stored = "" && n = name then stored <- v
            if not (String.Equals(stored, this.RequestValue name, StringComparison.Ordinal)) then all <- false
        all

    member this.VariantHeaders() : struct (string * string)[] =
        names |> Array.map (fun name -> struct (name, this.RequestValue name))
