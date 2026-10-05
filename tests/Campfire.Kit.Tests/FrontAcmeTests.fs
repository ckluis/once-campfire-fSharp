// Port of the #[cfg(test)] module in rust/crates/kit/src/front/acme.rs, and of the ACME test in
// rust/crates/kit/tests/front.rs (which needs a Pebble CA, and runs here against Pebble when
// PEBBLE_MINICA is set, and against a stub CA in this file always)
module Campfire.Kit.Tests.FrontAcmeTests

open System
open System.IO
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Text
open Xunit
open Campfire.Kit

/// A self-signed certificate for `names` and its key.
let selfSigned (names: string list) : string * ECDsa =
    let key = ECDsa.Create ECCurve.NamedCurves.nistP256
    let request = CertificateRequest("CN=" + List.head names, key, HashAlgorithmName.SHA256)
    let san = SubjectAlternativeNameBuilder()
    for name in names do
        san.AddDnsName name
    request.CertificateExtensions.Add(san.Build false)
    let now = DateTimeOffset.UtcNow
    use certificate = request.CreateSelfSigned(now.AddDays -1.0, now.AddDays 30.0)
    certificate.ExportCertificatePem(), key

let private options (storage: string) : AcmeOptions =
    { DirectoryUrl = "https://acme.invalid/directory"
      ExternalAccount = ValueNone
      StoragePath = storage
      Domains = [ "chat.example.com" ]
      ChallengeTypes = [ TlsAlpn01 ]
      DirectoryRoot = ValueNone }

let private tempDir () : string =
    let path = Path.Combine(Path.GetTempPath(), "campfire-acme-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory path |> ignore
    path

[<Fact>]
let ``server names are normalized and checked`` () =
    Assert.Equal(Ok "chat.example.com", AcmeFiles.serverNameToDomain "Chat.Example.COM.")
    Assert.True((AcmeFiles.serverNameToDomain null).IsError)
    Assert.True((AcmeFiles.serverNameToDomain "localhost").IsError)
    Assert.True((AcmeFiles.serverNameToDomain "a/b.com").IsError)
    Assert.Equal(Ok "xn--bcher-kva.example", AcmeFiles.serverNameToDomain "bücher.example")

[<Fact>]
let ``cache entries round trip in autocert format`` () =
    let chain, key = selfSigned [ "chat.example.com" ]
    let pem = AcmeFiles.cacheEntry key chain
    Assert.StartsWith("-----BEGIN EC PRIVATE KEY-----\n", pem)
    Assert.Contains("-----BEGIN CERTIFICATE-----\n", pem)
    match AcmeFiles.parseCached pem "chat.example.com" with
    | Ok certified -> Assert.True(certified.NotAfter > DateTimeOffset.UtcNow)
    | Error error -> failwith error
    Assert.True((AcmeFiles.parseCached pem "other.example.com").IsError)
    // A certificate whose key isn't the one in the file.
    let wildcard = AcmeFiles.cacheEntry key (fst (selfSigned [ "*.example.com" ]))
    Assert.True((AcmeFiles.parseCached wildcard "chat.example.com").IsError, "a key that doesn't match its certificate")
    // Wildcards cover one label.
    let wildChain, wildKey = selfSigned [ "*.example.com" ]
    let wild = AcmeFiles.cacheEntry wildKey wildChain
    Assert.True((AcmeFiles.parseCached wild "chat.example.com").IsOk)
    Assert.True((AcmeFiles.parseCached wild "a.b.example.com").IsError)

[<Fact>]
let ``reads sec1 keys as go and openssl write them`` () =
    // `openssl ecparam -name prime256v1 -genkey -noout`: the form autocert writes.
    let sec1 =
        "-----BEGIN EC PRIVATE KEY-----\nMHcCAQEEIG5OGupNC0FxH3AXSkAGwIJDK3cGjGtRRWYgHs4ovnTzoAoGCCqGSM49\nAwEHoUQDQgAEx4Ph0lvWp/mtK6ehtSouzqyPEbXzgtcp59tMiQeiHuF+lPGyklEW\nOt6HbebraKMCA2Nm3v6HWJwkCMUTklFv5Q==\n-----END EC PRIVATE KEY-----\n"
    use key = AcmeFiles.parsePrivateKey sec1
    // And writes them back byte for byte (Go can't read SEC1 keys without the curve named).
    Assert.Equal(sec1, AcmeFiles.privateKeyPem key)

[<Fact>]
let ``writes cache files privately`` () =
    let dir = Path.Combine(tempDir (), "thruster")
    AcmeFiles.writeCacheFile dir "chat.example.com" "data"B
    Assert.Equal<byte[]>("data"B, File.ReadAllBytes(Path.Combine(dir, "chat.example.com")))
    if not (OperatingSystem.IsWindows()) then
        Assert.Equal(UnixFileMode.UserRead ||| UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "chat.example.com")))
        Assert.Equal(UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute, File.GetUnixFileMode dir)

[<Fact>]
let ``names outside tls domain leave nothing behind`` () =
    task {
        use certs = new CertManager(options (tempDir ()))
        for n in 0..99 do
            let! result = certs.Certificate $"x{n}.example.com"
            Assert.True result.IsError
        Assert.Equal(0, certs.ObtainingCount)
        Assert.Equal(0, certs.LoadedCount)
    }

[<Fact>]
let ``cached certificates load and are forgotten by obtaining`` () =
    task {
        let dir = tempDir ()
        let chain, key = selfSigned [ "chat.example.com"; "old.example.com" ]
        let pem = AcmeFiles.cacheEntry key chain
        AcmeFiles.writeCacheFile dir "chat.example.com" (Encoding.UTF8.GetBytes pem)
        AcmeFiles.writeCacheFile dir "old.example.com" (Encoding.UTF8.GetBytes pem)
        use certs = new CertManager(options dir)
        let! first = certs.Certificate "chat.example.com"
        Assert.True first.IsOk
        // No longer in TLS_DOMAIN, but cached: served, as autocert does.
        let! old = certs.Certificate "old.example.com"
        Assert.True old.IsOk
        Assert.NotNull(certs.Loaded "old.example.com")
        Assert.Equal(0, certs.ObtainingCount)
    }

[<Fact>]
let ``external account keys are url safe base64`` () =
    let logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
    Assert.Equal(ValueSome("kid", "hello"B), AcmeFiles.externalAccount logger "kid" "aGVsbG8")
    Assert.Equal(ValueNone, AcmeFiles.externalAccount logger "" "aGVsbG8")
    Assert.Equal(ValueNone, AcmeFiles.externalAccount logger "kid" "not base64!")
    // Strict as Rust's decoder: padding, the standard alphabet and stray trailing bits are refused.
    Assert.Equal(ValueNone, Base64Url.decode "aGVsbG8=")
    Assert.Equal(ValueNone, Base64Url.decode "a+/=")
    Assert.Equal(ValueNone, Base64Url.decode "aGVsbG9")
    Assert.Equal(ValueSome("hello?>"B), Base64Url.decode "aGVsbG8_Pg")
