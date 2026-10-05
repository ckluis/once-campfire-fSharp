// The app rust/crates/kit/tests/http.rs runs its tests against, on Campfire.Kit: the same actions and
// the same routes. HttpTests drives it; reference-tools/kit/http-differential serves it next to the Rust
// one and compares the answers to random requests.
module Campfire.Kit.Tests.HttpApp

open System
open System.Text
open Falco
open Campfire.RailsCompat
open Campfire.RailsCompat.Clock
open Campfire.Kit

type AppState = { Name: string }

type CurrentUser = { Name: string }

let private text (v: string | null) : Value =
    match v with
    | null -> Value.Null
    | v -> Value.String v

let private show (c: Ctx) =
    act {
        let id = match c.ParamStr "id" with null -> "" | id -> id
        let name = (c.State<AppState>()).Name
        return c.Html $"<p>{name} room {id}</p>"
    }

let private form (c: Ctx) = act { return c.Html "<form method=\"post\" action=\"/form\"></form>" }

let private create (c: Ctx) =
    act {
        do! c.VerifyAuthenticityToken()
        let body =
            Value.Object
                [ "method", Value.String c.Request.Method
                  "original_method", Value.String c.Request.OriginalMethod
                  "params", c.Params.ToJson() ]
        return c.Json(Status.Ok, body)
    }

let private echo (c: Ctx) =
    act {
        let! remoteIp = c.Request.RemoteIp()
        let body =
            Value.Object
                [ "method", Value.String c.Request.Method
                  "original_method", Value.String c.Request.OriginalMethod
                  "params", c.Params.ToJson()
                  "raw", Value.String(Encoding.UTF8.GetString c.Request.RawPost.Span)
                  "remote_ip", Value.String remoteIp ]
        return c.Json(Status.Ok, body)
    }

let private upload (c: Ctx) =
    act {
        let! user = c.Params.Require "user"
        let file = user.Get "avatar" |> ValueOption.bind (fun p -> match p.AsFile with null -> ValueNone | f -> ValueSome f)
        let body =
            Value.Object
                [ "method", Value.String c.Request.Method
                  "name", (match user.Get "name" with ValueSome p -> text p.AsStr | ValueNone -> Value.Null)
                  "filename", (match file with ValueSome f -> Value.String f.OriginalFilename | ValueNone -> Value.Null)
                  "content", (match file with ValueSome f -> Value.String(Encoding.UTF8.GetString(f.Read())) | ValueNone -> Value.Null) ]
        return c.Json(Status.Ok, body)
    }

let private sessionSet (c: Ctx) =
    act {
        let value = match c.ParamStr "value" with null -> "" | v -> v
        c.Session().Insert("return_to_after_authenticating", value)
        return c.Head Status.Ok
    }

let private sessionGet (c: Ctx) =
    act {
        let session = c.Session()
        let value = match session.Get "return_to_after_authenticating" with ValueSome v -> v | ValueNone -> Value.Null
        return c.Json(Status.Ok, Value.Object [ "value", value; "id", text session.Id ])
    }

let private sessionReset (c: Ctx) =
    act {
        c.ResetSession()
        return c.Head Status.Ok
    }

let private noop (c: Ctx) = act { return c.Head Status.NoContent }

let private notice (c: Ctx) =
    act { return! c.RedirectToWith("/flash", { Redirect.Default with Notice = "✓" }) }

let private showFlash (c: Ctx) =
    act { return c.Json(Status.Ok, Value.Object [ "notice", text (c.Flash().Notice) ]) }

let private signIn (c: Ctx) =
    act {
        do! c.Cookies.SetSigned("session_token", (Cookie.New "tok123").AsPermanent().AsHttpOnly())
        c.Cookies.Set("last_room", (Cookie.New "7").AsPermanent())
        return c.Head Status.Ok
    }

/// `sign_in` from an `ActionController::Live` controller.
let private liveSignIn (c: Ctx) =
    act {
        c.UseLiveResponse()
        return! signIn c
    }

let private whoami (c: Ctx) =
    act { return c.Json(Status.Ok, Value.Object [ "token", text (c.Cookies.Signed "session_token"); "last_room", text (c.Cookies.Get "last_room") ]) }

let private requireUser (c: Ctx) : Result<unit, Error> =
    match c.Request.Header "x-user" with
    | null ->
        match c.RedirectTo "/session/new" with
        | Ok response -> halt response
        | Error e -> Error e
    | user ->
        c.SetCurrent { CurrentUser.Name = user }
        Ok()

let private ensureAdmin (c: Ctx) : Result<unit, Error> =
    match c.Current<CurrentUser>() with
    | ValueSome user when user.Name = "admin" -> Ok()
    | _ -> halt (c.Head Status.Forbidden)

let private admin (c: Ctx) =
    act {
        c.SetHeader("x-version", "42")
        do! requireUser c
        do! ensureAdmin c
        let user = (c.Current<CurrentUser>()).Value.Name
        return c.Html $"hi {user}"
    }

let private messages (c: Ctx) =
    act {
        let! format = c.RespondTo [ Format.Html; Format.TurboStream; Format.Json ]
        if format = Format.TurboStream then
            return c.TurboStream "<turbo-stream action=\"append\"></turbo-stream>"
        elif format = Format.Json then
            return c.Json(Status.Ok, Value.Array [])
        else
            return c.Render(Status.Ok, Format.Html, "<p>messages</p>")
    }

let private autocomplete (c: Ctx) =
    act {
        let! format = c.RespondTo [ Format.Html; Format.Json ]
        return c.Render(Status.Ok, format, "x")
    }

let private redirects (c: Ctx) =
    act {
        match c.ParamStr "to" with
        | "relative" -> return! c.RedirectTo "rooms/1"
        | "other" -> return! c.RedirectTo "https://evil.example/"
        | "other_allowed" -> return! c.RedirectToWith("https://docs.example/", { Redirect.Default with AllowOtherHost = true })
        | "see_other" -> return! c.RedirectToWith("/rooms", { Redirect.Default with Status = ValueSome Status.SeeOther })
        | "back" -> return! c.RedirectBackOrTo "/fallback"
        | _ -> return! c.RedirectTo "/rooms/1?x=1"
    }

let private created (c: Ctx) = act { return! c.HeadWithLocation(Status.Created, "/rooms/1/messages/2") }

let private file (c: Ctx) =
    act {
        let path = nonNull (c.ParamStr "path")
        return! c.SendFile(path, SendOptions.Inline "image/png")
    }

let private logo (c: Ctx) =
    act {
        if c.Stale(Freshness.OfEtag "accounts/1-20240601") then
            c.ExpiresIn(300UL, { ExpiresIn.Default with Public = true; StaleWhileRevalidate = ValueSome 604800UL })
            return c.SendData("PNG", SendOptions.Inline "image/png")
        else
            return c.Head Status.NotModified
    }

let private indexFresh (c: Ctx) =
    act {
        match c.FreshWhen(Freshness.OfEtag "messages/1-2") with
        | ValueSome notModified -> return notModified
        | ValueNone -> return c.Html "<p>page</p>"
    }

/// A kit as the tests build it: `test-secret`, a clock frozen at 2024-06-01T12:00:00Z, and this app's state.
let kitFor (config: KitConfig) : Kit =
    let clock: SharedClock = TestClock.FrozenAt((Timestamps.tryParse "2024-06-01T12:00:00Z").Value)
    Kit(config, Secrets.create "test-secret", clock, ({ AppState.Name = "Campfire" } :> obj))

/// Every route of the app, as `rust/crates/kit/tests/http.rs` lays them out.
let routes (kit: Kit) : HttpEndpoint list =
    let route = Adapter.route kit
    [ route "/rooms/{id}" [ "GET", show ]
      route "/form" [ "GET", form; "POST", create ]
      route "/echo/{id}" [ "GET", echo; "POST", echo; "PATCH", echo; "DELETE", echo ]
      route "/upload" [ "PATCH", upload; "POST", upload ]
      route "/session" [ "GET", sessionGet; "POST", sessionSet; "DELETE", sessionReset ]
      route "/noop" [ "GET", noop ]
      route "/notice" [ "GET", notice ]
      route "/flash" [ "GET", showFlash ]
      route "/sign_in" [ "GET", signIn ]
      route "/live_sign_in" [ "GET", liveSignIn ]
      route "/whoami" [ "GET", whoami ]
      route "/sign_out" [ "GET", (fun c -> act { c.Cookies.Delete "session_token"; return c.Head Status.Ok }) ]
      route "/admin" [ "GET", admin ]
      route "/messages" [ "GET", messages; "POST", messages ]
      route "/messages.{format}" [ "GET", messages ]
      route "/autocomplete" [ "GET", autocomplete ]
      route "/redirect" [ "GET", redirects ]
      route "/created" [ "GET", created ]
      route "/file" [ "GET", file ]
      route "/logo" [ "GET", logo ]
      route "/fresh" [ "GET", indexFresh ] ]

let private page (text: string) = ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes text)

/// `public/404.html` and `422.html`.
let errorPages = ErrorPages.Of [ 404, page "<h1>Not found</h1>"; 422, page "<h1>Unprocessable</h1>" ]

/// The configuration most tests run with.
let plainConfig = { KitConfig.Default with ErrorPages = errorPages }

/// The app as Campfire runs it in production: behind TLS (`assume_ssl`) with `force_ssl`.
let sslConfig =
    { KitConfig.Default with
        ErrorPages = ErrorPages.Of [ 422, page "<h1>Unprocessable</h1>" ]
        ForceSsl = true
        Proxy = { ProxyConfig.Default with AssumeSsl = true } }
