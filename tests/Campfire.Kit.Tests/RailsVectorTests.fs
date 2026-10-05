// Port of rust/crates/kit/tests/rails_vectors.rs, and the "csrf" section of vectors/rails_compat.json
//
// Old-browser-tab continuity: a session cookie and a signed cookie issued by the reference Rails
// app (`vectors/rails_compat.json`) are accepted by the kit, and what the kit writes back decodes
// to the same session. Forgery protection is by `Sec-Fetch-Site` rather than Rails' tokens, so a
// tab opened before an upgrade keeps working without one.
//
// The Rust crate never reads the "csrf" section: the kit has no token to check. It does keep Rails'
// `Origin` check, which the section's `origin` cases decide, and it must ignore a token Rails issued,
// whatever Rails would have said about it, which the `validity` cases (Rails' own verdicts on tokens
// for paths and methods) show. Each test asserts how many cases it ran.
module Campfire.Kit.Tests.RailsVectorTests

open System
open System.Net
open System.Text.Json
open System.Threading.Tasks
open Falco
open Microsoft.AspNetCore.Http
open Xunit
open Campfire.RailsCompat
open Campfire.RailsCompat.Clock
open Campfire.Kit
open Campfire.Kit.Tests.Helpers
open Campfire.Kit.Tests.Harness
open Campfire.Tests

let private vectors = lazy (Repo.vector "rails_compat")

let private root () = vectors.Value.RootElement

let private str (e: JsonElement) : string = nonNull (e.GetString())

let private kitFromVectors () : Kit * Secrets * Timestamp =
    let secrets = Secrets.create (str (root().GetProperty "secret_key_base"))
    let now = ts (str (root().GetProperty "now"))
    Kit(KitConfig.Default, secrets, TestClock.FrozenAt now, null), secrets, now

let private createSession (c: Ctx) =
    act {
        do! c.VerifyAuthenticityToken()
        let session = c.Session()
        let csrf = match session.Get "_csrf_token" with ValueSome v -> v | ValueNone -> Value.Null
        let id = match session.Id with null -> Value.Null | id -> Value.String id
        let body = Value.Object [ "id", id; "csrf", csrf ]
        session.Insert("return_to_after_authenticating", "/rooms/1")
        return c.Json(Status.Ok, body)
    }

let private whoami (c: Ctx) =
    act {
        let token = match c.Cookies.Signed "session_token" with null -> Value.Null | t -> Value.String t
        return c.Json(Status.Ok, Value.Object [ "token", token ])
    }

let private startApp () : Task<TestApp * Secrets * Timestamp> =
    task {
        let kit, secrets, now = kitFromVectors ()
        let route = Adapter.route kit
        let! app = start kit [ route "/session" [ "POST", createSession ]; route "/whoami" [ "GET", whoami ] ]
        return app, secrets, now
    }

let private postSession (app: TestApp) (cookie: string) (site: string) (token: string option) (origin: string option) : Task<Reply> =
    let request =
        (post "/session").With("host", "localhost:3000").With("cookie", cookie).With("sec-fetch-site", site)
    let request = match origin with Some origin -> request.With("origin", origin) | None -> request
    let request =
        match token with
        | Some token -> request.Form $"authenticity_token={CookieJar.escape token}"
        | None -> request.WithBody ""
    app.Send request

[<Fact>]
let ``rails sessions carry over`` () =
    task {
        let! app, secrets, now = startApp ()
        use _ = app
        let session = root().GetProperty "session"
        let sessionCookieRaw = str (session.GetProperty "session_cookie_raw")
        let cookie = "_campfire_session=" + CookieJar.escape sessionCookieRaw
        let formToken = str (session.GetProperty "session_form_token")

        // A form from a page Rails rendered still posts its token; it's ignored, not required.
        let! fromOldTab = postSession app cookie "same-origin" (Some formToken) None
        Assert.Equal(200, fromOldTab.Status)
        let setCookie = fromOldTab.Cookies |> List.exactlyOne
        let expectedSession = session.GetProperty "session"
        Assert.Equal(Some(Value.String(str (expectedSession.GetProperty "session_id"))), fromOldTab.Json.TryGet "id")
        // Rails' token stays in the session, unused.
        Assert.Equal(Some(Value.String(str (expectedSession.GetProperty "_csrf_token"))), fromOldTab.Json.TryGet "csrf")

        // What we write back after a change is the same session plus the change, in Rails' format.
        let raw = Cookies.unescape ((setCookie.Substring("_campfire_session=".Length)).Split(';')[0])
        let decoded = (Cookies.decrypt secrets "_campfire_session" raw now).Value
        let expected =
            match (Json.parse (Text.Encoding.UTF8.GetBytes(expectedSession.GetRawText()))).Value with
            | Value.Object entries -> Value.Object(entries @ [ "return_to_after_authenticating", Value.String "/rooms/1" ])
            | other -> other
        Assert.Equal(expected, decoded)
        Assert.EndsWith("; path=/; expires=Mon, 01 Jan 2046 12:00:00 GMT; httponly; samesite=lax", setCookie)

        let! noToken = postSession app cookie "same-origin" None None
        Assert.Equal(200, noToken.Status)

        // A valid Rails token doesn't make a cross-site request acceptable.
        let! crossSite = postSession app cookie "cross-site" (Some formToken) None
        Assert.Equal(422, crossSite.Status)

        let! crossOrigin = postSession app cookie "same-origin" None (Some "https://evil.example")
        Assert.Equal(session.GetProperty("post_with_cross_origin_status").GetInt32(), crossOrigin.Status)
    }

[<Fact>]
let ``rails signed session token cookie is read`` () =
    task {
        let! app, _, _ = startApp ()
        use _ = app
        let session = root().GetProperty "session"
        let tokenRaw = str (session.GetProperty "session_token_raw")
        let cookie = "session_token=" + CookieJar.escape tokenRaw
        let! reply = app.Send((get "/whoami").With("cookie", cookie))
        Assert.Equal(Some(Value.String(str (session.GetProperty "session_token_value"))), reply.Json.TryGet "token")
    }

/// A context for `request`, as the adapter would build one.
let private ctxFor (config: KitConfig) (meth: string) (headers: (string * string) list) : Ctx =
    let kit, _, _ = kitFromVectors ()
    let kit = Kit({ config with ForceSsl = config.ForceSsl }, kit.Secrets, kit.Clock, null)
    let map = HeaderDictionary()
    for (k, v) in headers do
        map.Append(k, v)
    let request = Request(meth, meth, "/session", null, null, null, map, IPAddress.Loopback, ReadOnlyMemory.Empty, config.Proxy)
    Ctx(kit, request, ParamMap(), ParamMap(), ParamMap(), CookieJar(kit.Secrets, kit.Clock))

[<Fact>]
let ``csrf origin cases`` () =
    // Rails' `valid_request_origin?`: `true`, `false`, or "raises" for the `null` origin.
    let cases = (root().GetProperty "csrf").GetProperty("origin").EnumerateArray() |> Seq.toArray
    Assert.Equal(8, cases.Length)
    for case in cases do
        let baseUrl = Uri(str (case.GetProperty "base_url"))
        let origin =
            match case.GetProperty("origin").ValueKind with
            | JsonValueKind.String -> [ "origin", str (case.GetProperty "origin") ]
            | _ -> []
        let host = if baseUrl.IsDefaultPort then baseUrl.Host else $"{baseUrl.Host}:{baseUrl.Port}"
        let headers = [ "host", host; "sec-fetch-site", "same-origin" ] @ origin
        let result = (ctxFor KitConfig.Default "POST" headers).VerifyAuthenticityToken()
        let label = case.GetRawText()
        match case.GetProperty("expected").ValueKind with
        | JsonValueKind.True -> Assert.True(result.IsOk, label)
        | JsonValueKind.False ->
            match result with
            | Error(InvalidAuthenticityToken message) -> Assert.Contains("didn't match request.base_url", message)
            | other -> failwith $"{label}: {other}"
        | _ ->
            match result with
            | Error(InvalidAuthenticityToken message) -> Assert.Contains("'null' origin", message)
            | other -> failwith $"{label}: {other}"

[<Fact>]
let ``csrf tokens rails issued are ignored`` () =
    let csrf = (root().GetProperty "csrf")
    Assert.Equal(3, csrf.GetProperty("global_tokens").GetArrayLength())
    Assert.Equal(8, csrf.GetProperty("form_tokens").GetArrayLength())
    let cases = csrf.GetProperty("validity").EnumerateArray() |> Seq.toArray
    Assert.Equal(189, cases.Length)
    let mutable accepted = 0
    for case in cases do
        // Whatever Rails said about the token (`expected`), the kit looks at the site, not the token.
        let meth = str (case.GetProperty "method")
        let verify site =
            (ctxFor KitConfig.Default meth [ "host", "campfire.test"; "sec-fetch-site", site ]).VerifyAuthenticityToken()
        Assert.True((verify "same-origin").IsOk, case.GetRawText())
        Assert.True((verify "same-site").IsOk, case.GetRawText())
        match verify "cross-site" with
        | Error(InvalidAuthenticityToken _) -> accepted <- accepted + 1
        | other -> failwith $"{case.GetRawText()}: {other}"
    Assert.Equal(189, accepted)
    // The SSL policy: no header is refused when the app is behind TLS, whatever token came with it.
    let tls = { KitConfig.Default with ForceSsl = true; Proxy = { ProxyConfig.Default with AssumeSsl = true } }
    Assert.True(((ctxFor tls "POST" [ "host", "campfire.test" ]).VerifyAuthenticityToken()).IsError)
    Assert.True(((ctxFor KitConfig.Default "POST" [ "host", "campfire.test" ]).VerifyAuthenticityToken()).IsOk)
