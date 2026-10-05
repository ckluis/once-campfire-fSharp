// Port of rust/crates/kit/src/front/acme.rs
//
// Automatic certificates, as Thruster gets them from `golang.org/x/crypto/acme/autocert`
// (`internal/server.go` `certManager`): on the first TLS handshake for a TLS_DOMAIN name, an ECDSA
// P-256 certificate is ordered from ACME_DIRECTORY (Let's Encrypt), answering TLS-ALPN-01 on the
// HTTPS port, or HTTP-01 on the HTTP port when that fails; it's renewed 30 days before it expires.
//
// Certificates and the account key live in STORAGE_PATH (`./storage/thruster`) in autocert's
// `DirCache` layout, so an install that ran Thruster keeps its certificates and account: `<domain>`
// holds the PEM private key followed by the PEM certificate chain, and `acme_account+key` the
// account's PEM private key.
//
// Where Rust uses instant-acme, this file has `AcmeClient`, a small RFC 8555 client of its own: the
// .NET ACME packages are either unmaintained (Certes and the forks of it) or tied to their own
// hosting integration and certificate store (LettuceEncrypt), and the part of ACME this needs
// (account, order, two challenge types, finalize, download) is a few hundred lines on `HttpClient`,
// `System.Text.Json` and `System.Security.Cryptography`.
namespace Campfire.Kit

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Net
open System.Net.Http
open System.Net.Security
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions

type AcmeChallengeType =
    | TlsAlpn01
    | Http01

    /// The name in an ACME challenge.
    member this.Name =
        match this with
        | TlsAlpn01 -> "tls-alpn-01"
        | Http01 -> "http-01"

type AcmeOptions =
    {
        DirectoryUrl: string
        /// `EAB_KID` and the decoded `EAB_HMAC_KEY`.
        ExternalAccount: (string * byte[]) voption
        StoragePath: string
        Domains: string list
        /// Challenge types in order of preference (autocert: TLS-ALPN-01, then HTTP-01).
        ChallengeTypes: AcmeChallengeType list
        /// A root certificate (PEM file) to trust for the ACME directory, for test CAs.
        DirectoryRoot: string voption
    }

module Base64Url =
    let encode (bytes: ReadOnlySpan<byte>) : string =
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    /// Unpadded URL-safe base64, as strictly as Rust's `URL_SAFE_NO_PAD.decode`: no padding, no
    /// other alphabet, no stray bits.
    let decode (text: string) : byte[] voption =
        let alphabet (c: char) = Char.IsAsciiLetterOrDigit c || c = '-' || c = '_'
        if not (Seq.forall alphabet text) || text.Length % 4 = 1 then
            ValueNone
        else
            let standard = text.Replace('-', '+').Replace('_', '/')
            let padded = standard + String('=', (4 - standard.Length % 4) % 4)
            try
                let bytes = Convert.FromBase64String padded
                // Trailing bits must be zero: decoding then encoding gives the text back.
                if encode (ReadOnlySpan<byte> bytes) = text then ValueSome bytes else ValueNone
            with :? FormatException ->
                ValueNone

module internal HttpHeaderValues =
    /// The first value of a response header, or null.
    let first (headers: Headers.HttpHeaders) (name: string) : string | null =
        match headers.TryGetValues name with
        | true, values ->
            match values with
            | null -> null
            | values -> Seq.tryHead values |> Option.toObj
        | _ -> null

/// What an ACME server answered with an error (RFC 8555 section 6.7).
type AcmeException(kind: string, message: string) =
    inherit Exception(message)
    member _.Kind = kind

type internal AcmeChallenge = { Type: string; Url: string; Token: string }

type internal AcmeAuthorization =
    { Status: string
      Identifier: string
      Challenges: AcmeChallenge list }

type internal AcmeOrder =
    { Url: string
      Status: string
      Authorizations: string list
      Finalize: string
      Certificate: string | null }

/// An ACME account over `HttpClient` (RFC 8555): JWS-signed POSTs with ES256 and a replay nonce
/// from each response.
[<Sealed>]
type internal AcmeClient(http: HttpClient, directoryUrl: string, key: ECDsa) =
    let jwkParameters = key.ExportParameters false
    let jwk =
        $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{Base64Url.encode (ReadOnlySpan<byte> (nonNull jwkParameters.Q.X))}\",\"y\":\"{Base64Url.encode (ReadOnlySpan<byte> (nonNull jwkParameters.Q.Y))}\"}}"
    let thumbprint = Base64Url.encode (ReadOnlySpan<byte>(SHA256.HashData(Encoding.UTF8.GetBytes jwk)))
    let mutable urls: struct (string * string * string) voption = ValueNone // newNonce, newAccount, newOrder
    let mutable nonce: string | null = null
    let mutable kid: string | null = null

    let problem (status: HttpStatusCode) (body: string) : exn =
        try
            use doc = JsonDocument.Parse body
            let root = doc.RootElement
            let kind = match root.TryGetProperty "type" with | true, t -> t.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
            let detail = match root.TryGetProperty "detail" with | true, d -> d.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
            AcmeException(kind, $"{int status}: {kind} {detail}".TrimEnd()) :> exn
        with :? JsonException ->
            AcmeException("", $"{int status}: {body}") :> exn

    member this.Directory() : Task<struct (string * string * string)> =
        task {
            match urls with
            | ValueSome known -> return known
            | ValueNone ->
                use! response = http.GetAsync directoryUrl
                let! body = response.Content.ReadAsStringAsync()
                if not response.IsSuccessStatusCode then raise (problem response.StatusCode body)
                use doc = JsonDocument.Parse body
                let get (name: string) = doc.RootElement.GetProperty(name).GetString() |> nonNull
                let known = struct (get "newNonce", get "newAccount", get "newOrder")
                urls <- ValueSome known
                return known
        }

    member private this.NextNonce() : Task<string> =
        task {
            match nonce with
            | null ->
                let! struct (newNonce, _, _) = this.Directory()
                use request = new HttpRequestMessage(HttpMethod.Head, newNonce)
                use! response = http.SendAsync request
                return
                    match HttpHeaderValues.first response.Headers "Replay-Nonce" with
                    | null -> raise (AcmeException("", "the server sent no Replay-Nonce"))
                    | fresh -> fresh
            | taken ->
                nonce <- null
                return taken
        }

    /// A JWS of `payload` (null for POST-as-GET) for `url`, signed with the account key and naming
    /// the account by `jwk` or, once registered, by its URL.
    member private this.Sign(url: string, payload: string | null, withJwk: bool, nonce: string) : string =
        let protectedHeader =
            if withJwk then
                $"{{\"alg\":\"ES256\",\"nonce\":\"{nonce}\",\"url\":\"{url}\",\"jwk\":{jwk}}}"
            else
                $"{{\"alg\":\"ES256\",\"kid\":\"{kid}\",\"nonce\":\"{nonce}\",\"url\":\"{url}\"}}"
        let protectedPart = Base64Url.encode (ReadOnlySpan<byte>(Encoding.UTF8.GetBytes protectedHeader))
        let payloadPart =
            match payload with
            | null -> ""
            | payload -> Base64Url.encode (ReadOnlySpan<byte>(Encoding.UTF8.GetBytes payload))
        let signature = key.SignData(Encoding.ASCII.GetBytes(protectedPart + "." + payloadPart), HashAlgorithmName.SHA256)
        $"{{\"protected\":\"{protectedPart}\",\"payload\":\"{payloadPart}\",\"signature\":\"{Base64Url.encode (ReadOnlySpan<byte> signature)}\"}}"

    /// POSTs a signed request, once more with a fresh nonce if the server found ours stale.
    member this.Post(url: string, payload: string | null, withJwk: bool, accept: string | null) : Task<struct (HttpResponseMessage * string)> =
        task {
            let mutable result = ValueNone
            let mutable attempts = 0
            while result.IsNone do
                let! current = this.NextNonce()
                use request = new HttpRequestMessage(HttpMethod.Post, url)
                let content = new StringContent(this.Sign(url, payload, withJwk, current), Encoding.UTF8)
                content.Headers.ContentType <- Headers.MediaTypeHeaderValue "application/jose+json"
                request.Content <- content
                match accept with
                | null -> ()
                | accept -> request.Headers.Accept.ParseAdd accept
                let! response = http.SendAsync request
                let! body = response.Content.ReadAsStringAsync()
                match HttpHeaderValues.first response.Headers "Replay-Nonce" with
                | null -> ()
                | fresh -> nonce <- fresh
                if response.IsSuccessStatusCode then
                    result <- ValueSome(struct (response, body))
                else
                    let error = problem response.StatusCode body
                    attempts <- attempts + 1
                    match error with
                    | :? AcmeException as e when e.Kind = "urn:ietf:params:acme:error:badNonce" && attempts < 3 -> response.Dispose()
                    | _ ->
                        response.Dispose()
                        raise error
            return result.Value
        }

    /// Registers the account for this key (or finds it, if the CA knows the key), agreeing to the terms.
    member this.Register(externalAccount: (string * byte[]) voption) : Task =
        task {
            let! struct (_, newAccount, _) = this.Directory()
            let eab =
                match externalAccount with
                | ValueNone -> ""
                | ValueSome(keyId, hmacKey) ->
                    let protectedPart =
                        Base64Url.encode (ReadOnlySpan<byte>(Encoding.UTF8.GetBytes $"{{\"alg\":\"HS256\",\"kid\":\"{keyId}\",\"url\":\"{newAccount}\"}}"))
                    let payloadPart = Base64Url.encode (ReadOnlySpan<byte>(Encoding.UTF8.GetBytes jwk))
                    let mac = HMACSHA256.HashData(hmacKey, Encoding.ASCII.GetBytes(protectedPart + "." + payloadPart))
                    $",\"externalAccountBinding\":{{\"protected\":\"{protectedPart}\",\"payload\":\"{payloadPart}\",\"signature\":\"{Base64Url.encode (ReadOnlySpan<byte> mac)}\"}}"
            let! struct (response, _) = this.Post(newAccount, $"{{\"termsOfServiceAgreed\":true{eab}}}", true, null)
            use response = response
            kid <- response.Headers.Location |> Option.ofObj |> Option.map (fun l -> l.ToString()) |> Option.toObj
            if isNull kid then raise (AcmeException("", "the server sent no account URL"))
        }

    member private _.ParseOrder(url: string, body: string) : AcmeOrder =
        use doc = JsonDocument.Parse body
        let root = doc.RootElement
        let str (name: string) : string | null =
            match root.TryGetProperty name with
            | true, v -> v.GetString()
            | _ -> null
        { Url = url
          Status = str "status" |> Option.ofObj |> Option.defaultValue ""
          Authorizations =
            match root.TryGetProperty "authorizations" with
            | true, list -> [ for a in list.EnumerateArray() -> nonNull (a.GetString()) ]
            | _ -> []
          Finalize = str "finalize" |> Option.ofObj |> Option.defaultValue ""
          Certificate = str "certificate" }

    member this.NewOrder(domain: string) : Task<AcmeOrder> =
        task {
            let! struct (_, _, newOrder) = this.Directory()
            let! struct (response, body) = this.Post(newOrder, $"{{\"identifiers\":[{{\"type\":\"dns\",\"value\":\"{domain}\"}}]}}", false, null)
            use response = response
            let url = response.Headers.Location |> Option.ofObj |> Option.map (fun l -> l.ToString()) |> Option.defaultValue ""
            return this.ParseOrder(url, body)
        }

    member this.Authorization(url: string) : Task<AcmeAuthorization> =
        task {
            let! struct (response, body) = this.Post(url, null, false, null)
            use _ = response
            use doc = JsonDocument.Parse body
            let root = doc.RootElement
            return
                { Status = root.GetProperty("status").GetString() |> nonNull
                  Identifier = root.GetProperty("identifier").GetProperty("value").GetString() |> nonNull
                  Challenges =
                    [ for c in root.GetProperty("challenges").EnumerateArray() ->
                          { Type = c.GetProperty("type").GetString() |> nonNull
                            Url = c.GetProperty("url").GetString() |> nonNull
                            Token = (match c.TryGetProperty "token" with | true, t -> t.GetString() |> nonNull | _ -> "") } ] }
        }

    /// `token.thumbprint`, what a challenge is answered with (RFC 8555 section 8.1).
    member _.KeyAuthorization(token: string) : string = token + "." + thumbprint

    /// Tells the CA the challenge is ready to be checked.
    member this.ChallengeReady(challengeUrl: string) : Task =
        task {
            let! struct (response, _) = this.Post(challengeUrl, "{}", false, null)
            response.Dispose()
        }

    /// Polls the order until its status is one of `until` (invalid is an error), for at most `timeout`.
    member this.PollOrder(url: string, until: string list, timeout: TimeSpan) : Task<AcmeOrder> =
        task {
            let started = Diagnostics.Stopwatch.StartNew()
            let mutable delay = TimeSpan.FromMilliseconds 250.0
            let mutable result = ValueNone
            while result.IsNone do
                let! struct (response, body) = this.Post(url, null, false, null)
                use _ = response
                let order = this.ParseOrder(url, body)
                if List.contains order.Status until then
                    result <- ValueSome order
                elif order.Status = "invalid" then
                    raise (AcmeException("", "order is invalid"))
                elif started.Elapsed > timeout then
                    raise (AcmeException("", $"timed out waiting for the order (status {order.Status})"))
                else
                    do! Task.Delay delay
                    delay <- TimeSpan.FromMilliseconds(min 5000.0 (delay.TotalMilliseconds * 1.5))
            return result.Value
        }

    member this.Finalize(order: AcmeOrder, csr: byte[]) : Task<AcmeOrder> =
        task {
            let! struct (response, body) = this.Post(order.Finalize, $"{{\"csr\":\"{Base64Url.encode (ReadOnlySpan<byte> csr)}\"}}", false, null)
            use _ = response
            return this.ParseOrder(order.Url, body)
        }

    member this.DownloadCertificate(url: string) : Task<string> =
        task {
            let! struct (response, body) = this.Post(url, null, false, "application/pem-certificate-chain")
            use _ = response
            return body
        }

/// Everything about a certificate a handshake needs.
[<Sealed; AllowNullLiteral>]
type LoadedCertificate(context: SslStreamCertificateContext, leaf: X509Certificate2, notAfter: DateTimeOffset) =
    /// What `SslStream` is given: the leaf with its key, and the rest of the chain.
    member _.Context = context
    member _.Leaf = leaf
    member _.NotAfter = notAfter

module internal AcmeFiles =
    [<Literal>]
    let AccountKey = "acme_account+key"

    /// `idna.Lookup.ToASCII`: a lowercased ASCII (punycode) domain, or none for an IP address or
    /// something that isn't a host name.
    let toAscii (host: string) : string voption =
        if host = "" || host.StartsWith '[' then
            ValueNone
        else
            match IPAddress.TryParse host with
            | true, _ -> ValueNone
            | _ ->
                try
                    let ascii = IdnMapping().GetAscii(host).ToLowerInvariant()
                    if ascii = "" || ascii.IndexOfAny [| ' '; '/'; '\\'; '@'; '#'; '?'; ':' |] >= 0 then ValueNone else ValueSome ascii
                with :? ArgumentException ->
                    ValueNone

    /// autocert's server name checks and normalization.
    let serverNameToDomain (serverName: string | null) : Result<string, string> =
        let name = match serverName with null -> "" | n -> n
        if name = "" then
            Error "acme/autocert: missing server name"
        elif not (name.Trim('.').Contains '.') then
            Error "acme/autocert: server name component count invalid"
        elif name.IndexOfAny [| '/'; '\\' |] >= 0 then
            Error "acme/autocert: server name contains invalid character"
        else
            match toAscii (name.TrimEnd '.') with
            | ValueSome domain -> Ok domain
            | ValueNone -> Error "acme/autocert: invalid server name"

    /// `externalAccountBinding`: both set, and the key is unpadded URL-safe base64.
    let externalAccount (logger: ILogger) (kid: string) (hmacKey: string) : (string * byte[]) voption =
        if kid = "" || hmacKey = "" then
            ValueNone
        else
            match Base64Url.decode hmacKey with
            | ValueSome key -> ValueSome(kid, key)
            | ValueNone ->
                logger.LogError("Error decoding EAB_HMACKey")
                ValueNone

    let pemBlock (label: string) (der: ReadOnlySpan<byte>) : string =
        let encoded = Convert.ToBase64String der
        let pem = StringBuilder($"-----BEGIN {label}-----\n")
        for line in encoded |> Seq.chunkBySize 64 do
            pem.Append(String line).Append('\n') |> ignore
        pem.Append($"-----END {label}-----\n").ToString()

    /// A P-256 key as autocert writes it (`x509.MarshalECPrivateKey`): SEC1 with the curve named,
    /// which Go needs to read it back (`ParseECPrivateKey`).
    let privateKeyPem (key: ECDsa) : string =
        let parameters = key.ExportParameters true
        let der = ResizeArray<byte>([| 0x30uy; 0x77uy; 0x02uy; 0x01uy; 0x01uy; 0x04uy; 0x20uy |])
        der.AddRange(nonNull parameters.D)
        // [0] namedCurve prime256v1
        der.AddRange([| 0xa0uy; 0x0auy; 0x06uy; 0x08uy; 0x2auy; 0x86uy; 0x48uy; 0xceuy; 0x3duy; 0x03uy; 0x01uy; 0x07uy |])
        // [1] publicKey BIT STRING, uncompressed point
        der.AddRange([| 0xa1uy; 0x44uy; 0x03uy; 0x42uy; 0x00uy; 0x04uy |])
        der.AddRange(nonNull parameters.Q.X)
        der.AddRange(nonNull parameters.Q.Y)
        pemBlock "EC PRIVATE KEY" (ReadOnlySpan<byte>(der.ToArray()))

    /// The certificates of a PEM text, as DER.
    let certificatesOf (pem: string) : byte[] list =
        let found = ResizeArray<byte[]>()
        let mutable rest = pem.AsSpan()
        let mutable fields = Unchecked.defaultof<PemFields>
        while PemEncoding.TryFind(rest, &fields) do
            let struct (labelAt, labelLength) = fields.Label.GetOffsetAndLength rest.Length
            let struct (dataAt, dataLength) = fields.Base64Data.GetOffsetAndLength rest.Length
            let struct (locationAt, locationLength) = fields.Location.GetOffsetAndLength rest.Length
            if rest.Slice(labelAt, labelLength).SequenceEqual("CERTIFICATE") then
                found.Add(Convert.FromBase64String(rest.Slice(dataAt, dataLength).ToString()))
            rest <- rest.Slice(locationAt + locationLength)
        List.ofSeq found

    /// A cache file's contents: the private key (as autocert writes it, SEC1 "EC PRIVATE KEY"),
    /// then the chain.
    let cacheEntry (key: ECDsa) (chainPem: string) : string =
        let pem = StringBuilder(privateKeyPem key)
        for der in certificatesOf chainPem do
            pem.Append(pemBlock "CERTIFICATE" (ReadOnlySpan<byte> der)) |> ignore
        pem.ToString()

    /// `x509.Certificate.VerifyHostname` for DNS names, including wildcards.
    let covers (certificate: X509Certificate2) (domain: string) : bool =
        let mutable covered = false
        for ext in certificate.Extensions do
            match ext with
            | :? X509SubjectAlternativeNameExtension as san ->
                for pattern in san.EnumerateDnsNames() do
                    let pattern = pattern.ToLowerInvariant()
                    if pattern.StartsWith("*.", StringComparison.Ordinal) then
                        let suffix = pattern.Substring 2
                        match domain.IndexOf '.' with
                        | -1 -> ()
                        | dot -> if domain.Substring(dot + 1) = suffix then covered <- true
                    elif pattern = domain then
                        covered <- true
            | _ -> ()
        covered

    /// The first private key in a cache file (autocert's `parsePrivateKey` accepts SEC1 EC keys and
    /// PKCS#8).
    let parsePrivateKey (pem: string) : ECDsa =
        let key = ECDsa.Create()
        key.ImportFromPem pem
        key

    /// Parses and checks a cache file for `domain` (autocert's `validCert`): its leaf covers the
    /// domain, is current, and matches the key.
    let parseCached (pem: string) (domain: string) : Result<LoadedCertificate, string> =
        try
            use key = parsePrivateKey pem
            let chain = certificatesOf pem |> List.map (fun der -> X509CertificateLoader.LoadCertificate der)
            match chain with
            | [] -> Error "no certificate"
            | leaf :: intermediates ->
                let now = DateTimeOffset.UtcNow
                if now < DateTimeOffset leaf.NotBefore || now > DateTimeOffset leaf.NotAfter then
                    Error "certificate is expired or not yet valid"
                elif not (covers leaf domain) then
                    Error $"certificate is not valid for {domain}"
                else
                    let matches =
                        match leaf.GetECDsaPublicKey() with
                        | null -> false
                        | publicKey ->
                            use publicKey = publicKey
                            let a = key.ExportParameters false
                            let b = publicKey.ExportParameters false
                            a.Q.X = b.Q.X && a.Q.Y = b.Q.Y
                    if not matches then
                        Error "private key does not match public key"
                    else
                        // The key goes in as a PKCS#12 so that every platform's TLS stack can use it.
                        use withKey = leaf.CopyWithPrivateKey key
                        let pfx = withKey.Export(X509ContentType.Pfx)
                        let usable = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable)
                        let extra = X509Certificate2Collection()
                        for certificate in intermediates do
                            extra.Add certificate |> ignore
                        let context = SslStreamCertificateContext.Create(usable, extra, false)
                        Ok(LoadedCertificate(context, usable, DateTimeOffset leaf.NotAfter))
        with e ->
            Error $"certificate: {e.Message}"

    /// `DirCache.Put`: the directory 0700, the file 0600, written through a temporary file.
    let writeCacheFile (dir: string) (name: string) (data: byte[]) : unit =
        if OperatingSystem.IsWindows() then
            Directory.CreateDirectory dir |> ignore
        else
            Directory.CreateDirectory(dir, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute) |> ignore
        let temporary = Path.Combine(dir, $"{name}.tmp{Environment.ProcessId}")
        let options = FileStreamOptions(Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None)
        if not (OperatingSystem.IsWindows()) then options.UnixCreateMode <- UnixFileMode.UserRead ||| UnixFileMode.UserWrite
        (
            use file = new FileStream(temporary, options)
            file.Write(ReadOnlySpan<byte> data)
            file.Flush true
        )
        File.Move(temporary, Path.Combine(dir, name), true)

    /// A TLS-ALPN-01 challenge certificate (RFC 8737).
    let challengeCertificate (domain: string) (digest: byte[]) : LoadedCertificate =
        use key = ECDsa.Create ECCurve.NamedCurves.nistP256
        let request = CertificateRequest($"CN={domain}", key, HashAlgorithmName.SHA256)
        let names = SubjectAlternativeNameBuilder()
        names.AddDnsName domain
        request.CertificateExtensions.Add(names.Build false)
        // id-pe-acmeIdentifier: an OCTET STRING of the key authorization's SHA-256, critical.
        request.CertificateExtensions.Add(X509Extension(Oid "1.3.6.1.5.5.7.1.31", Array.append [| 0x04uy; 0x20uy |] digest, true))
        let now = DateTimeOffset.UtcNow
        use certificate = request.CreateSelfSigned(now.AddDays -1.0, now.AddDays 7.0)
        let pfx = certificate.Export(X509ContentType.Pfx)
        let usable = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable)
        LoadedCertificate(SslStreamCertificateContext.Create(usable, X509Certificate2Collection(), false), usable, now.AddDays 7.0)

    /// An HttpClient for the ACME directory, trusting `root` (a PEM file) alone when there is one.
    let httpClient (root: string voption) : HttpClient =
        let handler = new SocketsHttpHandler()
        match root with
        | ValueNone -> ()
        | ValueSome path ->
            let anchor = X509CertificateLoader.LoadCertificate(certificatesOf (File.ReadAllText path) |> List.head)
            handler.SslOptions.RemoteCertificateValidationCallback <-
                RemoteCertificateValidationCallback(fun _ certificate _ _ ->
                    match certificate with
                    | null -> false
                    | certificate ->
                        use chain = new X509Chain()
                        chain.ChainPolicy.TrustMode <- X509ChainTrustMode.CustomRootTrust
                        chain.ChainPolicy.CustomTrustStore.Add anchor |> ignore
                        chain.ChainPolicy.RevocationMode <- X509RevocationMode.NoCheck
                        use leaf = new X509Certificate2(certificate)
                        chain.Build leaf)
        let client = new HttpClient(handler, Timeout = TimeSpan.FromSeconds 90.0)
        // RFC 8555 section 6.1: every request names its client.
        client.DefaultRequestHeaders.UserAgent.ParseAdd "campfire-fsharp-front (+https://github.com/ckluis/once-campfire-fsharp)"
        client

module AcmeOptions =
    let fromConfig (logger: ILogger) (config: FrontConfig) : AcmeOptions =
        { DirectoryUrl = config.AcmeDirectoryUrl
          ExternalAccount = AcmeFiles.externalAccount logger config.EabKid config.EabHmacKey
          StoragePath = config.StoragePath
          Domains = config.TlsDomains
          ChallengeTypes = [ TlsAlpn01; Http01 ]
          DirectoryRoot = ValueNone }

/// autocert's `renewJitter`.
module private AcmeTimings =
    let renewBefore = TimeSpan.FromDays 30.0
    let renewJitter = TimeSpan.FromHours 1.0
    /// autocert's timeout for obtaining a certificate during a handshake.
    let issueTimeout = TimeSpan.FromMinutes 5.0

[<Sealed; AllowNullLiteral>]
type CertManager(options: AcmeOptions, logger: ILogger) =
    let allowed = HashSet<string>(options.Domains |> List.choose (fun d -> AcmeFiles.toAscii d |> ValueOption.toOption))
    let certificates = Dictionary<string, LoadedCertificate>()
    let challengeCertificates = Dictionary<string, LoadedCertificate>()
    let httpTokens = Dictionary<string, string>()
    let gate = obj ()
    let obtaining = Dictionary<string, Task<Result<LoadedCertificate, string>>>()
    let renewing = HashSet<string>()
    let account = new SemaphoreSlim(1, 1)
    let shutdown = new CancellationTokenSource()
    let mutable accountClient: AcmeClient | null = null
    let mutable http: HttpClient | null = null

    new(options: AcmeOptions) = new CertManager(options, NullLogger.Instance)

    /// `HostWhitelist(domains...)(ctx, host)`
    member _.HostAllowed(host: string) : bool = allowed.Contains host

    /// The loaded certificate for a normalized name, for the TLS resolver.
    member _.Loaded(name: string) : LoadedCertificate =
        lock gate (fun () ->
            match certificates.TryGetValue name with
            | true, certificate -> certificate
            | _ -> null)

    /// The certificate a TLS-ALPN-01 validation handshake for `serverName` gets.
    member _.ChallengeCertificate(serverName: string | null) : LoadedCertificate =
        match AcmeFiles.serverNameToDomain serverName with
        | Error _ -> null
        | Ok name ->
            lock gate (fun () ->
                match challengeCertificates.TryGetValue name with
                | true, certificate -> certificate
                | _ -> null)

    /// The HTTP-01 response for a `/.well-known/acme-challenge/` path.
    member _.HttpToken(path: string) : string | null =
        lock gate (fun () ->
            match httpTokens.TryGetValue path with
            | true, token -> (token: string | null)
            | _ -> null)

    /// How many certificates are being obtained, for tests.
    member _.ObtainingCount = lock gate (fun () -> obtaining.Count)

    /// How many certificates are loaded, for tests.
    member _.LoadedCount = lock gate (fun () -> certificates.Count)

    member private _.HasCacheFile(name: string) : bool = File.Exists(Path.Combine(options.StoragePath, name))

    /// `cacheGet`: a cached certificate that's current, covers `name` and matches its key.
    member private _.ReadCached(name: string) : Task<LoadedCertificate> =
        task {
            let path = Path.Combine(options.StoragePath, name)
            if not (File.Exists path) then
                return null
            else
                let! pem =
                    task {
                        try
                            let! text = File.ReadAllTextAsync path
                            return ValueSome text
                        with :? IOException | :? UnauthorizedAccessException ->
                            return ValueNone
                    }
                match pem with
                | ValueNone -> return null
                | ValueSome pem ->
                    match AcmeFiles.parseCached pem name with
                    | Ok certificate -> return certificate
                    | Error error ->
                        logger.LogInformation("TLS: ignoring cached certificate domain={Domain} error={Error}", name, error)
                        return null
        }

    /// The ACME account, registered with the cached account key (or a new one, cached first).
    member private _.Account() : Task<AcmeClient> =
        task {
            do! account.WaitAsync()
            try
                match accountClient with
                | null ->
                    let client =
                        match http with
                        | null ->
                            let created = AcmeFiles.httpClient options.DirectoryRoot
                            http <- created
                            created
                        | existing -> existing
                    let keyPath = Path.Combine(options.StoragePath, AcmeFiles.AccountKey)
                    let key = ECDsa.Create()
                    let existing = File.Exists keyPath
                    if existing then
                        key.ImportFromPem(File.ReadAllText keyPath)
                    else
                        key.GenerateKey ECCurve.NamedCurves.nistP256
                        AcmeFiles.writeCacheFile options.StoragePath AcmeFiles.AccountKey (Encoding.UTF8.GetBytes(AcmeFiles.privateKeyPem key))
                    let created = AcmeClient(client, options.DirectoryUrl, key)
                    do! created.Register options.ExternalAccount
                    accountClient <- created
                    return created
                | existing -> return existing
            finally
                account.Release() |> ignore
        }

    /// Orders a certificate with one challenge type (`order`).
    member private this.Order(client: AcmeClient, name: string, challengeType: AcmeChallengeType) : Task<LoadedCertificate> =
        task {
            let httpPaths = ResizeArray<string>()
            let alpnDomains = ResizeArray<string>()
            let! order = client.NewOrder name
            try
                for authorizationUrl in order.Authorizations do
                    let! authorization = client.Authorization authorizationUrl
                    match authorization.Status with
                    | "valid" -> ()
                    | "pending" ->
                        let challenge =
                            match authorization.Challenges |> List.tryFind (fun c -> c.Type = challengeType.Name) with
                            | Some challenge -> challenge
                            | None -> raise (AcmeException("", "challenge type not offered"))
                        let keyAuthorization = client.KeyAuthorization challenge.Token
                        match challengeType with
                        | TlsAlpn01 ->
                            let certificate = AcmeFiles.challengeCertificate authorization.Identifier (SHA256.HashData(Encoding.ASCII.GetBytes keyAuthorization))
                            lock gate (fun () -> challengeCertificates[authorization.Identifier] <- certificate)
                            alpnDomains.Add authorization.Identifier
                        | Http01 ->
                            let path = "/.well-known/acme-challenge/" + challenge.Token
                            lock gate (fun () -> httpTokens[path] <- keyAuthorization)
                            httpPaths.Add path
                        do! client.ChallengeReady challenge.Url
                    | status -> raise (AcmeException("", $"authorization is {status}"))
                let! ready = client.PollOrder(order.Url, [ "ready"; "valid" ], TimeSpan.FromSeconds 120.0)
                ignore ready
            finally
                lock gate (fun () ->
                    for path in httpPaths do
                        httpTokens.Remove path |> ignore
                    for domain in alpnDomains do
                        challengeCertificates.Remove domain |> ignore)
            use key = ECDsa.Create ECCurve.NamedCurves.nistP256
            let request = CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256)
            let names = SubjectAlternativeNameBuilder()
            names.AddDnsName name
            request.CertificateExtensions.Add(names.Build false)
            let csr = request.CreateSigningRequest()
            let! finalized = client.Finalize(order, csr)
            let! valid =
                match finalized.Certificate with
                | null -> client.PollOrder(order.Url, [ "valid" ], TimeSpan.FromSeconds 120.0)
                | _ -> Task.FromResult finalized
            let certificateUrl = match valid.Certificate with null -> raise (AcmeException("", "the order has no certificate")) | url -> url
            let! chain = client.DownloadCertificate certificateUrl
            let pem = AcmeFiles.cacheEntry key chain
            match AcmeFiles.parseCached pem name with
            | Error error -> return raise (AcmeException("", error))
            | Ok parsed ->
                AcmeFiles.writeCacheFile options.StoragePath name (Encoding.UTF8.GetBytes pem)
                return parsed
        }

    /// Orders a certificate, trying each challenge type in turn (`verifiedOrder`).
    member private this.Issue(name: string) : Task<LoadedCertificate> =
        task {
            let! client = this.Account()
            let mutable result: LoadedCertificate = null
            let mutable lastError: exn = AcmeException("", "no supported challenge type")
            for challengeType in options.ChallengeTypes do
                if isNull result then
                    try
                        let! issued = this.Order(client, name, challengeType)
                        logger.LogInformation("TLS: obtained certificate domain={Domain}", name)
                        result <- issued
                    with e ->
                        logger.LogInformation("TLS: order failed domain={Domain} challenge={Challenge} error={Error}", name, challengeType.Name, e.Message)
                        lastError <- e
            match result with
            | null -> return raise lastError
            | certificate -> return certificate
        }

    /// autocert's `domainRenewal`: renew `RenewBefore` (minus jitter) ahead of expiry; after a
    /// failure, try again in 30 to 60 minutes.
    member private this.RenewForever(name: string, first: DateTimeOffset) : Task =
        task {
            let mutable notAfter = first
            let mutable renewed = false
            try
                while not shutdown.IsCancellationRequested do
                    let jitter = TimeSpan.FromTicks(int64 (float AcmeTimings.renewJitter.Ticks * Random.Shared.NextDouble()))
                    let due = notAfter - DateTimeOffset.UtcNow - AcmeTimings.renewBefore - jitter
                    // A certificate that lives less than `renewBefore` (a CA's short-lived profile) would be
                    // renewed over and over; after a renewal the next one waits at least an hour.
                    let mutable wait = if renewed && due < AcmeTimings.renewJitter then AcmeTimings.renewJitter else due
                    // `Task.Delay` takes at most ~49 days.
                    while wait > TimeSpan.Zero do
                        let step = if wait > TimeSpan.FromDays 7.0 then TimeSpan.FromDays 7.0 else wait
                        do! Task.Delay(step, shutdown.Token)
                        wait <- wait - step
                    try
                        let! certificate = this.Issue name
                        lock gate (fun () -> certificates[name] <- certificate)
                        notAfter <- certificate.NotAfter
                        renewed <- true
                    with e when not (e :? OperationCanceledException) ->
                        logger.LogError("TLS: certificate renewal failed domain={Domain} error={Error}", name, e.Message)
                        let half = AcmeTimings.renewJitter / 2.0
                        do! Task.Delay(half + TimeSpan.FromTicks(int64 (float half.Ticks * Random.Shared.NextDouble())), shutdown.Token)
            with :? OperationCanceledException ->
                ()
        }

    member private this.Install(name: string, certificate: LoadedCertificate) : unit =
        let start =
            lock gate (fun () ->
                certificates[name] <- certificate
                renewing.Add name)
        if start then this.RenewForever(name, certificate.NotAfter) |> ignore

    member private this.ObtainNow(name: string) : Task<Result<LoadedCertificate, string>> =
        task {
            match this.Loaded name with
            | null ->
                match! this.ReadCached name with
                | null ->
                    if not (this.HostAllowed name) then
                        return Error $"acme/autocert: host \"{name}\" not configured in HostWhitelist"
                    else
                        try
                            let! certificate = this.Issue name
                            this.Install(name, certificate)
                            return Ok certificate
                        with e ->
                            return Error e.Message
                | cached ->
                    this.Install(name, cached)
                    return Ok cached
            | certificate -> return Ok certificate
        }

    /// Obtains the certificate for `name` once, however many handshakes ask for it meanwhile.
    member private this.Obtain(name: string) : Task<Result<LoadedCertificate, string>> =
        lock gate (fun () ->
            match obtaining.TryGetValue name with
            | true, running -> running
            | _ ->
                let work =
                    task {
                        try
                            return! this.ObtainNow name
                        finally
                            lock gate (fun () -> obtaining.Remove name |> ignore)
                    }
                obtaining[name] <- work
                work)

    /// The certificate for a TLS handshake's server name (`GetCertificate`), obtaining one if there's
    /// none in memory or in the cache.
    member this.Certificate(serverName: string | null) : Task<Result<LoadedCertificate, string>> =
        task {
            match AcmeFiles.serverNameToDomain serverName with
            | Error error -> return Error error
            | Ok name ->
                match this.Loaded name with
                | null ->
                    // A cached certificate is used even for a name no longer in TLS_DOMAIN, as autocert
                    // does. Any other name is turned away here, before it costs a task or a place in
                    // `obtaining`.
                    if not (this.HostAllowed name) && not (this.HasCacheFile name) then
                        return Error $"acme/autocert: host \"{name}\" not configured in HostWhitelist"
                    else
                        // Its own task, so a client that gives up doesn't abandon the order.
                        let work = this.Obtain name
                        let timeout = Task.Delay AcmeTimings.issueTimeout
                        let! first = Task.WhenAny(work, timeout)
                        if obj.ReferenceEquals(first, work) then
                            return! work
                        else
                            return Error "acme/autocert: timed out obtaining a certificate"
                | certificate -> return Ok certificate
        }

    interface IDisposable with
        member _.Dispose() =
            shutdown.Cancel()
            match http with
            | null -> ()
            | client -> client.Dispose()
