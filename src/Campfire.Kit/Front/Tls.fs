// Port of rust/crates/kit/src/front/tls.rs
//
// TLS for the HTTPS port (autocert's `TLSConfig`), and the HTTP port's handler when TLS is on
// (`manager.HTTPHandler(httpRedirectHandler)`): ACME HTTP-01 responses, and a permanent redirect to
// HTTPS for everything else.
//
// Kestrel does the TLS itself. What `LazyConfigAcceptor` gave Rust, the server name and the offered
// ALPN protocols before the certificate is chosen, comes here from the handshake callback (the name)
// and from reading the ClientHello off the connection first (the protocols, which `SslStream` doesn't
// show a callback), so a TLS-ALPN-01 validation gets its challenge certificate and nobody else does.
namespace Campfire.Kit

open System
open System.Buffers
open System.Buffers.Binary
open System.IO.Pipelines
open System.Net.Security
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Connections
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.AspNetCore.Server.Kestrel.Https

module FrontTls =
    let AcmeTlsAlpn = "acme-tls/1"

    /// The connection item that says the client offered `acme-tls/1`.
    let private acmeItem = "campfire.front.acme-tls-alpn"

    /// Whether a TLS record that holds a ClientHello offers `protocol` in its ALPN extension.
    let clientHelloOffers (record: byte[]) (protocol: string) : bool =
        let wanted = Encoding.ASCII.GetBytes protocol
        let u16 (at: int) = (int record[at] <<< 8) ||| int record[at + 1]
        let length = record.Length
        // Record header (5), handshake header (4), client version (2), random (32).
        if length < 44 || record[0] <> 0x16uy || record[5] <> 0x01uy then
            false
        else
            let mutable p = 43
            let mutable ok = true
            // session_id
            p <- p + 1 + int record[p]
            // cipher_suites
            if ok && p + 2 <= length then p <- p + 2 + u16 p else ok <- false
            // compression_methods
            if ok && p + 1 <= length then p <- p + 1 + int record[p] else ok <- false
            if not ok || p + 2 > length then
                false
            else
                let extensionsEnd = min length (p + 2 + u16 p)
                p <- p + 2
                let mutable offered = false
                while not offered && p + 4 <= extensionsEnd do
                    let kind = u16 p
                    let size = u16 (p + 2)
                    p <- p + 4
                    if kind = 16 && p + 2 <= extensionsEnd then
                        // ProtocolNameList: a 2-byte length, then 1-byte-length names.
                        let listEnd = min extensionsEnd (p + 2 + u16 p)
                        let mutable q = p + 2
                        while not offered && q < listEnd do
                            let nameLength = int record[q]
                            if q + 1 + nameLength <= listEnd && ReadOnlySpan<byte>(record, q + 1, nameLength).SequenceEqual(ReadOnlySpan<byte> wanted) then
                                offered <- true
                            q <- q + 1 + nameLength
                    p <- p + size
                offered

    /// A connection middleware that reads a ClientHello without consuming it and notes whether it offers
    /// `acme-tls/1`. A client that sends nothing for `timeout` is dropped.
    let sniffAcme (timeout: TimeSpan) : Func<ConnectionContext, Func<Task>, Task> =
        Func<ConnectionContext, Func<Task>, Task>(fun connection next ->
            task {
                use cts = new CancellationTokenSource()
                if timeout > TimeSpan.Zero then cts.CancelAfter timeout
                let reader = connection.Transport.Input
                let mutable decided = false
                let mutable aborted = false
                try
                    while not decided do
                        let! result = reader.ReadAsync cts.Token
                        let buffer = result.Buffer
                        if buffer.Length >= 5L then
                            let header = buffer.Slice(0L, 5L).ToArray()
                            if header[0] <> 0x16uy then
                                decided <- true
                            else
                                let need = 5L + int64 (BinaryPrimitives.ReadUInt16BigEndian(ReadOnlySpan<byte>(header, 3, 2)))
                                if buffer.Length >= need then
                                    let record = buffer.Slice(0L, need).ToArray()
                                    if clientHelloOffers record AcmeTlsAlpn then connection.Items[acmeItem] <- box true
                                    decided <- true
                                elif result.IsCompleted then
                                    decided <- true
                        elif result.IsCompleted then
                            decided <- true
                        if decided then reader.AdvanceTo(buffer.Start, buffer.Start) else reader.AdvanceTo(buffer.Start, buffer.End)
                with :? OperationCanceledException ->
                    aborted <- true
                    connection.Abort()
                if not aborted then do! next.Invoke()
            }
            :> Task)

    /// What a handshake negotiates: the certificate for the client's server name, obtained first if
    /// need be, or (for a TLS-ALPN-01 validation) the challenge certificate, with `acme-tls/1` as the
    /// protocol.
    let handshakeOptions (certs: CertManager) (timeout: TimeSpan) : TlsHandshakeCallbackOptions =
        let options = TlsHandshakeCallbackOptions()
        options.HandshakeTimeout <- (if timeout > TimeSpan.Zero then timeout else TimeSpan.MaxValue)
        options.OnConnection <-
            Func<TlsHandshakeCallbackContext, ValueTask<SslServerAuthenticationOptions>>(fun context ->
                ValueTask<SslServerAuthenticationOptions>(
                    task {
                        let serverName = context.ClientHelloInfo.ServerName
                        let challenge =
                            match context.Connection.Items.TryGetValue acmeItem with
                            | true, _ -> true
                            | _ -> false
                        let sslOptions = SslServerAuthenticationOptions()
                        if challenge then
                            match certs.ChallengeCertificate serverName with
                            | null -> failwith "no TLS-ALPN-01 challenge for this name"
                            | certificate ->
                                sslOptions.ServerCertificateContext <- certificate.Context
                                sslOptions.ApplicationProtocols <- Collections.Generic.List<SslApplicationProtocol>([ SslApplicationProtocol(AcmeTlsAlpn) ])
                        else
                            match! certs.Certificate serverName with
                            | Error error -> failwith error
                            | Ok certificate ->
                                sslOptions.ServerCertificateContext <- certificate.Context
                                sslOptions.ApplicationProtocols <-
                                    Collections.Generic.List<SslApplicationProtocol>([ SslApplicationProtocol.Http2; SslApplicationProtocol.Http11 ])
                        return sslOptions
                    }
                ))
        options

    /// `http.Error`
    let private error (ctx: HttpContext) (status: int) (message: string) : Task =
        let body = Encoding.UTF8.GetBytes(message + "\n")
        ctx.Response.StatusCode <- status
        ctx.Response.ContentType <- "text/plain; charset=utf-8"
        ctx.Response.Headers["X-Content-Type-Options"] <- Microsoft.Extensions.Primitives.StringValues "nosniff"
        ctx.Response.ContentLength <- int64 body.Length
        ctx.Response.Body.WriteAsync(ReadOnlyMemory<byte> body).AsTask()

    /// `net.SplitHostPort`, or the host as given.
    let splitHostPort (host: string) : string =
        if host.StartsWith '[' then
            match host.IndexOf "]:" with
            | -1 -> host
            | at -> host.Substring(1, at - 1)
        else
            match host.LastIndexOf ':' with
            | -1 -> host
            | colon ->
                let name = host.Substring(0, colon)
                if name.Contains ':' then host else name

    let private htmlEscape (s: string) : string =
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&#34;").Replace("'", "&#39;")

    /// `httpRedirectHandler`: 301 to the same path over HTTPS for TLS_DOMAIN hosts, 421 for others.
    let private redirect (certs: CertManager) (ctx: HttpContext) (rawTarget: string | null) (host: string) : Task =
        let bareHost = splitHostPort host
        let allowed =
            match AcmeFiles.toAscii bareHost with
            | ValueSome ascii when certs.HostAllowed ascii -> ValueSome ascii
            | _ -> ValueNone
        ctx.Response.Headers["Connection"] <- Microsoft.Extensions.Primitives.StringValues "close"
        match allowed with
        | ValueNone -> error ctx 421 "Misdirected Request"
        | ValueSome host ->
            let struct (path, query, _) = FrontCache.splitTarget rawTarget
            let target = if query = "" && not ((match rawTarget with null -> "" | t -> t).Contains '?') then path else path + "?" + query
            let url = $"https://{host}{target}"
            let meth = ctx.Request.Method
            ctx.Response.StatusCode <- 301
            ctx.Response.Headers["Location"] <- Microsoft.Extensions.Primitives.StringValues url
            if meth = "GET" || meth = "HEAD" then ctx.Response.ContentType <- "text/html; charset=utf-8"
            if meth = "GET" then
                let body = Encoding.UTF8.GetBytes($"<a href=\"{htmlEscape url}\">Moved Permanently</a>.\n\n")
                ctx.Response.ContentLength <- int64 body.Length
                ctx.Response.Body.WriteAsync(ReadOnlyMemory<byte> body).AsTask()
            else
                ctx.Response.ContentLength <- 0L
                Task.CompletedTask

    /// The HTTP port's handler with TLS on.
    let httpHandler (certs: CertManager) (ctx: HttpContext) : Task =
        let rawTarget = match ctx.Features.Get<IHttpRequestFeature>() with null -> null | f -> f.RawTarget
        let struct (path, _, authority) = FrontCache.splitTarget rawTarget
        let host =
            match ctx.Request.Headers.Host.ToString() with
            | "" -> (match authority with null -> "" | a -> a)
            | host -> host
        if path.StartsWith("/.well-known/acme-challenge/", StringComparison.Ordinal) then
            if not (certs.HostAllowed host) then
                error ctx 403 $"acme/autocert: host \"{host}\" not configured in HostWhitelist"
            else
                match certs.HttpToken path with
                | null -> error ctx 404 "acme/autocert: certificate cache miss"
                | token ->
                    let body = Encoding.UTF8.GetBytes token
                    ctx.Response.ContentType <- "text/plain; charset=utf-8"
                    ctx.Response.ContentLength <- int64 body.Length
                    ctx.Response.Body.WriteAsync(ReadOnlyMemory<byte> body).AsTask()
        else
            redirect certs ctx rawTarget host
