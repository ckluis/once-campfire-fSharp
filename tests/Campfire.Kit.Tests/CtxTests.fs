// Ctx behaviors rust/crates/kit/tests/http.rs reaches only indirectly, checked on a Ctx built the way
// the adapter builds one.
module Campfire.Kit.Tests.CtxTests

open System
open System.Buffers
open System.Net
open System.Text
open Microsoft.AspNetCore.Http
open Xunit
open Campfire.RailsCompat
open Campfire.Kit
open Campfire.Kit.Tests.Helpers
open Campfire.Kit.Tests.Harness

let private makeCtx (config: KitConfig) (meth: string) (headers: (string * string) list) (requestParams: ParamMap) : Ctx =
    let kit = kitWith config (obj ())
    let map = HeaderDictionary()
    for (k, v) in headers do
        map.Append(k, v)
    let request = Request.Create(meth, meth, "/rooms/1", "x=1", null, null, map, IPAddress.Loopback, ReadOnlyMemory.Empty, config.Proxy)
    Ctx(kit, request, ParamMap(), ParamMap(), requestParams, CookieJar(kit.Secrets, kit.Clock))

let private plain (headers: (string * string) list) = makeCtx KitConfig.Default "GET" ([ "host", "chat.example.com" ] @ headers) (ParamMap())

let private ok (result: Result<'a, Error>) : 'a =
    match result with
    | Ok v -> v
    | Error e -> failwith (Error.display e)

let private finish (c: Ctx) (response: Response) : Response = c.Finish(Ok response)

let private header (response: Response) (name: string) : string option =
    match response.GetHeader name with
    | null -> None
    | v -> Some v

[<Fact>]
let ``wrap parameters nests a json body's params under the key`` () =
    let newBody () =
        let body = ParamMap()
        body.Insert("name", Param.Str "Jo")
        body.Insert("_method", Param.Str "patch")
        body.Insert("authenticity_token", Param.Str "t")
        body
    let c = makeCtx KitConfig.Default "POST" [ "content-type", "application/json" ] (newBody ())
    c.WrapParameters("user", ValueNone)
    Assert.Equal(json """{"name": "Jo"}""", (c.Params.Get "user").Value.ToJson())
    Assert.True(c.RequestParams.ContainsKey "user")
    // With a model's attribute names, only those.
    let c = makeCtx KitConfig.Default "POST" [ "content-type", "application/json" ] (newBody ())
    c.WrapParameters("user", ValueSome [ "name"; "_method" ])
    Assert.Equal(json """{"name": "Jo", "_method": "patch"}""", (c.Params.Get "user").Value.ToJson())
    // Not JSON, or already there: left alone.
    let c = makeCtx KitConfig.Default "POST" [ "content-type", "application/x-www-form-urlencoded" ] (newBody ())
    c.WrapParameters("user", ValueNone)
    Assert.False(c.Params.ContainsKey "user")
    let withUser = ParamMap()
    withUser.Insert("user", Param.Str "x")
    let c = makeCtx KitConfig.Default "POST" [ "content-type", "application/json" ] withUser
    c.WrapParameters("user", ValueNone)
    Assert.Equal(Some "x", (c.Params.Get "user").Value.AsStr |> Option.ofObj)

[<Fact>]
let ``freshness by last modified`` () =
    let lastModified = ts "2024-05-01T00:00:00Z"
    let validators = { Freshness.Default with LastModified = ValueSome lastModified }
    let c = plain [ "if-modified-since", "Wed, 01 May 2024 00:00:00 GMT" ]
    Assert.True((c.FreshWhen validators).IsSome)
    Assert.Equal(Some "Wed, 01 May 2024 00:00:00 GMT", Option.ofObj (c.Headers.Get "last-modified"))
    let c = plain [ "if-modified-since", "Tue, 30 Apr 2024 00:00:00 GMT" ]
    Assert.True((c.FreshWhen validators).IsNone)
    let c = plain []
    Assert.True(c.Stale validators)
    // A cache that can share it: with `public` set, Rails (and so the kit) don't add the default
    // `max-age=0, must-revalidate` that a response with validators gets otherwise.
    let c = plain []
    c.FreshWhen { validators with Public = true } |> ignore
    let response = finish c (c.Html "x")
    Assert.Equal(Some "public", header response "cache-control")
    let c = plain []
    c.FreshWhen validators |> ignore
    Assert.Equal(Some "max-age=0, private, must-revalidate", header (finish c (c.Html "x")) "cache-control")

let private etagOf (c: Ctx) (freshness: Freshness) : string =
    c.FreshWhen freshness |> ignore
    nonNull (c.Headers.Get "etag")

[<Fact>]
let ``etags depend on the validator the template and the flash`` () =
    let weak = etagOf (plain []) (Freshness.OfEtag "messages/1")
    Assert.StartsWith("W/\"", weak)
    Assert.Equal(weak, etagOf (plain []) (Freshness.OfEtag "messages/1"))
    Assert.NotEqual<string>(weak, etagOf (plain []) (Freshness.OfEtag "messages/2"))
    Assert.NotEqual<string>(weak, etagOf (plain []) { Freshness.OfEtag "messages/1" with Template = "abc123" })
    let strong = etagOf (plain []) { Freshness.Default with StrongEtag = "messages/1" }
    Assert.StartsWith("\"", strong)
    Assert.False(strong.StartsWith "W/")
    // A flash is part of it, so a page shown with one isn't answered 304 for the one without.
    let flashed = plain []
    flashed.Flash().Now("notice", "hi")
    let withFlash = etagOf flashed (Freshness.OfEtag "messages/1")
    Assert.NotEqual<string>(weak, withFlash)
    let other = plain []
    other.Flash().Now("notice", "hi again")
    Assert.NotEqual<string>(withFlash, etagOf other (Freshness.OfEtag "messages/1"))

[<Fact>]
let ``cache control directives`` () =
    let c = plain []
    c.NoStore()
    Assert.Equal(Some "no-store", header (finish c (c.Html "x")) "cache-control")
    let c = plain []
    c.ExpiresNow()
    Assert.Equal(Some "no-cache", header (finish c (c.Html "x")) "cache-control")
    let c = plain []
    c.ExpiresIn(3600UL, { ExpiresIn.Default with Public = true; Immutable = true })
    let response = finish c (c.Html "x")
    Assert.Equal(Some "max-age=3600, public, immutable", header response "cache-control")
    Assert.Equal(Some "Sat, 01 Jun 2024 12:00:00 GMT", header response "date")
    // What `fresh_when` and `expires_in` set stays off what the action already set.
    let c = plain []
    let own = Response(200).ContentType("text/plain").Header("cache-control", "private, no-store")
    own.SetBody "x" |> ignore
    Assert.Equal(Some "private, no-store", header (finish c own) "cache-control")

[<Fact>]
let ``a live response skips the default headers and the etag`` () =
    let c = plain []
    c.UseLiveResponse()
    let response = finish c (c.Html "x")
    Assert.Equal(None, header response "x-frame-options")
    Assert.Equal(None, header response "etag")
    Assert.Equal(Some "no-cache", header response "cache-control")
    let c = plain []
    let normal = finish c (c.Html "x")
    Assert.Equal(Some "SAMEORIGIN", header normal "x-frame-options")
    Assert.True((header normal "etag").IsSome)

[<Fact>]
let ``redirects refuse what Rails refuses`` () =
    let refused (location: string) =
        match (plain []).RedirectTo location with
        | Error(UnsafeRedirect _) -> true
        | _ -> false
    Assert.True(refused "rooms/1")
    Assert.True(refused "//evil.example/x")
    Assert.True(refused "https://evil.example/")
    Assert.True(refused "javascript:alert(1)")
    Assert.False(refused "/rooms/1")
    Assert.False(refused "?page=2")
    Assert.False(refused "http://chat.example.com/rooms/1")
    Assert.False(refused "HTTP://CHAT.EXAMPLE.COM/rooms/1")
    // Line breaks never reach the header.
    let response = ok ((plain []).RedirectTo "/a\r\nset-cookie: x=1")
    Assert.Equal(Some "http://chat.example.com/aset-cookie: x=1", header response "location")
    // A referer from another host falls back.
    let back = ok ((plain [ "referer", "https://evil.example/x" ]).RedirectBackOrTo "/fallback")
    Assert.Equal(Some "http://chat.example.com/fallback", header back "location")
    let userinfo = ok ((plain [ "referer", "http://chat.example.com@evil.example/x" ]).RedirectBackOrTo "/fallback")
    Assert.Equal(Some "http://chat.example.com/fallback", header userinfo "location")

[<Fact>]
let ``notice and alert are flashed with the redirect`` () =
    let c = plain []
    ok (c.RedirectToWith("/rooms", { Redirect.Default with Notice = "saved"; Alert = "careful" })) |> ignore
    Assert.Equal("saved", c.Flash().Notice)
    Assert.Equal("careful", c.Flash().Alert)

[<Fact>]
let ``respond to picks the first offered format for a wildcard`` () =
    let c = plain [ "accept", "*/*" ]
    Assert.Equal(Format.Json, ok (c.RespondTo [ Format.Json; Format.Html ]))
    let c = plain [ "accept", "text/html" ]
    Assert.Equal(Format.Html, ok (c.RespondTo [ Format.Json; Format.Html ]))
    let c = plain [ "accept", "image/png" ]
    match c.RespondTo [ Format.Html ] with
    | Error UnknownFormat -> ()
    | other -> failwith $"{other}"

[<Fact>]
let ``current attributes are per request and per type`` () =
    let c = plain []
    Assert.Equal(ValueNone, c.Current<string>())
    c.SetCurrent "jo"
    c.SetCurrent 42
    Assert.Equal(ValueSome "jo", c.Current<string>())
    Assert.Equal(ValueSome 42, c.Current<int>())
    Assert.Equal(ValueSome "jo", c.TakeCurrent<string>())
    Assert.Equal(ValueNone, c.Current<string>())

[<Fact>]
let ``vary accept follows where the format came from`` () =
    let varies (headers: (string * string) list) =
        let c = plain headers
        header (c.Html "x") "vary"
    Assert.Equal(Some "Accept", varies [ "accept", "application/json" ])
    Assert.Equal(None, varies [])
    // A browser's Accept with `*/*` is ignored for the format, and so for Vary.
    Assert.Equal(None, varies [ "accept", "text/html,*/*;q=0.8" ])

type private FakeParts(text: string) =
    let bytes = Encoding.UTF8.GetBytes text
    interface IPageParts with
        member _.BodyLength = bytes.Length
        member _.Etag() = String('a', 64)
        member _.WritePlain writer = writer.Write(ReadOnlySpan<byte> bytes)
        member _.Gzip _ = ReadOnlyMemory<byte>(Array.empty)

[<Fact>]
let ``a page in parts gets its etag from the parts`` () =
    let c = plain []
    let response = c.RenderParts(Status.Ok, Format.Html, FakeParts "page")
    let finished = finish c response
    Assert.Equal(Some "W/\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"", header finished "etag")
    Assert.Equal(Some "text/html; charset=utf-8", header finished "content-type")
