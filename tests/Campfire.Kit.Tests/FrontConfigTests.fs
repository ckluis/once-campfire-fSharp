// Port of the #[cfg(test)] module in rust/crates/kit/src/front/config.rs
module Campfire.Kit.Tests.FrontConfigTests

open System
open System.Net
open Xunit
open Campfire.Kit

let private config (vars: (string * string) list) : FrontConfig =
    let map = dict vars
    FrontConfig.fromLookup (fun name ->
        match map.TryGetValue name with
        | true, v -> v
        | _ -> null)

[<Fact>]
let ``defaults`` () =
    let c = config []
    Assert.Equal((80, 443, 3000), (c.HttpPort, c.HttpsPort, c.TargetPort))
    Assert.Equal(IPAddress.Loopback, c.TargetBind)
    Assert.Equal(64L * 1024L * 1024L, c.CacheSize)
    Assert.Equal(1024L * 1024L, c.MaxCacheItemSize)
    Assert.Equal(TimeSpan.FromSeconds 60.0, c.HttpIdleTimeout)
    Assert.Equal(TimeSpan.FromSeconds 30.0, c.HttpReadTimeout)
    Assert.Equal(TimeSpan.FromSeconds 30.0, c.HttpWriteTimeout)
    Assert.Equal("./storage/thruster", c.StoragePath)
    Assert.Equal(FrontConfig.LetsEncryptUrl, c.AcmeDirectoryUrl)
    Assert.Equal(32L, c.GzipCompressionJitter)
    Assert.True(c.GzipCompressionEnabled && not c.GzipCompressionDisableOnAuth && not c.H2cEnabled)
    Assert.True(not c.HasTls && c.ForwardHeaders && c.LogRequests)

[<Fact>]
let ``prefixed variables win`` () =
    let c = config [ "HTTP_PORT", "8080"; "THRUSTER_HTTP_PORT", "9090"; "HTTPS_PORT", "8443" ]
    Assert.Equal((9090, 8443), (c.HttpPort, c.HttpsPort))

[<Fact>]
let ``target bind can open the app listener`` () =
    Assert.Equal(IPAddress.Any, (config [ "TARGET_BIND", "0.0.0.0" ]).TargetBind)
    Assert.Equal("::", (config [ "TARGET_BIND", "::" ]).TargetBind.ToString())
    Assert.Equal(IPAddress.Loopback, (config [ "TARGET_BIND", "everywhere" ]).TargetBind)
    // Rust's `IpAddr` parser: four plain decimal octets.
    Assert.Equal(IPAddress.Loopback, (config [ "TARGET_BIND", "127.1" ]).TargetBind)
    Assert.Equal(IPAddress.Loopback, (config [ "TARGET_BIND", "0x7f.0.0.1" ]).TargetBind)

[<Fact>]
let ``unparseable values fall back to the default`` () =
    let c = config [ "HTTP_PORT", "eighty"; "HTTP_READ_TIMEOUT", "5s"; "LOG_REQUESTS", "yes"; "H2C_ENABLED", "1" ]
    Assert.Equal(80, c.HttpPort)
    Assert.Equal(TimeSpan.FromSeconds 30.0, c.HttpReadTimeout)
    Assert.True(c.LogRequests && c.H2cEnabled)

[<Fact>]
let ``huge timeouts are clamped instead of failing`` () =
    // Rust takes Duration::from_secs(s as u64); TimeSpan.FromSeconds throws past about 9.2e11.
    let c = config [ "HTTP_IDLE_TIMEOUT", "9999999999999"; "HTTP_READ_TIMEOUT", "9223372036854775807"; "HTTP_WRITE_TIMEOUT", "86400" ]
    Assert.Equal(TimeSpan.FromDays 49.0, c.HttpIdleTimeout)
    Assert.Equal(TimeSpan.FromDays 49.0, c.HttpReadTimeout)
    Assert.Equal(TimeSpan.FromDays 1.0, c.HttpWriteTimeout)
    // Negative values are still zero (no timeout).
    Assert.Equal(TimeSpan.Zero, (config [ "HTTP_IDLE_TIMEOUT", "-5" ]).HttpIdleTimeout)
    // The longest a CancellationTokenSource waits is more than the clamp.
    use cts = new Threading.CancellationTokenSource()
    cts.CancelAfter c.HttpIdleTimeout

[<Fact>]
let ``tls domains turn off forwarded headers`` () =
    let c = config [ "TLS_DOMAIN", " chat.example.com, ,other.example.com " ]
    Assert.Equal<string list>([ "chat.example.com"; "other.example.com" ], c.TlsDomains)
    Assert.True(c.HasTls && not c.ForwardHeaders)
    Assert.True((config [ "TLS_DOMAIN", "a.example.com"; "FORWARD_HEADERS", "true" ]).ForwardHeaders)
    // Only TLS_DOMAIN counts (Thruster 0.1.23 doesn't read SSL_DOMAIN).
    Assert.False((config [ "SSL_DOMAIN", "a.example.com" ]).HasTls)
    Assert.False((config [ "TLS_DOMAIN", " , " ]).HasTls)
