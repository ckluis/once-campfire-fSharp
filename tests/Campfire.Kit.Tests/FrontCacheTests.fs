// Port of the #[cfg(test)] module in rust/crates/kit/src/front/cache.rs
module Campfire.Kit.Tests.FrontCacheTests

open System
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.Extensions.Primitives
open Xunit
open Campfire.Kit

/// A request as it arrived: its method, raw target and headers.
let private request (meth: string) (target: string) (headers: (string * string) list) : DefaultHttpContext =
    let ctx = DefaultHttpContext()
    ctx.Request.Method <- meth
    (nonNull (ctx.Features.Get<IHttpRequestFeature>())).RawTarget <- target
    for (name, value) in headers do
        ctx.Request.Headers.Append(name, value)
    ctx

let private response (body: string) : CachedResponse =
    CachedResponse(200, Array.empty, ReadOnlyMemory<byte>(Text.Encoding.UTF8.GetBytes body), Array.empty)

let private lifetime (status: int) (cacheControl: string) (vary: string) : TimeSpan voption =
    FrontCache.cacheLifetime status cacheControl vary

let private shouldCache (meth: string) (target: string) (headers: (string * string) list) : bool =
    let ctx = request meth target headers
    let get name = match ctx.Request.Headers[name].ToString() with "" -> null | v -> v
    FrontCache.shouldCacheRequest meth (get "Connection") (get "Upgrade") (get "Range") target.Length

[<Fact>]
let ``lifetime needs public and a max age`` () =
    let ok = 200
    Assert.Equal(ValueSome(TimeSpan.FromSeconds 2592000.0), lifetime ok "public, max-age=2592000" "")
    Assert.Equal(ValueSome(TimeSpan.FromSeconds 300.0), lifetime ok "max-age=300, public, stale-while-revalidate=604800" "")
    Assert.Equal(ValueSome(TimeSpan.FromSeconds 10.0), lifetime ok "public, s-max-age=10, max-age=99" "")
    Assert.Equal(ValueNone, lifetime ok "max-age=0, private, must-revalidate" "")
    Assert.Equal(ValueNone, lifetime ok "public" "")
    Assert.Equal(ValueNone, lifetime ok "public, max-age=0" "")
    Assert.Equal(ValueNone, lifetime ok "public, no-cache, max-age=60" "")
    Assert.Equal(ValueNone, lifetime ok "public, max-age=60" "*")
    Assert.Equal(ValueNone, lifetime 304 "public, max-age=60" "")
    Assert.Equal(ValueNone, lifetime 404 "public, max-age=60" "")
    Assert.True((lifetime 301 "public, max-age=60" "").IsSome)
    // The regular expressions' word boundaries.
    Assert.Equal(ValueNone, lifetime ok "public, max-age=60abc" "")
    Assert.Equal(ValueNone, lifetime ok "public, xmax-age=60" "")
    Assert.Equal(ValueNone, lifetime ok "publicly, max-age=60" "")
    Assert.Equal(ValueSome(TimeSpan.FromSeconds 60.0), lifetime ok "public, xs-max-age=60" "")
    Assert.Equal(ValueNone, lifetime ok "public, max-age=99999999999999999999" "")

[<Fact>]
let ``requests that bypass the cache`` () =
    Assert.True(shouldCache "GET" "/x" [])
    Assert.False(shouldCache "GET" "/x" [ "upgrade", "websocket" ])
    Assert.False(shouldCache "GET" "/x" [ "connection", "Upgrade" ])
    Assert.True(shouldCache "GET" "/x" [ "connection", "upgrade" ]) // Thruster compares exactly
    Assert.False(shouldCache "GET" "/x" [ "range", "bytes=0-1" ])
    Assert.False(shouldCache "POST" "/x" [])
    Assert.True(shouldCache "HEAD" "/x" [])
    let uri (length: int) = "/qr_code/aGk?pad=" + String('x', length - 17)
    Assert.True(shouldCache "HEAD" (uri FrontCacheLimits.MaxCacheableUri) [])
    Assert.False(shouldCache "HEAD" (uri (FrontCacheLimits.MaxCacheableUri + 1)) [])

[<Fact>]
let ``keys use the raw path and query and include varying headers`` () =
    let variant (uri: string) (ae: string) =
        let ctx = request "GET" uri [ "host", "chat.test"; "accept-encoding", ae ]
        Variant.OfRequest("GET", uri, ctx.Request.Headers)
    let key (uri: string) = (variant uri "gzip").CacheKey()
    Assert.NotEqual<string>(key "/a?a=1", key "/a?a=2")
    Assert.NotEqual<string>(key "/a%2Fb", key "/a/b")
    Assert.NotEqual<string>(key "/a?b=2&a=1", key "/a?a=1&b=2")
    Assert.NotEqual<string>(key "/a?q=a+b", key "/a?q=a%20b")
    // Rack reads `disposition=attachment;` where Go's query parser dropped the pair.
    Assert.NotEqual<string>(key "/a?disposition=attachment;", key "/a")
    Assert.NotEqual<string>(key "/a?disposition=inline&disposition=x;", key "/a?disposition=inline")
    Assert.Equal(key "/a?", key "/a")

    let gzip = variant "/a" "gzip"
    let plain = variant "/a" ""
    Assert.Equal(gzip.CacheKey(), plain.CacheKey())
    gzip.SetResponseHeaders "Accept-Encoding"
    plain.SetResponseHeaders "Accept-Encoding"
    Assert.NotEqual<string>(gzip.CacheKey(), plain.CacheKey())
    Assert.True(gzip.Matches(gzip.VariantHeaders()))
    Assert.False(plain.Matches(gzip.VariantHeaders()))

/// A body that makes an entry under a one-letter key take `size` bytes.
let private responseOfSize (size: int) : CachedResponse = response (String('x', size - 1 - FrontCacheLimits.EntryOverhead))

[<Fact>]
let ``memory cache expires and evicts`` () =
    let now = 1000L
    let seconds (n: int) = int64 n * 1000L
    let item = int64 FrontCacheLimits.EntryOverhead + 50L
    let cache = MemoryCache(2L * item, item + 10L)
    cache.Set("a", responseOfSize (int item), now + seconds 10, now)
    Assert.NotNull(cache.Get("a", now))
    Assert.Null(cache.Get("a", now + seconds 11))

    cache.Set("big", responseOfSize (int item + 11), now + seconds 10, now)
    Assert.Null(cache.Get("big", now)) // larger than the item limit

    cache.Set("b", responseOfSize (int item), now + seconds 10, now)
    cache.Set("c", responseOfSize (int item), now + seconds 10, now)
    Assert.Equal(2L * item, cache.Size)
    let kept = [ "a"; "b"; "c" ] |> List.filter (fun k -> not (isNull (cache.Get(k, now)))) |> List.length
    Assert.Equal(2, kept)

    cache.Set("c", responseOfSize (int item - 30), now + seconds 10, now)
    Assert.True(cache.Size <= 2L * item)

[<Fact>]
let ``memory cache charges keys`` () =
    let now = 1000L
    let capacity = 64L * 1024L
    let cache = MemoryCache(capacity, 1024L * 1024L)
    for n in 0..99 do
        let key = $"HEAD\n/qr_code/aGk\npad={n}{String('x', 1000)}\nchat.test"
        cache.Set(key, response "", now + 60_000L, now)
    Assert.True(cache.Size <= capacity)
    Assert.True(cache.Size > capacity - 2000L, $"charged the keys: {cache.Size}")
    let held = cache.Keys |> Array.sumBy (fun k -> int64 (k.Length + FrontCacheLimits.EntryOverhead))
    Assert.True(held <= capacity)

[<Fact>]
let ``not modified compares etags`` () =
    let cached = CachedResponse(200, [| struct ("ETag", "\"abc\"") |], ReadOnlyMemory<byte>(Text.Encoding.UTF8.GetBytes "x"), Array.empty)
    Assert.True(FrontCache.wasNotModified cached "\"abc\"")
    Assert.True(FrontCache.wasNotModified cached "\"x\", \"abc\"")
    Assert.False(FrontCache.wasNotModified cached "W/\"abc\"")
    Assert.False(FrontCache.wasNotModified (response "x") "\"abc\"")
    Assert.False(FrontCache.wasNotModified cached null)
