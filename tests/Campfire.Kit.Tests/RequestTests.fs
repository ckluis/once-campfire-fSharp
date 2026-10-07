// Port of the #[cfg(test)] module in rust/crates/kit/src/request.rs
module Campfire.Kit.Tests.RequestTests

open System
open System.Net
open Microsoft.AspNetCore.Http
open Xunit
open Campfire.Kit

let private request (headers: (string * string) list) (peer: string) (proxy: ProxyConfig) : Request =
    let map = HeaderDictionary()
    for (k, v) in headers do
        map.Append(k, v)
    Request.Create("GET", "GET", "/rooms/1", "x=1", null, null, map, IPAddress.Parse peer, ReadOnlyMemory.Empty, proxy)

let private remoteIp (r: Request) : string =
    match r.RemoteIp() with
    | Ok ip -> ip
    | Error e -> failwith $"{e}"

[<Fact>]
let ``remote ip skips trusted proxies`` () =
    let proxy = ProxyConfig.Default
    Assert.Equal("203.0.113.9", remoteIp (request [ "x-forwarded-for", "203.0.113.9, 10.0.0.2" ] "127.0.0.1" proxy))
    Assert.Equal("198.51.100.7", remoteIp (request [] "198.51.100.7" proxy))
    // As in Rails, a forwarded address is believed even from a public peer: `remote_ip` is
    // only as trustworthy as the proxy in front.
    Assert.Equal("203.0.113.9", remoteIp (request [ "x-forwarded-for", "203.0.113.9" ] "198.51.100.7" proxy))
    Assert.Equal("10.0.0.3", remoteIp (request [ "x-forwarded-for", "10.0.0.3" ] "127.0.0.1" proxy))
    Assert.Equal("2001:db8::1", remoteIp (request [ "forwarded", "for=\"[2001:db8::1]:4711\"" ] "::1" proxy))

[<Fact>]
let ``remote ip spoofing check`` () =
    let proxy = ProxyConfig.Default
    match (request [ "client-ip", "1.1.1.1"; "x-forwarded-for", "2.2.2.2" ] "127.0.0.1" proxy).RemoteIp() with
    | Error IpSpoofAttack -> ()
    | other -> failwith $"{other}"
    Assert.Equal("2.2.2.2", remoteIp (request [ "client-ip", "2.2.2.2"; "x-forwarded-for", "2.2.2.2" ] "127.0.0.1" proxy))

[<Fact>]
let ``host and protocol`` () =
    let proxy = ProxyConfig.Default
    let r = request [ "host", "chat.example.com:3000" ] "127.0.0.1" proxy
    Assert.Equal("chat.example.com", r.Host)
    Assert.Equal(3000, r.Port)
    Assert.Equal("http://chat.example.com:3000", r.BaseUrl)
    Assert.Equal("http://chat.example.com:3000/rooms/1?x=1", r.Url)

    let r =
        request
            [ "host", "internal:80"; "x-forwarded-host", "a.example, chat.example.com"; "x-forwarded-proto", "https" ]
            "127.0.0.1"
            proxy
    Assert.Equal("chat.example.com", r.Host)
    Assert.True r.IsSsl
    Assert.Equal("https://chat.example.com", r.BaseUrl)

    let assume = { ProxyConfig.Default with AssumeSsl = true }
    let r = request [ "host", "chat.example.com" ] "127.0.0.1" assume
    Assert.Equal("https://chat.example.com", r.BaseUrl)

[<Fact>]
let ``ip nets`` () =
    let net = IpNet.parse "172.16.0.0/12"
    Assert.True(IpNet.contains net (IPAddress.Parse "172.31.255.1"))
    Assert.False(IpNet.contains net (IPAddress.Parse "172.32.0.1"))
    let net = IpNet.parse "fc00::/7"
    Assert.True(IpNet.contains net (IPAddress.Parse "fd12::1"))

[<Fact>]
let ``ip addresses are read as strictly as rust reads them`` () =
    for text in [ "127.1"; "0x7f.0.0.1"; "01.2.3.4"; "1.2.3"; "256.1.1.1"; "fe80::1%eth0"; ""; "1.2.3.4.5" ] do
        Assert.True((IpNet.tryParseIp text).IsNone, text)
    for text in [ "0.0.0.0"; "255.255.255.255"; "::1"; "2001:db8::1"; "::ffff:1.2.3.4" ] do
        Assert.True((IpNet.tryParseIp text).IsSome, text)
    Assert.True((IpNet.tryParse "10.0.0.0/33").IsError)
    Assert.True((IpNet.tryParse "10.0.0.0/x").IsError)
    Assert.True((IpNet.tryParse "10.0.0.1").IsOk)
