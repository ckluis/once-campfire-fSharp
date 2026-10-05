// Port of the #[cfg(test)] module in rust/crates/kit/src/front/tls.rs
//
// Not ported: `returning_clients_resume_their_sessions_by_ticket`. It reads a client's `handshake_kind`
// (full or resumed) from rustls; `SslStream` doesn't say whether a handshake resumed a session, so there
// is nothing to assert it with. Kestrel on Linux (OpenSSL) issues TLS 1.3 session tickets by default,
// and nothing in this code turns them off.
module Campfire.Kit.Tests.FrontTlsTests

open System
open System.IO
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Xunit
open Campfire.Kit

let private certs () : CertManager =
    new CertManager(
        { DirectoryUrl = "https://acme.invalid/directory"
          ExternalAccount = ValueNone
          StoragePath = Path.Combine(Path.GetTempPath(), "campfire-front-tls-test")
          Domains = [ "chat.example.com" ]
          ChallengeTypes = []
          DirectoryRoot = ValueNone }
    )

let private get (meth: string) (uri: string) (host: string) : Task<int * IHeaderDictionary * string> =
    task {
        let ctx = DefaultHttpContext()
        ctx.Request.Method <- meth
        (nonNull (ctx.Features.Get<IHttpRequestFeature>())).RawTarget <- uri
        ctx.Request.Headers["Host"] <- host
        let body = new MemoryStream()
        ctx.Response.Body <- body
        do! FrontTls.httpHandler (certs ()) ctx
        return ctx.Response.StatusCode, ctx.Response.Headers, Encoding.UTF8.GetString(body.ToArray())
    }

[<Fact>]
let ``redirects configured hosts to https`` () =
    task {
        let! status, headers, body = get "GET" "/rooms/1?x=1&y=%3C" "Chat.Example.com:80"
        Assert.Equal(301, status)
        Assert.Equal("https://chat.example.com/rooms/1?x=1&y=%3C", headers["Location"].ToString())
        Assert.Equal("close", headers["Connection"].ToString())
        Assert.Equal("text/html; charset=utf-8", headers["Content-Type"].ToString())
        Assert.Equal("<a href=\"https://chat.example.com/rooms/1?x=1&amp;y=%3C\">Moved Permanently</a>.\n\n", body)

        let! status, headers, body = get "POST" "/session" "chat.example.com"
        Assert.Equal(301, status)
        Assert.False(headers.ContainsKey "Content-Type")
        Assert.Equal("", body)
    }

[<Fact>]
let ``refuses other hosts`` () =
    task {
        let! status, _, body = get "GET" "/" "evil.example.com"
        Assert.Equal(421, status)
        Assert.Equal("Misdirected Request\n", body)
        let! status, _, _ = get "GET" "/" "[::1]:80"
        Assert.Equal(421, status)
    }

[<Fact>]
let ``answers http01 challenges`` () =
    task {
        let! status, _, body = get "GET" "/.well-known/acme-challenge/abc" "chat.example.com"
        Assert.Equal((404, "acme/autocert: certificate cache miss\n"), (status, body))
        let! status, _, _ = get "GET" "/.well-known/acme-challenge/abc" "chat.example.com:80"
        Assert.Equal(403, status)
    }

[<Fact>]
let ``a client hello is read for the protocols it offers`` () =
    // A ClientHello as .NET's `SslStream` sends it, captured from a handshake offering `acme-tls/1` alone,
    // and one offering `h2` and `http/1.1`: built here byte by byte (TLS 1.2 record, empty session id).
    let hello (protocols: string list) : byte[] =
        let names = protocols |> List.collect (fun p -> [ byte p.Length ] @ (Encoding.ASCII.GetBytes p |> List.ofArray))
        let alpn = [ 0uy; byte (names.Length + 2) ] // not exact: see below
        ignore alpn
        let list = [ byte (names.Length >>> 8); byte names.Length ] @ names
        let alpnExtension = [ 0uy; 16uy; byte (list.Length >>> 8); byte list.Length ] @ list
        let serverName = [ 0uy; 0uy; 0uy; 0uy ]
        let extensions = serverName @ alpnExtension
        let body =
            [ 3uy; 3uy ] @ List.replicate 32 0uy @ [ 0uy ] @ [ 0uy; 2uy; 0x13uy; 0x01uy ] @ [ 1uy; 0uy ]
            @ [ byte (extensions.Length >>> 8); byte extensions.Length ] @ extensions
        let handshake = [ 1uy; byte (body.Length >>> 16); byte (body.Length >>> 8); byte body.Length ] @ body
        Array.ofList ([ 0x16uy; 3uy; 1uy; byte (handshake.Length >>> 8); byte handshake.Length ] @ handshake)
    Assert.True(FrontTls.clientHelloOffers (hello [ "acme-tls/1" ]) "acme-tls/1")
    Assert.False(FrontTls.clientHelloOffers (hello [ "h2"; "http/1.1" ]) "acme-tls/1")
    Assert.True(FrontTls.clientHelloOffers (hello [ "h2"; "acme-tls/1" ]) "acme-tls/1")
    // Not a ClientHello, or cut short.
    Assert.False(FrontTls.clientHelloOffers "GET / HTTP/1.1\r\n"B "acme-tls/1")
    Assert.False(FrontTls.clientHelloOffers ((hello [ "acme-tls/1" ])[0..30]) "acme-tls/1")
