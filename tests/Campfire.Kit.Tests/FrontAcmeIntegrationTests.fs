// The ACME test in rust/crates/kit/tests/front.rs (`acme_tls_alpn_certificate_cached_and_reused`) against
// a stub CA (AcmeStub), which also lets HTTP-01, the fallback from one challenge to the other,
// external account binding and concurrent handshakes be tested. With PEBBLE_MINICA set, the original
// test runs against a Pebble CA too:
//
//   docker run -d --name pebble --network host --add-host campfire.test:127.0.0.1 \
//     -e PEBBLE_VA_NOSLEEP=1 -e PEBBLE_WFE_NONCEREJECT=0 ghcr.io/letsencrypt/pebble:latest
//   docker cp pebble:/test/certs/pebble.minica.pem /tmp/pebble.minica.pem
//   PEBBLE_MINICA=/tmp/pebble.minica.pem dotnet test --project tests/Campfire.Kit.Tests ...
//
// Pebble validates TLS-ALPN-01 on port 5001, so that test serves HTTPS there.
module Campfire.Kit.Tests.FrontAcmeIntegrationTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Security
open System.Net.Sockets
open System.Security.Cryptography.X509Certificates
open System.Text
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open Campfire.Kit
open Campfire.Kit.Tests.AcmeStub
open Campfire.Kit.Tests.FrontHarness
open Campfire.Kit.Tests.FrontTests

let private tempDir () : string =
    let path = Path.Combine(Path.GetTempPath(), "campfire-acme-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory path |> ignore
    path

let private acmeOptions (stub: AcmeStub) (domain: string) (storage: string) (types: AcmeChallengeType list) : AcmeOptions =
    { DirectoryUrl = stub.DirectoryUrl
      ExternalAccount = ValueNone
      StoragePath = storage
      Domains = [ domain ]
      ChallengeTypes = types
      DirectoryRoot = ValueNone }

/// A TLS handshake with SNI `domain` on 127.0.0.1:`port`: the negotiated protocol and the leaf's issuer.
let private handshake (port: int) (domain: string) : Task<string * string> =
    task {
        use client = new TcpClient()
        do! client.ConnectAsync(IPAddress.Loopback, port)
        use ssl = new SslStream(client.GetStream(), false)
        let options = SslClientAuthenticationOptions(TargetHost = domain)
        options.ApplicationProtocols <- Collections.Generic.List<SslApplicationProtocol>([ SslApplicationProtocol.Http2; SslApplicationProtocol.Http11 ])
        options.RemoteCertificateValidationCallback <- RemoteCertificateValidationCallback(fun _ _ _ _ -> true)
        do! ssl.AuthenticateAsClientAsync options
        use certificate = new X509Certificate2(nonNull ssl.RemoteCertificate)
        return Encoding.ASCII.GetString(ssl.NegotiatedApplicationProtocol.Protocol.ToArray()), certificate.Issuer
    }

let private startTls (vars: (string * string) list) (acme: AcmeOptions) (http: int) (https: int) : Task<FrontServer> =
    task {
        let renders = Counter()
        let logging: ILoggerFactory =
            if isNull (Environment.GetEnvironmentVariable "ACME_TEST_LOG") then
                NullLoggerFactory.Instance
            else
                LoggerFactory.Create(fun b -> b.AddConsole().SetMinimumLevel(LogLevel.Debug) |> ignore)
        let! started = tryStart (vars @ [ "TLS_DOMAIN", List.head acme.Domains ]) (testApp renders) (ValueSome acme) logging http https
        return (match started with Some s -> s | None -> failwith "front server didn't start")
    }

[<Fact>]
let ``a certificate is obtained by tls-alpn-01, cached and reused`` () =
    task {
        let stub = new AcmeStub(None)
        do! stub.Start()
        let storage = tempDir ()
        let domain = "campfire.test"
        let http, https = freePort (), freePort ()
        stub.ValidationTlsPort <- https
        let acme = acmeOptions stub domain storage [ TlsAlpn01 ]
        let! server = startTls [] acme http https

        let! alpn, issuer =
            task {
                try
                    return! handshake https domain
                with e ->
                    let failures = String.Join(" | ", stub.ValidationFailures)
                    return failwith (e.Message + "; validation: " + failures)
            }
        Assert.Equal("h2", alpn)
        Assert.Contains("Stub CA", issuer)
        let cached = File.ReadAllText(Path.Combine(storage, domain))
        Assert.StartsWith("-----BEGIN EC PRIVATE KEY-----", cached)
        Assert.Contains("-----BEGIN CERTIFICATE-----", cached)
        Assert.StartsWith("-----BEGIN EC PRIVATE KEY-----", File.ReadAllText(Path.Combine(storage, "acme_account+key")))

        let! redirect = exchange http "GET /rooms?x=1 HTTP/1.1\r\nHost: campfire.test\r\nConnection: close\r\n\r\n"
        Assert.Equal((301, Some "https://campfire.test/rooms?x=1"), (redirect.Status, redirect.Get "location"))
        let! misdirected = exchange http "GET / HTTP/1.1\r\nHost: other.test\r\nConnection: close\r\n\r\n"
        Assert.Equal(421, misdirected.Status)

        // The app answers over HTTPS, HTTP/2 and all, as the proxy would have it.
        use handler = new SocketsHttpHandler()
        handler.SslOptions.TargetHost <- domain
        handler.SslOptions.RemoteCertificateValidationCallback <- RemoteCertificateValidationCallback(fun _ _ _ _ -> true)
        use client = new HttpClient(handler)
        use request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{https}/headers", Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact)
        request.Headers.Host <- domain
        use! response = client.SendAsync request
        let! text = response.Content.ReadAsStringAsync()
        Assert.Equal(HttpVersion.Version20, response.Version)
        Assert.Contains("proto=https", text)
        Assert.Contains("host=campfire.test", text)
        do! server.Stop()

        // A restart serves the cached certificate without asking the CA.
        let orders = stub.NewOrders
        let offline = { acme with DirectoryUrl = "http://127.0.0.1:9/dir" }
        let! server = startTls [] offline http https
        let! _, again = handshake https domain
        Assert.Equal(issuer, again)
        Assert.Equal(orders, stub.NewOrders)
        do! server.Stop()
        do! (stub :> IAsyncDisposable).DisposeAsync()
    }

[<Fact>]
let ``a certificate is obtained by http-01`` () =
    task {
        let stub = new AcmeStub(None)
        do! stub.Start()
        let storage = tempDir ()
        let domain = "campfire.test"
        let http, https = freePort (), freePort ()
        stub.ValidationHttpPort <- http
        let! server = startTls [] (acmeOptions stub domain storage [ Http01 ]) http https
        let! _, issuer = handshake https domain
        Assert.Contains("Stub CA", issuer)
        Assert.True(File.Exists(Path.Combine(storage, domain)))
        do! server.Stop()
        do! (stub :> IAsyncDisposable).DisposeAsync()
    }

[<Fact>]
let ``http-01 is tried when tls-alpn-01 fails`` () =
    task {
        let stub = new AcmeStub(None)
        do! stub.Start()
        let domain = "campfire.test"
        let http, https = freePort (), freePort ()
        // The CA looks for TLS-ALPN-01 where nothing listens.
        stub.ValidationTlsPort <- freePort ()
        stub.ValidationHttpPort <- http
        let! server = startTls [] (acmeOptions stub domain (tempDir ()) [ TlsAlpn01; Http01 ]) http https
        let! _, issuer = handshake https domain
        Assert.Contains("Stub CA", issuer)
        Assert.Equal(2, stub.NewOrders)
        Assert.Single(stub.ValidationFailures) |> ignore
        do! server.Stop()
        do! (stub :> IAsyncDisposable).DisposeAsync()
    }

[<Fact>]
let ``external account binding is sent when configured`` () =
    task {
        let key = [| 1uy; 2uy; 3uy; 4uy; 5uy; 6uy; 7uy; 8uy |]
        let stub = new AcmeStub(Some("kid-1", key))
        do! stub.Start()
        let domain = "campfire.test"
        let http, https = freePort (), freePort ()
        stub.ValidationTlsPort <- https
        let withEab = { acmeOptions stub domain (tempDir ()) [ TlsAlpn01 ] with ExternalAccount = ValueSome("kid-1", key) }
        let! server = startTls [] withEab http https
        let! _, issuer = handshake https domain
        Assert.Contains("Stub CA", issuer)
        Assert.Equal(1, stub.Registrations)
        do! server.Stop()

        // Without it the CA refuses the account, and the handshake fails.
        let http, https = freePort (), freePort ()
        stub.ValidationTlsPort <- https
        let! server = startTls [] (acmeOptions stub domain (tempDir ()) [ TlsAlpn01 ]) http https
        let! failed =
            task {
                try
                    let! _ = handshake https domain
                    return false
                with _ ->
                    return true
            }
        Assert.True(failed, "no certificate without the binding")
        do! server.Stop()
        do! (stub :> IAsyncDisposable).DisposeAsync()
    }

[<Fact>]
let ``concurrent handshakes make one order and other names get none`` () =
    task {
        let stub = new AcmeStub(None)
        do! stub.Start()
        let domain = "campfire.test"
        let http, https = freePort (), freePort ()
        stub.ValidationTlsPort <- https
        let! server = startTls [] (acmeOptions stub domain (tempDir ()) [ TlsAlpn01 ]) http https
        let! results = Task.WhenAll [| for _ in 1..6 -> handshake https domain |]
        Assert.All(results, fun (alpn, issuer) -> Assert.True(alpn = "h2" && issuer.Contains "Stub CA"))
        Assert.Equal(1, stub.NewOrders)
        Assert.Equal(1, stub.Registrations)
        // A name outside TLS_DOMAIN gets no certificate and costs no order.
        let! refused =
            task {
                try
                    let! _ = handshake https "evil.example.com"
                    return false
                with _ ->
                    return true
            }
        Assert.True refused
        Assert.Equal(1, stub.NewOrders)
        do! server.Stop()
        do! (stub :> IAsyncDisposable).DisposeAsync()
    }

[<Fact>]
let ``a certificate is obtained from pebble when there is one`` () =
    task {
        match Environment.GetEnvironmentVariable "PEBBLE_MINICA" with
        | null
        | "" -> eprintfn "skipped: PEBBLE_MINICA isn't set"
        | root ->
            let storage = tempDir ()
            let domain = "campfire.test"
            let acme =
                { DirectoryUrl = "https://localhost:14000/dir"
                  ExternalAccount = ValueNone
                  StoragePath = storage
                  Domains = [ domain ]
                  ChallengeTypes = [ TlsAlpn01 ]
                  DirectoryRoot = ValueSome root }
            let! server = startTls [] acme 5002 5001
            let! alpn, issuer = handshake 5001 domain
            Assert.Equal("h2", alpn)
            Assert.Contains("Pebble", issuer)
            do! server.Stop()
    }

/// `openssl s_client` against `port` with SNI `domain`, sending a request so that the server answers and
/// closes (a TLS 1.3 client only reads its session tickets after the handshake). `session` is the file
/// the session is saved to or, with `resume`, read from. Returns what s_client printed, or None when
/// there is no openssl to run.
let private sClient (port: int) (domain: string) (session: string) (resume: bool) : Task<string option> =
    task {
        let info = Diagnostics.ProcessStartInfo("openssl", RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true)
        for argument in [ "s_client"; "-ign_eof"; "-connect"; $"127.0.0.1:{port}"; "-servername"; domain; (if resume then "-sess_in" else "-sess_out"); session ] do
            info.ArgumentList.Add argument
        let started =
            try
                Option.ofObj (Diagnostics.Process.Start info)
            with :? ComponentModel.Win32Exception ->
                None
        match started with
        | None -> return None
        | Some proc ->
            use proc = proc
            do! proc.StandardInput.WriteAsync $"GET /headers HTTP/1.1\r\nHost: {domain}\r\nConnection: close\r\n\r\n"
            proc.StandardInput.Close()
            use timeout = new Threading.CancellationTokenSource(TimeSpan.FromSeconds 30.0)
            let output = proc.StandardOutput.ReadToEndAsync timeout.Token
            let errors = proc.StandardError.ReadToEndAsync timeout.Token
            do! proc.WaitForExitAsync timeout.Token
            let! output = output
            let! errors = errors
            return Some(output + errors)
    }

/// Port of `returning_clients_resume_their_sessions_by_ticket` in rust/crates/kit/src/front/tls.rs. rustls
/// reports a handshake's kind; `SslStream` doesn't, so the client here is `openssl s_client`, which prints
/// `Reused` for a resumed session and `New` for a full handshake. Kestrel's server side issues TLS 1.3
/// tickets through OpenSSL on Linux, where the front runs; on macOS `SslStream` issues none, so the test
/// skips there (`bin/linux-tests Kit` runs it in the toolchain container).
[<Fact>]
let ``returning clients resume their sessions by ticket`` () =
    task {
        if not (OperatingSystem.IsLinux()) then
            Assert.Skip "SslStream issues no session tickets outside Linux; run it with bin/linux-tests Kit"
        let storage = tempDir ()
        let domain = "campfire.test"
        let chain, key = FrontAcmeTests.selfSigned [ domain ]
        File.WriteAllText(Path.Combine(storage, domain), AcmeFiles.cacheEntry key chain)
        let acme =
            { DirectoryUrl = "https://acme.invalid/directory"
              ExternalAccount = ValueNone
              StoragePath = storage
              Domains = [ domain ]
              ChallengeTypes = []
              DirectoryRoot = ValueNone }
        let http, https = freePort (), freePort ()
        let! server = startTls [] acme http https
        try
            let session = Path.Combine(storage, "session.pem")
            match! sClient https domain session false with
            | None -> Assert.Skip "openssl isn't installed, so the resumed handshake can't be observed"
            | Some first ->
                Assert.True(File.Exists session, "the first connection was issued a session: " + first)
                Assert.Contains("New, ", first)
                // A handful of other clients come between, as in the Rust test (which pushes the session
                // out of rustls's cache): what resumes it is the ticket, not a server-side cache entry.
                for n in 1..20 do
                    let! _ = sClient https domain (Path.Combine(storage, $"other{n}.pem")) false
                    ()
                match! sClient https domain session true with
                | Some second -> Assert.Contains("Reused, ", second)
                | None -> failwith "openssl vanished"
        finally
            server.Stop().Wait()
    }
