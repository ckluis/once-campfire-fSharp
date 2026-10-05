// The endpoints bench/front measures, behind Campfire.Kit's front server. bench/front/rust/src/main.rs is
// the same on the Rust front server.
//
//   FrontBench HTTP_PORT TARGET_PORT
module FrontBench

open System
open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open Campfire.RailsCompat
open Campfire.RailsCompat.Clock
open Campfire.Kit

let page =
    let one = "<div class=\"message\" data-message-id=\"42\"><p>Hello there, see you at 10:30</p></div>\n"
    String.replicate 1200 one

let pageBytes = ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes page)
let assetBytes = ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(String.replicate 800 "body { color: #123456; }\n"))

let hello (c: Ctx) = act { return c.Html "hello" }

let pageAction (c: Ctx) = act { return c.Html pageBytes }

let asset (c: Ctx) = act { return (c.Html assetBytes).Header("Cache-Control", "public, max-age=3600") }

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

[<EntryPoint>]
let main argv =
    let http = if argv.Length > 0 then argv[0] else "3001"
    let target = if argv.Length > 1 then argv[1] else "3101"
    let secrets = Secrets.create "bench-secret-key-base"
    let kit = Kit(KitConfig.Default, secrets, SystemClock(), obj ())
    let config =
        FrontConfig.fromLookup (fun name ->
            match name with
            | "HTTP_PORT" -> http
            | "TARGET_PORT" -> target
            | "LOG_REQUESTS" -> "false"
            | _ -> null)
    let route = Adapter.route kit
    let configure (app: Microsoft.AspNetCore.Builder.IApplicationBuilder) =
        // config.ru: `use Rack::Deflater` around the whole app.
        Adapter.useDeflater kit app |> ignore
        Adapter.app
            kit
            [ route "/hello" [ "GET", hello ]
              route "/page" [ "GET", pageAction ]
              route "/asset" [ "GET", asset ]
              route "/session" [ "GET", withSession ]
              route "/gc" [ "GET", gcStats ]
              route "/rooms/{id}/messages" [ "POST", post ] ]
            app
    // A session token cookie to send, signed the way the app signs it (the Rust server has the same secret).
    let cookie = Cookies.sign secrets "session_token" "Kjcb4WUEktkJJaYJrSbT8wcb" None
    printfn "COOKIE=session_token=%s" (CookieJar.escape cookie)
    (Front.serve config configure (TaskCompletionSource<unit>().Task :> Task)).GetAwaiter().GetResult()
    0
