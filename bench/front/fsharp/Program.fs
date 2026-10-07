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

/// What a page of cached fragments costs per request once its pieces are stored: splice the parts, the
/// ETag, and the gzip member. bench/front/rust/src/bin/splice.rs is the same on the Rust kit.
let micro (iterations: int) =
    let message (n: int) =
        Fragment(
            "<div id=\"message_" + string n + "\" class=\"message\">"
            + String.replicate (140 + n % 7) "<button>Boost</button> hello there, see you at 10:30 in the usual room "
            + "</div>\n"
        )
    let messages = [| for n in 0..39 -> message n |]
    let head = String.replicate 300 "<html><head><title>Room</title></head><body>layout "
    let tail = String.replicate 100 "</body></html>"
    let text = StringBuilder(head)
    let offsets = ResizeArray<struct (int * Fragment)>()
    for fragment in messages do
        text.Append "  " |> ignore
        offsets.Add(struct (text.Length, fragment))
    text.Append tail |> ignore
    let textBytes = Encoding.UTF8.GetBytes(text.ToString())
    let body = textBytes.Length + (messages |> Array.sumBy (fun m -> m.Length))
    let page () =
        let copy = Array.copy textBytes
        match PageParts.Splice(ReadOnlyMemory<byte> copy, offsets) with
        | Ok parts ->
            let etag = parts.Etag()
            let gzip = parts.Gzip 0u
            struct (etag, gzip.Length)
        | Error _ -> failwith "parts"
    for _ in 1..2000 do
        page () |> ignore
    let a0 = GC.GetAllocatedBytesForCurrentThread()
    let sw = Diagnostics.Stopwatch.StartNew()
    let mutable length = 0
    for _ in 1..iterations do
        let struct (_, gzipLength) = page ()
        length <- gzipLength
    let elapsed = sw.Elapsed
    let a1 = GC.GetAllocatedBytesForCurrentThread()
    // Where the time goes: each step on its own, on a page already spliced.
    let time (name: string) (n: int) (f: unit -> unit) =
        for _ in 1..500 do f ()
        let a = GC.GetAllocatedBytesForCurrentThread()
        let started = Diagnostics.Stopwatch.StartNew()
        for _ in 1..n do f ()
        let elapsed = started.Elapsed
        let b = GC.GetAllocatedBytesForCurrentThread()
        printfn "  %-28s %7.2f us  %7d B" name (elapsed.TotalMilliseconds * 1000.0 / float n) ((b - a) / int64 n)
    let spliced = match PageParts.Splice(ReadOnlyMemory<byte> textBytes, offsets) with Ok p -> p | Error _ -> failwith "parts"
    time "splice (text not copied)" iterations (fun () -> PageParts.Splice(ReadOnlyMemory<byte> textBytes, offsets) |> ignore)
    time "splice (text copied)" iterations (fun () -> PageParts.Splice(ReadOnlyMemory<byte>(Array.copy textBytes), offsets) |> ignore)
    time "sha256 of the text" iterations (fun () -> Security.Cryptography.SHA256.HashData textBytes |> ignore)
    time "etag" iterations (fun () -> spliced.Etag() |> ignore)
    time "gzip" iterations (fun () -> spliced.Gzip 0u |> ignore)
    time "copy of the text" iterations (fun () -> Array.copy textBytes |> ignore)
    let mutable tCopy, tSplice, tEtag, tGzip = 0L, 0L, 0L, 0L
    for _ in 1..iterations do
        let t0 = Diagnostics.Stopwatch.GetTimestamp()
        let copy = Array.copy textBytes
        let t1 = Diagnostics.Stopwatch.GetTimestamp()
        let parts = match PageParts.Splice(ReadOnlyMemory<byte> copy, offsets) with Ok p -> p | Error _ -> failwith "parts"
        let t2 = Diagnostics.Stopwatch.GetTimestamp()
        parts.Etag() |> ignore
        let t3 = Diagnostics.Stopwatch.GetTimestamp()
        parts.Gzip 0u |> ignore
        let t4 = Diagnostics.Stopwatch.GetTimestamp()
        tCopy <- tCopy + (t1 - t0)
        tSplice <- tSplice + (t2 - t1)
        tEtag <- tEtag + (t3 - t2)
        tGzip <- tGzip + (t4 - t3)
    let us (t: int64) = float t * 1e6 / float Diagnostics.Stopwatch.Frequency / float iterations
    printfn "  in the loop: copy %.2f splice %.2f etag %.2f gzip %.2f us" (us tCopy) (us tSplice) (us tEtag) (us tGzip)
    printfn "body %d bytes, gzip member %d bytes: %.2f us per page (splice, etag, gzip), %d bytes allocated" body length (elapsed.TotalMilliseconds * 1000.0 / float iterations) ((a1 - a0) / int64 iterations)

[<EntryPoint>]
let main argv =
    if argv.Length > 0 && argv[0] = "micro" then
        micro (if argv.Length > 1 then int argv[1] else 20000)
        exit 0
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
