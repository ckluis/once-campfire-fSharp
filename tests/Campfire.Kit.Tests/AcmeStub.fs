/// A small ACME CA for tests (RFC 8555, enough of it for `AcmeClient`): it checks each request's JWS
/// signature, nonce and URL, answers TLS-ALPN-01 and HTTP-01 challenges by connecting to the front
/// server being tested the way a real CA does, and signs the certificate for the order's CSR.
module Campfire.Kit.Tests.AcmeStub

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Net.Security
open System.Net.Sockets
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Campfire.Kit

let private b64 (bytes: byte[]) = Base64Url.encode (ReadOnlySpan<byte> bytes)
let private unb64 (text: string) = (Base64Url.decode text).Value

type private Order =
    { Id: int
      Domain: string
      Account: string
      mutable Status: string
      mutable AuthzStatus: string
      mutable Certificate: string | null }

[<Sealed>]
type AcmeStub(requireEab: (string * byte[]) option) =
    let caKey = ECDsa.Create ECCurve.NamedCurves.nistP256
    let caCertificate =
        let request = CertificateRequest("CN=Stub CA", caKey, HashAlgorithmName.SHA256)
        request.CertificateExtensions.Add(X509BasicConstraintsExtension(true, false, 0, true))
        request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays -1.0, DateTimeOffset.UtcNow.AddDays 365.0)
    let nonces = ConcurrentDictionary<string, bool>()
    let accounts = ConcurrentDictionary<string, ECDsa>()
    let orders = ConcurrentDictionary<int, Order>()
    let mutable nextOrder = 0
    let mutable newOrders = 0
    let mutable registrations = 0
    let mutable host: WebApplication | null = null
    let mutable baseUrl = ""
    let validationFailures = ConcurrentQueue<string>()

    /// The front server's HTTPS port, where TLS-ALPN-01 is validated.
    member val ValidationTlsPort = 0 with get, set

    /// The front server's HTTP port, where HTTP-01 is validated.
    member val ValidationHttpPort = 0 with get, set

    /// The orders made.
    member _.NewOrders = newOrders

    /// The accounts registered.
    member _.Registrations = registrations

    /// Why validations failed, for test messages.
    member _.ValidationFailures = validationFailures.ToArray()

    member _.CaSubject = caCertificate.Subject
    member _.DirectoryUrl = baseUrl + "/dir"

    member private _.Problem(ctx: HttpContext, status: int, kind: string, detail: string) : Task =
        ctx.Response.StatusCode <- status
        ctx.Response.ContentType <- "application/problem+json"
        ctx.Response.WriteAsync($"{{\"type\":\"urn:ietf:params:acme:error:{kind}\",\"detail\":\"{detail}\"}}")

    /// Checks a flattened JWS POST: nonce (single use), URL, and the signature, by the account's key.
    member private this.Verify(ctx: HttpContext, body: string) : Result<JsonElement * string * ECDsa * string, string * string> =
        use doc = JsonDocument.Parse body
        let root = doc.RootElement
        let protectedPart = root.GetProperty("protected").GetString() |> nonNull
        let payloadPart = root.GetProperty("payload").GetString() |> nonNull
        let signature = unb64 (root.GetProperty("signature").GetString() |> nonNull)
        let header = JsonDocument.Parse(Encoding.UTF8.GetString(unb64 protectedPart)).RootElement.Clone()
        let nonce = header.GetProperty("nonce").GetString() |> nonNull
        let url = header.GetProperty("url").GetString() |> nonNull
        if header.GetProperty("alg").GetString() <> "ES256" then
            Error("badSignatureAlgorithm", "ES256 only")
        elif not (nonces.TryRemove(nonce) |> fst) then
            Error("badNonce", "unknown or reused nonce")
        elif url <> baseUrl + ctx.Request.Path.Value then
            Error("unauthorized", $"url {url} is not the request's")
        else
            let found: (ECDsa | null) * string =
                match header.TryGetProperty "jwk" with
                | true, jwk ->
                    let key = ECDsa.Create()
                    key.ImportParameters(
                        ECParameters(
                            Curve = ECCurve.NamedCurves.nistP256,
                            Q = ECPoint(X = unb64 (jwk.GetProperty("x").GetString() |> nonNull), Y = unb64 (jwk.GetProperty("y").GetString() |> nonNull))
                        )
                    )
                    key, ""
                | _ ->
                    let kid = header.GetProperty("kid").GetString() |> nonNull
                    match accounts.TryGetValue kid with
                    | true, key -> key, kid
                    | _ -> null, kid
            match found with
            | null, kid -> Error("accountDoesNotExist", kid)
            | key, kid ->
                if not (key.VerifyData(Encoding.ASCII.GetBytes(protectedPart + "." + payloadPart), signature, HashAlgorithmName.SHA256)) then
                    Error("malformed", "bad signature")
                else
                    Ok(header, (if payloadPart = "" then "" else Encoding.UTF8.GetString(unb64 payloadPart)), key, kid)

    member private _.Thumbprint(key: ECDsa) : string =
        let p = key.ExportParameters false
        let jwk = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{b64 (nonNull p.Q.X)}\",\"y\":\"{b64 (nonNull p.Q.Y)}\"}}"
        b64 (SHA256.HashData(Encoding.UTF8.GetBytes jwk))

    member private this.KeyAuthorization(order: Order, token: string) : string =
        token + "." + this.Thumbprint(accounts[order.Account])

    member private this.Validate(order: Order, kind: string) : Task =
        task {
            try
                let token = (if kind = "alpn" then "tokalpn" else "tokhttp") + string order.Id
                let keyAuth = this.KeyAuthorization(order, token)
                if kind = "alpn" then
                    use client = new TcpClient()
                    do! client.ConnectAsync(IPAddress.Loopback, this.ValidationTlsPort)
                    use ssl = new SslStream(client.GetStream(), false)
                    let options = SslClientAuthenticationOptions(TargetHost = order.Domain)
                    options.ApplicationProtocols <- List<SslApplicationProtocol>([ SslApplicationProtocol("acme-tls/1") ])
                    options.RemoteCertificateValidationCallback <- RemoteCertificateValidationCallback(fun _ _ _ _ -> true)
                    do! ssl.AuthenticateAsClientAsync options
                    if ssl.NegotiatedApplicationProtocol <> SslApplicationProtocol("acme-tls/1") then failwith "no acme-tls/1 negotiated"
                    use certificate = new X509Certificate2(nonNull ssl.RemoteCertificate)
                    let extension = certificate.Extensions |> Seq.cast<X509Extension> |> Seq.tryFind (fun e -> (match e.Oid with null -> "" | oid -> (match oid.Value with null -> "" | v -> v)) = "1.3.6.1.5.5.7.1.31")
                    match extension with
                    | None -> failwith "no acmeIdentifier extension"
                    | Some extension ->
                        if not extension.Critical then failwith "acmeIdentifier isn't critical"
                        let expected = Array.append [| 0x04uy; 0x20uy |] (SHA256.HashData(Encoding.ASCII.GetBytes keyAuth))
                        if extension.RawData <> expected then failwith "wrong key authorization digest"
                    if not (certificate.GetNameInfo(X509NameType.DnsName, false) = order.Domain) then failwith "wrong name"
                else
                    use http = new HttpClient()
                    use request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{this.ValidationHttpPort}/.well-known/acme-challenge/{token}")
                    request.Headers.Host <- order.Domain
                    use! response = http.SendAsync request
                    let! text = response.Content.ReadAsStringAsync()
                    if text <> keyAuth then failwith $"http-01 answered {int response.StatusCode} {text}"
                order.AuthzStatus <- "valid"
                order.Status <- "ready"
            with e ->
                validationFailures.Enqueue $"{kind}: {e.Message}"
                order.AuthzStatus <- "invalid"
                order.Status <- "invalid"
        }

    member private this.OrderJson(order: Order) : string =
        let certificate =
            match order.Certificate with
            | null -> ""
            | _ -> $",\"certificate\":\"{baseUrl}/cert/{order.Id}\""
        $"{{\"status\":\"{order.Status}\",\"identifiers\":[{{\"type\":\"dns\",\"value\":\"{order.Domain}\"}}],\"authorizations\":[\"{baseUrl}/authz/{order.Id}\"],\"finalize\":\"{baseUrl}/finalize/{order.Id}\"{certificate}}}"

    member private this.Handle(ctx: HttpContext) : Task =
        task {
            let fresh = Convert.ToHexString(RandomNumberGenerator.GetBytes 12)
            nonces[fresh] <- true
            ctx.Response.Headers["Replay-Nonce"] <- fresh
            let path = ctx.Request.Path.Value |> nonNull
            if path = "/dir" then
                ctx.Response.ContentType <- "application/json"
                do! ctx.Response.WriteAsync($"{{\"newNonce\":\"{baseUrl}/nonce\",\"newAccount\":\"{baseUrl}/acct\",\"newOrder\":\"{baseUrl}/order\"}}")
            elif path = "/nonce" then
                ctx.Response.StatusCode <- 200
            else
                use reader = new IO.StreamReader(ctx.Request.Body)
                let! body = reader.ReadToEndAsync()
                match this.Verify(ctx, body) with
                | Error(kind, detail) -> do! this.Problem(ctx, 400, kind, detail)
                | Ok(_, payload, key, kid) ->
                    ctx.Response.ContentType <- "application/json"
                    let id (prefix: string) = Int32.Parse(path.Substring prefix.Length)
                    if path = "/acct" then
                        let parameters = key.ExportParameters false
                        let eab = JsonDocument.Parse(payload).RootElement.TryGetProperty "externalAccountBinding"
                        let eabOk =
                            match requireEab, eab with
                            | None, _ -> true
                            | Some _, (false, _) -> false
                            | Some(keyId, hmacKey), (true, binding) ->
                                let protectedPart = binding.GetProperty("protected").GetString() |> nonNull
                                let payloadPart = binding.GetProperty("payload").GetString() |> nonNull
                                let header = JsonDocument.Parse(Encoding.UTF8.GetString(unb64 protectedPart)).RootElement
                                let expected = HMACSHA256.HashData(hmacKey, Encoding.ASCII.GetBytes(protectedPart + "." + payloadPart))
                                header.GetProperty("kid").GetString() = keyId
                                && header.GetProperty("alg").GetString() = "HS256"
                                && header.GetProperty("url").GetString() = baseUrl + "/acct"
                                && expected = unb64 (binding.GetProperty("signature").GetString() |> nonNull)
                                && JsonDocument.Parse(Encoding.UTF8.GetString(unb64 payloadPart)).RootElement.GetProperty("x").GetString() = b64 (nonNull parameters.Q.X)
                        if not eabOk then
                            do! this.Problem(ctx, 400, "externalAccountRequired", "bad or missing external account binding")
                        else
                            let account = $"{baseUrl}/acct/{b64 (nonNull parameters.Q.X)}"
                            if accounts.TryAdd(account, key) then Interlocked.Increment(&registrations) |> ignore
                            ctx.Response.StatusCode <- 201
                            ctx.Response.Headers["Location"] <- account
                            do! ctx.Response.WriteAsync "{\"status\":\"valid\"}"
                    elif kid = "" then
                        do! this.Problem(ctx, 400, "malformed", "kid required")
                    elif path = "/order" then
                        Interlocked.Increment(&newOrders) |> ignore
                        let identifiers = JsonDocument.Parse(payload).RootElement.GetProperty("identifiers")
                        let domain = identifiers[0].GetProperty("value").GetString() |> nonNull
                        let order =
                            { Id = Interlocked.Increment(&nextOrder)
                              Domain = domain
                              Account = kid
                              Status = "pending"
                              AuthzStatus = "pending"
                              Certificate = null }
                        orders[order.Id] <- order
                        ctx.Response.StatusCode <- 201
                        ctx.Response.Headers["Location"] <- $"{baseUrl}/order/{order.Id}"
                        do! ctx.Response.WriteAsync(this.OrderJson order)
                    elif path.StartsWith "/authz/" then
                        let order = orders[id "/authz/"]
                        let n = order.Id
                        do!
                            ctx.Response.WriteAsync(
                                $"{{\"status\":\"{order.AuthzStatus}\",\"identifier\":{{\"type\":\"dns\",\"value\":\"{order.Domain}\"}},\"challenges\":[{{\"type\":\"tls-alpn-01\",\"url\":\"{baseUrl}/chall/{n}/alpn\",\"token\":\"tokalpn{n}\",\"status\":\"pending\"}},{{\"type\":\"http-01\",\"url\":\"{baseUrl}/chall/{n}/http\",\"token\":\"tokhttp{n}\",\"status\":\"pending\"}}]}}"
                            )
                    elif path.StartsWith "/chall/" then
                        let parts = path.Substring("/chall/".Length).Split '/'
                        let order = orders[Int32.Parse parts[0]]
                        this.Validate(order, parts[1]) |> ignore
                        do! ctx.Response.WriteAsync "{\"status\":\"processing\"}"
                    elif path.StartsWith "/order/" then
                        do! ctx.Response.WriteAsync(this.OrderJson orders[id "/order/"])
                    elif path.StartsWith "/finalize/" then
                        let order = orders[id "/finalize/"]
                        let csr = unb64 (JsonDocument.Parse(payload).RootElement.GetProperty("csr").GetString() |> nonNull)
                        let request = CertificateRequest.LoadSigningRequest(csr, HashAlgorithmName.SHA256)
                        if order.Status <> "ready" || request.SubjectName.Name <> "CN=" + order.Domain then
                            do! this.Problem(ctx, 403, "orderNotReady", "not ready, or the wrong name")
                        else
                            let leafRequest = CertificateRequest(request.SubjectName, request.PublicKey, HashAlgorithmName.SHA256)
                            let san = SubjectAlternativeNameBuilder()
                            san.AddDnsName order.Domain
                            leafRequest.CertificateExtensions.Add(san.Build false)
                            use leaf =
                                leafRequest.Create(
                                    caCertificate,
                                    DateTimeOffset.UtcNow.AddDays -1.0,
                                    DateTimeOffset.UtcNow.AddDays 90.0,
                                    RandomNumberGenerator.GetBytes 8
                                )
                            order.Certificate <- leaf.ExportCertificatePem() + "\n" + caCertificate.ExportCertificatePem() + "\n"
                            order.Status <- "valid"
                            do! ctx.Response.WriteAsync(this.OrderJson order)
                    elif path.StartsWith "/cert/" then
                        ctx.Response.ContentType <- "application/pem-certificate-chain"
                        let order = orders[id "/cert/"]
                        do! ctx.Response.WriteAsync(nonNull order.Certificate)
                    else
                        ctx.Response.StatusCode <- 404
        }

    member this.Start() : Task =
        task {
            let builder = WebApplication.CreateSlimBuilder()
            builder.Logging.ClearProviders() |> ignore
            builder.WebHost.ConfigureKestrel(fun (options: KestrelServerOptions) -> options.Listen(IPAddress.Loopback, 0)) |> ignore
            let app = builder.Build()
            app.Run(RequestDelegate(fun ctx -> this.Handle ctx))
            do! app.StartAsync()
            let addresses = (nonNull (app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>())).Addresses
            baseUrl <- Seq.head addresses
            host <- app
        }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            match host with
            | null -> ValueTask()
            | app -> ValueTask(app.StopAsync())
