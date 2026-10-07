// Port of the #[cfg(test)] module in rust/crates/kit/src/cookies.rs
module Campfire.Kit.Tests.CookiesTests

open System
open System.Diagnostics
open Xunit
open Campfire.RailsCompat
open Campfire.RailsCompat.Clock
open Campfire.Kit
open Campfire.Kit.Tests.Helpers

let private clock () : SharedClock = TestClock.FrozenAt(ts "2024-06-01T12:00:00Z")

let private jar (header: string) : CookieJar = CookieJar.FromHeaders([ header ], secrets.Value, clock ())

let private headers (jar: CookieJar) (ssl: bool) (host: string) : string list = List.ofSeq (jar.SetCookieHeaders(ssl, host))

let private get (jar: CookieJar) (name: string) : string option =
    match jar.Get name with
    | null -> None
    | v -> Some v

[<Fact>]
let ``parses request cookies`` () =
    let jar = jar "a=1; b=x%20y+z;c=3; a=2; d"
    Assert.Equal(Some "1", get jar "a")
    Assert.Equal(Some "x y z", get jar "b")
    Assert.Equal(Some "3", get jar "c")
    Assert.Equal(Some "", get jar "d")
    Assert.Equal(None, get jar "zz")

[<Fact>]
let ``sets plain cookies with rails defaults`` () =
    let jar = jar ""
    jar.Set("last_room", "42")
    Assert.Equal<string list>([ "last_room=42; path=/; samesite=lax" ], headers jar false "example.com")

[<Fact>]
let ``unchanged values are not rewritten unless expiring`` () =
    let jar = jar "last_room=42"
    jar.Set("last_room", "42")
    Assert.Empty(headers jar false "h")
    jar.Set("last_room", (Cookie.New "42").AsPermanent())
    Assert.Equal<string list>(
        [ "last_room=42; path=/; expires=Wed, 01 Jun 2044 12:00:00 GMT; samesite=lax" ],
        headers jar false "h"
    )

[<Fact>]
let ``escapes values`` () =
    let jar = jar ""
    jar.Set("x", "a b+c/=")
    Assert.Equal<string list>([ "x=a+b%2Bc%2F%3D; path=/; samesite=lax" ], headers jar false "h")

[<Fact>]
let ``signed permanent httponly round trip`` () =
    let jar = jar ""
    jar.SetSigned("session_token", ((Cookie.New "tok").AsPermanent().AsHttpOnly())) |> Result.defaultWith (fun e -> failwith $"{e}")
    let headers = headers jar true "h"
    Assert.Equal(1, headers.Length)
    Assert.StartsWith("session_token=", headers[0])
    Assert.EndsWith("; path=/; expires=Wed, 01 Jun 2044 12:00:00 GMT; httponly; samesite=lax", headers[0])
    Assert.Equal(Some "tok", (match jar.Signed "session_token" with null -> None | v -> Some v))
    let raw = (jar.Get "session_token") |> nonNull
    let next = CookieJar.FromHeaders([ $"session_token={CookieJar.escape raw}" ], secrets.Value, clock ())
    Assert.Equal(Some "tok", (match next.Signed "session_token" with null -> None | v -> Some v))

[<Fact>]
let ``tampered signed cookies read as nil`` () =
    let jar = jar "session_token=forged"
    Assert.Null(jar.Signed "session_token")
    Assert.Equal(Some "forged", get jar "session_token")

[<Fact>]
let ``encrypted round trip`` () =
    let jar = jar ""
    let value = json """{"a": 1}"""
    jar.SetEncrypted("secret", value, Cookie.New "") |> Result.defaultWith (fun e -> failwith $"{e}")
    Assert.Equal(ValueSome value, jar.Encrypted "secret")

[<Fact>]
let ``overflow`` () =
    let jar = jar ""
    let big = String('x', CookieJar.MaxCookieSize)
    match jar.SetSigned("big", big) with
    | Error(CookieOverflow _) -> ()
    | other -> failwith $"{other}"

[<Fact>]
let ``delete only when present`` () =
    let jar = jar "session_token=abc"
    jar.Delete "missing"
    jar.Delete "session_token"
    Assert.True(jar.IsDeleted "session_token")
    Assert.Equal(None, get jar "session_token")
    Assert.Equal<string list>(
        [ "session_token=; path=/; max-age=0; expires=Thu, 01 Jan 1970 00:00:00 GMT; samesite=lax" ],
        headers jar false "h"
    )

[<Fact>]
let ``secure cookies need ssl`` () =
    let jar = jar ""
    jar.Set("s", (Cookie.New "1").AsSecure())
    Assert.Empty(headers jar false "example.com")
    Assert.Equal(1, (headers jar false "x.onion").Length)
    Assert.Equal<string list>([ "s=1; path=/; secure; samesite=lax" ], headers jar true "example.com")

[<Fact>]
let ``many cookies parse in linear time`` () =
    let header = String.Join("; ", [ for n in 0..99_999 -> $"c{n}=1" ])
    let started = Stopwatch.GetTimestamp()
    Assert.Equal(100_000, (CookieJar.parseCookieHeader header).Length)
    let elapsed = Stopwatch.GetElapsedTime started
    Assert.True(elapsed < TimeSpan.FromSeconds 1.0, $"{elapsed}")
