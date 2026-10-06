// Port of the tests of rust/crates/campfire/src/integrations/net/guard.rs, with `FakeResolver` of
// `integrations/test_support.rs`.
module Campfire.App.Tests.GuardTests

open System
open System.Collections.Generic
open System.Net
open System.Threading.Tasks
open Xunit
open Campfire.App.Integrations
open Campfire.App.Tests.IntegrationsSupport

let private blocked (ip: string) = Guard.blockedAddress (IPAddress.Parse ip)

[<Fact>]
let ``classifies addresses like surfguard`` () =
    for ip in
        [ "0.0.0.0"; "10.1.2.3"; "100.64.0.1"; "127.0.0.1"; "168.63.129.16"; "169.254.169.254"; "172.16.0.0"; "172.31.255.255"
          "192.0.0.8"; "192.0.2.1"; "192.88.99.1"; "192.168.1.1"; "198.18.0.1"; "198.51.100.1"; "203.0.113.1"; "224.0.0.1"; "240.0.0.1"
          "255.255.255.255"; "::"; "::1"; "::ffff:192.168.1.1"; "::ffff:8.8.8.8"; "::8.8.8.8"; "64:ff9b::a00:1"; "64:ff9b:1::1"
          "::ffff:0:a00:1"; "fc00::1"; "fd00::1"; "fe80::1"; "fec0::1"; "ff02::1"; "2001::1"; "2001:db8::1"; "2002::1"; "3fff::1"
          "5f00::1"; "100::1"; "2001:2::1"; "4000::1"; "2001:10::1" ] do
        Assert.True(blocked ip, $"{ip} should be blocked")
    for ip in
        [ "8.8.8.8"; "1.1.1.1"; "93.184.216.34"; "142.250.185.206"; "172.32.0.1"; "100.128.0.1"; "192.0.1.1"
          "2606:2800:220:1:248:1893:25c8:1946"; "2a00:1450:4001:82a::200e"; "2001:3::1"; "2001:4:112::1"; "64:ff9b::808:808"
          "::ffff:0:808:808"; "2c0f:ffff::1" ] do
        Assert.False(blocked ip, $"{ip} should be public")

[<Fact>]
let ``inet aton forms`` () =
    let aton (text: string) = Guard.inetAton text |> Option.map string
    Assert.Equal(Some "127.0.0.1", aton "127.1")
    Assert.Equal(Some "127.0.0.1", aton "0x7f.1")
    Assert.Equal(Some "127.0.0.1", aton "2130706433")
    Assert.Equal(Some "127.0.0.1", aton "0177.0.0.01")
    Assert.Equal(Some "10.0.1.2", aton "10.0.258")
    Assert.Equal(None, aton "09.1.1.1")
    Assert.Equal(None, aton "256.1.1.1")
    Assert.Equal(None, aton "1.2.3.4.")
    Assert.Equal(None, aton "www.example.com")

[<Fact>]
let ``resolves hosts like the private network guard`` () =
    task {
        let resolver =
            FakeResolver(
                [ "www.example.com", [ "93.184.216.34" ]
                  "mixed.example", [ "10.0.0.1"; "2606:2800:220:1:248:1893:25c8:1946"; "::1"; "93.184.216.39" ]
                  "private.example", [ "192.168.1.10" ] ]
            )
        let net = resolver :> IResolver
        let ip (s: string) = IPAddress.Parse s
        let! resolved = Guard.resolve net "www.example.com"
        Assert.Equal(Ok(ip "93.184.216.34"), resolved)
        let! mixed = Guard.resolvePublicIps net "mixed.example"
        Assert.Equal(Ok [ ip "93.184.216.39"; ip "2606:2800:220:1:248:1893:25c8:1946" ], mixed)
        let! privateHost = Guard.resolve net "private.example"
        Assert.Equal(Error(Violation "private.example"), privateHost)
        let! nowhere = Guard.resolve net "nowhere.example"
        Assert.Equal(Error Unresolvable, nowhere)
        let! literal = Guard.resolve net "8.8.8.8"
        Assert.Equal(Ok(ip "8.8.8.8"), literal)
        let! bracketed = Guard.resolve net "[2606:2800:220:1:248:1893:25c8:1946]"
        Assert.Equal(Ok(ip "2606:2800:220:1:248:1893:25c8:1946"), bracketed)
        for host in
            [ "127.0.0.1"; "0x7f.1"; "2130706433"; "[::1]"; "::1"; "[fd00::1]"; "under_score.example"; ""; "a..b"; "1.2.3.4."; "01.2.3.4."
              "host%eth0"; "exämple.com"; "-lead.example"; "[v1.x]" ] do
            let! result = Guard.resolve net host
            match result with
            | Error(Violation _) -> ()
            | other -> failwith $"{host}: {other}"
        Assert.Equal<string list>([ "www.example.com"; "mixed.example"; "private.example"; "nowhere.example" ], resolver.Lookups)
    }
