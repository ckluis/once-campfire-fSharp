// The endpoints bench/kit measures, on Campfire.Kit and Kestrel. bench/kit/rust/src/main.rs is the same
// five endpoints on campfire_kit and hyper.
//
//   /hello                      a tiny page
//   /page                       a 100 KB page held in memory (ETag by SHA-256, gzip from the cache)
//   /session                    the signed session_token cookie verified, then the page
//   /rooms/{id}/messages (POST) a form: params parsed, CSRF checked, redirect
//   /gc                         bytes allocated so far (F# only)
//
//   KitBench PORT        serve
//   KitBench micro       time the cookie and hash building blocks
module KitBench

open System
open System.Net
open System.Text
open Falco
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Logging
open Campfire.RailsCompat
open Campfire.RailsCompat.Clock
open Campfire.Kit

let page =
    let one = "<div class=\"message\" data-message-id=\"42\"><p>Hello there, see you at 10:30</p></div>\n"
    String.replicate 1200 one

let pageBytes = ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes page)

let hello (c: Ctx) = act { return c.Html "hello" }

let pageAction (c: Ctx) = act { return c.Html pageBytes }

let withSession (c: Ctx) =
    act {
        match c.Cookies.Signed "session_token" with
        | null -> return! c.RedirectTo "/session/new"
        | token ->
            let prefix = Encoding.UTF8.GetByteCount token
            let buffer = PooledBytes.Rent(prefix + pageBytes.Length)
            Encoding.UTF8.GetBytes(token, 0, token.Length, buffer.Array, 0) |> ignore
            pageBytes.CopyTo(Memory<byte>(buffer.Array, prefix, pageBytes.Length))
            return c.Html buffer
    }

let gcStats (c: Ctx) =
    act { return c.Html($"{GC.GetTotalAllocatedBytes(false)} {GC.CollectionCount 0} {GC.CollectionCount 1} {GC.CollectionCount 2}") }

let post (c: Ctx) =
    act {
        do! c.VerifyAuthenticityToken()
        let! message = c.Params.Require "message"
        let length = match message.Get "body" with ValueSome b -> (match b.ToS() with null -> 0 | s -> s.Length) | ValueNone -> 0
        return! c.RedirectTo("/rooms/1?m=" + string length)
    }

let secretKeyBase = "bench-secret-key-base"

let micro () =
    let secrets = Secrets.create secretKeyBase
    let cookie = Cookies.sign secrets "session_token" "Kjcb4WUEktkJJaYJrSbT8wcb" None
    let now = DateTimeOffset.UtcNow
    let time (name: string) (n: int) (f: unit -> unit) =
        for _ in 1..2000 do f ()
        let sw = Diagnostics.Stopwatch.StartNew()
        let a0 = GC.GetAllocatedBytesForCurrentThread()
        for _ in 1..n do f ()
        let a1 = GC.GetAllocatedBytesForCurrentThread()
        printfn "%-30s %8.2f us/op  %8d B/op" name (sw.Elapsed.TotalMilliseconds * 1000.0 / float n) ((a1 - a0) / int64 n |> int)
    time "Cookies.verifySigned" 20000 (fun () -> Cookies.verifySigned secrets "session_token" cookie now |> ignore)
    time "Cookies.sign" 20000 (fun () -> Cookies.sign secrets "session_token" "Kjcb4WUEktkJJaYJrSbT8wcb" None |> ignore)
    let body = Encoding.UTF8.GetBytes page
    time "SHA256 of the 100 KB page" 5000 (fun () -> Security.Cryptography.SHA256.HashData body |> ignore)
    let session = Cookies.encrypt secrets "_campfire_session" (Value.Object [ "session_id", Value.String "36166e44ee4e3a234bcf50a9f864d2a7" ]) None
    time "Cookies.decrypt (session)" 20000 (fun () -> Cookies.decrypt secrets "_campfire_session" session now |> ignore)

[<EntryPoint>]
let main argv =
    if argv.Length > 0 && argv[0] = "micro" then
        micro ()
        exit 0
    let port = if argv.Length > 0 then int argv[0] else 3001
    let secrets = Secrets.create secretKeyBase
    let kit = Kit(KitConfig.Default, secrets, SystemClock(), obj ())
    let builder = WebApplication.CreateBuilder()
    builder.WebHost.ConfigureKestrel(fun options ->
        Adapter.configureKestrel options
        options.Listen(IPAddress.Loopback, port))
    |> ignore
    builder.Logging.ClearProviders() |> ignore
    let app = builder.Build()
    // config.ru: `use Rack::Deflater` around the whole app.
    Adapter.useDeflater kit app |> ignore
    let route = Adapter.route kit
    Adapter.app
        kit
        [ route "/hello" [ "GET", hello ]
          route "/page" [ "GET", pageAction ]
          route "/session" [ "GET", withSession ]
          route "/gc" [ "GET", gcStats ]
          route "/rooms/{id}/messages" [ "POST", post ] ]
        app
    // A session token cookie to send, signed the way the app signs it (the Rust server has the same secret).
    let cookie = Cookies.sign secrets "session_token" "Kjcb4WUEktkJJaYJrSbT8wcb" None
    printfn "COOKIE=session_token=%s" (CookieJar.escape cookie)
    app.Run()
    0
