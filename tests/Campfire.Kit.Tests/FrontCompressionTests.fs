// Port of the #[cfg(test)] module in rust/crates/kit/src/front/compression.rs. Rust's tests of
// `Compression::apply` run it on a `Response<Body>`; here that logic is the front handler's, so those
// run on the front server (`FrontTests`' app, and the app below).
module Campfire.Kit.Tests.FrontCompressionTests

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Xunit
open Campfire.Kit
open Campfire.Kit.Tests.FrontHarness

[<Fact>]
let ``selects zstd at equal quality`` () =
    let select = FrontCompression.selectEncoding
    Assert.Equal(Encoding.Zstd, select "GET" "gzip, deflate, br, zstd")
    Assert.Equal(Encoding.Gzip, select "GET" "gzip, zstd;q=0.5")
    Assert.Equal(Encoding.Gzip, select "GET" "gzip")
    Assert.Equal(Encoding.Zstd, select "GET" "zstd")
    Assert.Equal(Encoding.NoEncoding, select "GET" "gzip;q=0, br")
    Assert.Equal(Encoding.NoEncoding, select "GET" null)
    Assert.Equal(Encoding.NoEncoding, select "HEAD" "gzip")

[<Fact>]
let ``filters compressed types`` () =
    let filter = FrontCompression.contentTypeFilter
    Assert.True(filter "text/html; charset=utf-8")
    Assert.True(filter "image/svg+xml")
    Assert.False(filter "image/png")
    Assert.False(filter "image/jpeg")
    Assert.False(filter "video/mp4")
    Assert.False(filter "application/zip")
    Assert.False(filter "application/gzip")

[<Fact>]
let ``crc32c matches the standard check value`` () =
    Assert.Equal(0xE3069283u, FrontCompression.crc32c (ReadOnlySpan<byte>("123456789"B)))
    // Whichever of the instruction and table versions runs here, they agree on lengths around the 8-byte steps.
    let data = Array.init 100 (fun i -> byte (i * 7))
    let bytewise =
        let mutable crc = ~~~0u
        for b in data do
            crc <- crc ^^^ uint32 b
            for _ in 0..7 do
                crc <- if crc &&& 1u <> 0u then (crc >>> 1) ^^^ 0x82F63B78u else crc >>> 1
        ~~~crc
    Assert.Equal(bytewise, FrontCompression.crc32c (ReadOnlySpan<byte> data))

[<Fact>]
let ``jitter padding is a prefix of the padding string`` () =
    Assert.Equal("Padding-Padding-Padding-Padding-P", FrontCompression.jitterPadding 32L)
    Assert.Equal("", FrontCompression.jitterPadding 0L)
    let padding = FrontCompression.jitterPadding 32L
    for length in [ 0; 1; 100; 70_000 ] do
        let body = Array.init length (fun i -> byte i)
        let jitter = nonNull (FrontCompression.jitterFor padding (ReadOnlySpan<byte> body))
        Assert.True(jitter.Length >= 1 && jitter.Length <= 32 && padding.StartsWith jitter)
    Assert.Null(FrontCompression.jitterFor "" (ReadOnlySpan<byte>([| 1uy |])))

[<Fact>]
let ``sniffs the types go sniffs`` () =
    let detect (text: string) = FrontCompression.detectContentType (ReadOnlySpan<byte>(Encoding.UTF8.GetBytes text))
    Assert.Equal("text/html; charset=utf-8", detect "<!DOCTYPE html><p>")
    Assert.Equal("text/html; charset=utf-8", detect "  \n<html>")
    Assert.Equal("text/xml; charset=utf-8", detect "<?xml version=\"1.0\"?>")
    Assert.Equal("text/plain; charset=utf-8", detect "plain words")
    Assert.Equal("application/pdf", detect "%PDF-1.4")
    Assert.Equal("application/octet-stream", FrontCompression.detectContentType (ReadOnlySpan<byte>([| 0uy; 1uy; 2uy |])))
    Assert.Equal("image/png", FrontCompression.detectContentType (ReadOnlySpan<byte>([| 0x89uy; 80uy; 78uy; 71uy; 13uy; 10uy; 26uy; 10uy |])))

let private gunzip (bytes: byte[]) : byte[] * string =
    use input = new MemoryStream(bytes)
    use gzip = new GZipStream(input, CompressionMode.Decompress)
    use output = new MemoryStream()
    gzip.CopyTo output
    // The header's comment, when there is one (FLG bit 3 set): up to the first NUL after the 10-byte header.
    let comment =
        if bytes[3] &&& 0x10uy <> 0uy then
            let nul = Array.IndexOf(bytes, 0uy, 10)
            Encoding.ASCII.GetString(bytes, 10, nul - 10)
        else
            ""
    output.ToArray(), comment

let private unzstd (bytes: byte[]) : byte[] =
    use input = new MemoryStream(bytes)
    use zstd = new ZstdSharp.DecompressionStream(input)
    use output = new MemoryStream()
    zstd.CopyTo output
    output.ToArray()

/// An app answering `/c/<case>` with the responses compression.rs's tests built.
let private compressionApp (app: IApplicationBuilder) : unit =
    app.Run(
        RequestDelegate(fun ctx ->
            let response = ctx.Response
            let body (text: string) = response.WriteAsync text
            match ctx.Request.Path.Value with
            | "/c/html" ->
                response.ContentType <- "text/html; charset=utf-8"
                body (String.replicate 200 "hello campfire ")
            | "/c/css" ->
                response.ContentType <- "text/css"
                body (String.replicate 200 "hello campfire ")
            | "/c/encoded" ->
                response.ContentType <- "text/html"
                response.Headers["Content-Encoding"] <- "gzip"
                response.Headers["Vary"] <- "Accept-Encoding"
                body (String('x', 2000))
            | "/c/small" ->
                response.ContentType <- "text/html"
                body "small"
            | "/c/png" ->
                response.ContentType <- "image/png"
                body (String('x', 2000))
            | "/c/plain" ->
                response.ContentType <- "text/html"
                body (String('x', 2000))
            | "/c/sniffed" -> body (String.replicate 100 "<!DOCTYPE html><p>")
            | "/c/failing" ->
                response.ContentType <- "text/html"
                response.ContentLength <- 100000L
                task {
                    do! response.Body.WriteAsync(ReadOnlyMemory<byte>(Encoding.ASCII.GetBytes(String.replicate 100 "hello campfire ")))
                    do! response.Body.FlushAsync()
                    failwith "disk gone"
                }
                :> Task
            | "/c/private" ->
                response.Headers["Cache-Control"] <- "private"
                response.ContentType <- "text/html"
                body (String('x', 2000))
            | "/c/cookie" ->
                response.Headers.Append("Set-Cookie", "a=b")
                response.ContentType <- "text/html"
                body (String('x', 2000))
            | "/c/varied" ->
                response.Headers["Vary"] <- "Accept"
                response.ContentType <- "text/html"
                body "x"
            | _ ->
                response.StatusCode <- 404
                Task.CompletedTask)
    )

let private get (server: FrontServer) (path: string) (encoding: string) : Task<Reply> =
    exchange server.Http (getRequest path (if encoding = "" then "" else $"Accept-Encoding: {encoding}\r\n"))

[<Fact>]
let ``compresses unencoded bodies with zstd`` () =
    task {
        let! server = startWith [] compressionApp
        let body = String.replicate 200 "hello campfire "
        let! reply = get server "/c/html" "zstd"
        Assert.Equal(Some "zstd", reply.Get "content-encoding")
        Assert.Equal<string list>([ "Accept-Encoding" ], reply.All "vary")
        Assert.Equal<byte[]>(Encoding.UTF8.GetBytes body, unzstd reply.Body)
        do! server.Stop()
    }

[<Fact>]
let ``compresses with gzip and a jitter comment`` () =
    task {
        let! server = startWith [] compressionApp
        let body = String.replicate 200 "hello campfire "
        let! reply = get server "/c/css" "gzip"
        Assert.Equal(Some "gzip", reply.Get "content-encoding")
        let decoded, comment = gunzip reply.Body
        Assert.Equal(body, Encoding.UTF8.GetString decoded)
        Assert.True(comment <> "" && comment.Length <= 32 && "Padding-Padding-Padding-Padding-P".StartsWith comment, comment)
        do! server.Stop()
    }

[<Fact>]
let ``a failing body fails the response`` () =
    task {
        let! server = startWith [] compressionApp
        // The body is cut short, not completed: its declared length never arrives.
        let! reply =
            task {
                try
                    return! get server "/c/failing" "gzip"
                with _ ->
                    return { Status = 0; Headers = []; Body = [||] }
            }
        Assert.Equal(None, reply.Get "content-encoding")
        Assert.True(reply.Body.Length < 100000)
        do! server.Stop()
    }

[<Fact>]
let ``leaves encoded small and incompressible bodies alone`` () =
    task {
        let! server = startWith [] compressionApp
        // Bypassed requests (a POST here) get `Vary: Accept-Encoding` added to the response's own.
        let! reply = exchange server.Http "POST /c/encoded HTTP/1.1\r\nHost: x\r\nConnection: close\r\nContent-Length: 0\r\nAccept-Encoding: gzip\r\n\r\n"
        Assert.Equal(Some "gzip", reply.Get "content-encoding")
        Assert.Equal<string list>([ "Accept-Encoding"; "Accept-Encoding" ], reply.All "vary")
        Assert.Equal(2000, reply.Body.Length)

        let! small = get server "/c/small" "gzip"
        Assert.Equal(None, small.Get "content-encoding")
        let! png = get server "/c/png" "gzip"
        Assert.Equal(None, png.Get "content-encoding")
        let! none = get server "/c/plain" ""
        Assert.Equal(None, none.Get "content-encoding")
        Assert.Equal(2000, none.Body.Length)
        do! server.Stop()
    }

[<Fact>]
let ``vary is replaced by the cache and appended otherwise`` () =
    task {
        let! server = startWith [] compressionApp
        // A GET goes through the cache's path: the response's own Vary is kept.
        let! varied = get server "/c/varied" "gzip"
        Assert.Equal<string list>([ "Accept" ], varied.All "vary")
        // A POST is bypassed: Accept-Encoding is added to it.
        let! posted = exchange server.Http "POST /c/varied HTTP/1.1\r\nHost: x\r\nConnection: close\r\nContent-Length: 0\r\nAccept-Encoding: gzip\r\n\r\n"
        Assert.Equal<string list>([ "Accept-Encoding"; "Accept" ], posted.All "vary")
        do! server.Stop()
    }

[<Fact>]
let ``sniffs a missing content type`` () =
    task {
        let! server = startWith [] compressionApp
        let! reply = get server "/c/sniffed" "gzip"
        Assert.Equal(Some "text/html; charset=utf-8", reply.Get "content-type")
        Assert.Equal(Some "gzip", reply.Get "content-encoding")
        do! server.Stop()
    }

[<Fact>]
let ``guard vetoes user specific responses`` () =
    task {
        let! server = startWith [ "GZIP_COMPRESSION_DISABLE_ON_AUTH", "true" ] compressionApp
        let! withCookie = exchange server.Http (getRequest "/c/plain" "Accept-Encoding: gzip\r\nCookie: a=b\r\n")
        Assert.Equal(None, withCookie.Get "content-encoding")
        let! priv = get server "/c/private" "gzip"
        Assert.Equal(None, priv.Get "content-encoding")
        let! setting = get server "/c/cookie" "gzip"
        Assert.Equal(None, setting.Get "content-encoding")
        let! plain = get server "/c/plain" "gzip"
        Assert.Equal(Some "gzip", plain.Get "content-encoding")
        do! server.Stop()
    }
