// Port of rust/crates/kit/src/request.rs
//
// The request as a Rails controller sees it: method after `_method` override, URL pieces with
// proxy-derived host and protocol, `remote_ip`, and the raw body.
namespace Campfire.Kit

open System
open System.Collections.Generic
open System.Net
open System.Net.Sockets
open Microsoft.AspNetCore.Http
open Campfire.Kit

/// An IP network (`IPAddr.new("10.0.0.0/8")`).
[<Struct>]
type IpNet =
    { Addr: byte[]
      Prefix: int }

module IpNet =
    /// An IP address as Rust's `IpAddr::from_str` reads it: four decimal octets without leading
    /// zeros, or IPv6 text without a zone. (.NET's own parser also takes `127.1` and `0x7f.1`.)
    let tryParseIp (text: string) : IPAddress voption =
        if text.Contains ':' then
            if text.Contains '%' || text.Contains '/' then
                ValueNone
            else
                match IPAddress.TryParse text with
                | true, ip ->
                    match ip with
                    | null -> ValueNone
                    | ip -> if ip.AddressFamily = AddressFamily.InterNetworkV6 then ValueSome ip else ValueNone
                | _ -> ValueNone
        else
            let octets = text.Split '.'
            if octets.Length <> 4 then
                ValueNone
            else
                let parsed = octets |> Array.map (fun o ->
                    if o.Length = 0 || o.Length > 3 || not (Seq.forall Char.IsAsciiDigit o) || (o.Length > 1 && o[0] = '0') then
                        -1
                    else
                        let n = Int32.Parse o
                        if n > 255 then -1 else n)
                if parsed |> Array.exists (fun n -> n < 0) then
                    ValueNone
                else
                    ValueSome(IPAddress(parsed |> Array.map byte))

    /// `"10.0.0.0/8"` or a single address.
    let tryParse (s: string) : Result<IpNet, string> =
        let addr, prefix =
            match s.IndexOf '/' with
            | -1 -> s, ValueNone
            | slash -> s.Substring(0, slash), ValueSome(s.Substring(slash + 1))
        match tryParseIp addr with
        | ValueNone -> Error $"invalid IP \"{s}\""
        | ValueSome ip ->
            let max = if ip.AddressFamily = AddressFamily.InterNetwork then 32 else 128
            let prefix =
                match prefix with
                | ValueSome p ->
                    match Int32.TryParse(p, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
                    | true, n when n <= max && n <= 255 -> ValueSome n
                    | _ -> ValueNone
                | ValueNone -> ValueSome max
            match prefix with
            | ValueSome prefix ->
                Ok
                    { Addr = ip.GetAddressBytes()
                      Prefix = prefix }
            | ValueNone -> Error $"invalid prefix \"{s}\""

    let parse (s: string) : IpNet =
        match tryParse s with
        | Ok net -> net
        | Error message -> invalidArg "s" message

    let private maskEq (a: byte[]) (b: byte[]) (prefix: int) : bool =
        let full = prefix / 8
        let mutable same = true
        let mutable i = 0
        while same && i < full do
            if a[i] <> b[i] then same <- false
            i <- i + 1
        if not same then
            false
        else
            let rest = prefix % 8
            rest = 0
            || (let mask = byte (0xff <<< (8 - rest)) in (a[full] &&& mask) = (b[full] &&& mask))

    let contains (net: IpNet) (ip: IPAddress) : bool =
        let bytes = ip.GetAddressBytes()
        bytes.Length = net.Addr.Length && maskEq net.Addr bytes net.Prefix

/// How to read proxy headers (`ActionDispatch::RemoteIp`, `ActionDispatch::AssumeSSL`).
type ProxyConfig =
    {
        /// `config.action_dispatch.trusted_proxies`; Rails' `TRUSTED_PROXIES` by default.
        TrustedProxies: IpNet[]
        /// `config.action_dispatch.ip_spoofing_check`.
        IpSpoofingCheck: bool
        /// `config.assume_ssl`: treat every request as HTTPS (TLS terminates at the proxy).
        AssumeSsl: bool
    }

module ProxyConfig =
    /// `ActionDispatch::RemoteIp::TRUSTED_PROXIES`.
    let defaultTrustedProxies: IpNet[] =
        [| "127.0.0.0/8"; "::1/128"; "fc00::/7"; "10.0.0.0/8"; "172.16.0.0/12"; "192.168.0.0/16"; "169.254.0.0/16"; "fe80::/10" |]
        |> Array.map IpNet.parse

    let Default: ProxyConfig =
        { TrustedProxies = defaultTrustedProxies
          IpSpoofingCheck = true
          AssumeSsl = false }

module RequestHeaders =
    /// The first value of `name`, null when there is none.
    let get (headers: IHeaderDictionary) (name: string) : string | null =
        match headers.TryGetValue name with
        | true, values when values.Count > 0 -> values[0]
        | _ -> null

    /// The `type/subtype` part of `Content-Type`, lowercased (`Rack::MediaType.type`).
    let mediaType (contentType: string | null) : string | null =
        match contentType with
        | null -> null
        | ct ->
            let cut = ct.IndexOfAny [| ';'; ',' |]
            let baseType = (if cut < 0 then ct else ct.Substring(0, cut)).Trim().ToLowerInvariant()
            if baseType = "" then null else baseType

    let private isSplitChar (c: char) = c = ',' || c = ' ' || c = '\t'

    /// `value.trim().split([',', ' ', '\t']).filter(non-empty)`
    let splitHeader (value: string) : string[] =
        value.Trim().Split([| ','; ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)

    /// `Rack::Utils.forwarded_values(header)[param]`, for the simple unquoted and quoted forms.
    let forwardedValues (header: string) (param: string) : string list =
        [ for element in header.Split [| ','; ';' |] do
              match element.IndexOf '=' with
              | -1 -> ()
              | eq ->
                  if String.Equals(element.Substring(0, eq).Trim(), param, StringComparison.OrdinalIgnoreCase) then
                      element.Substring(eq + 1).Trim().Trim('"') ]

    let private schemes = [| "https"; "http"; "wss"; "ws" |]

    /// `Rack::Request#scheme` → `ssl?`. `scheme` is the connection's, as the server reports it.
    let schemeIsHttps (headers: IHeaderDictionary) (scheme: string | null) : bool =
        if get headers Hdr.XForwardedSsl = "on" then
            true
        else
            let fromForwarded =
                match get headers Hdr.Forwarded with
                | null -> ValueNone
                | forwarded ->
                    match forwardedValues forwarded "proto" |> List.tryLast with
                    | Some proto when Array.contains proto schemes -> ValueSome(proto = "https" || proto = "wss")
                    | _ -> ValueNone
            match fromForwarded with
            | ValueSome ssl -> ssl
            | ValueNone ->
                let fromHeader (name: string) =
                    match get headers name with
                    | null -> ValueNone
                    | value ->
                        match splitHeader value |> Array.rev |> Array.tryFind (fun s -> Array.contains s schemes) with
                        | Some scheme -> ValueSome(scheme = "https" || scheme = "wss")
                        | None -> ValueNone
                match fromHeader Hdr.XForwardedProto with
                | ValueSome ssl -> ssl
                | ValueNone ->
                    match fromHeader Hdr.XForwardedScheme with
                    | ValueSome ssl -> ssl
                    | ValueNone -> scheme = "https"

    /// Strip a port and IPv6 brackets: `Rack::Request#split_authority(...)[1]`.
    let private authorityAddress (authority: string) : string =
        let authority = authority.Trim()
        if authority.StartsWith '[' then
            let rest = authority.Substring 1
            match rest.IndexOf ']' with
            | -1 -> rest
            | close -> rest.Substring(0, close)
        elif (IpNet.tryParseIp authority).IsSome then
            authority
        else
            match authority.LastIndexOf ':' with
            | -1 -> authority
            | colon ->
                let host = authority.Substring(0, colon)
                let port = authority.Substring(colon + 1)
                if Seq.forall Char.IsAsciiDigit port && not (host.Contains ':') then host else authority

    let private sanitizeIps (ips: string seq) : ResizeArray<IPAddress> =
        let parsed = ResizeArray<IPAddress>()
        for ip in ips do
            match IpNet.tryParseIp (ip.Trim([| '['; ']' |])) with
            | ValueSome ip -> parsed.Add ip
            | ValueNone -> ()
        parsed

    let private forwardedFor (headers: IHeaderDictionary) : string list =
        let fromForwarded =
            match get headers Hdr.Forwarded with
            | null -> []
            | forwarded -> forwardedValues forwarded "for"
        if not fromForwarded.IsEmpty then
            fromForwarded |> List.map authorityAddress
        else
            match get headers Hdr.XForwardedFor with
            | null -> []
            | value -> splitHeader value |> Array.map authorityAddress |> List.ofArray

    /// `ActionDispatch::RemoteIp::GetIp#calculate_ip`.
    let calculateRemoteIp (headers: IHeaderDictionary) (peer: IPAddress | null) (proxy: ProxyConfig) : Result<string, unit> =
        let clientIps =
            match get headers Hdr.ClientIp with
            | null -> ResizeArray()
            | header -> sanitizeIps (header.Trim().Split [| ','; ' '; '\t' |])
        clientIps.Reverse()
        let forwardedIps = sanitizeIps (forwardedFor headers)
        forwardedIps.Reverse()
        let spoofed =
            proxy.IpSpoofingCheck
            && clientIps.Count > 0
            && forwardedIps.Count > 0
            && not (forwardedIps.Contains clientIps[clientIps.Count - 1])
        if spoofed then
            Error()
        else
            let ips = ResizeArray<IPAddress>(forwardedIps)
            ips.AddRange clientIps
            let trusted (ip: IPAddress) = proxy.TrustedProxies |> Array.exists (fun net -> IpNet.contains net ip)
            let candidates = ResizeArray<IPAddress>(ips)
            if not (isNull peer) then candidates.Add(nonNull peer)
            let chosen =
                match candidates |> Seq.tryFind (fun ip -> not (trusted ip)) with
                | Some ip -> Some ip
                | None ->
                    if ips.Count > 0 then Some ips[ips.Count - 1]
                    elif isNull peer then None
                    else Some(nonNull peer)
            Ok(match chosen with Some ip -> ip.ToString() | None -> "")

/// The request as a Rails controller sees it. Built by the adapter; `Headers` is the host's own
/// header collection, so reading a header costs no copy.
[<Sealed>]
type Request
    (
        meth: string,
        originalMethod: string,
        path: string,
        query: string | null,
        authority: string | null,
        ssl: bool,
        headers: IHeaderDictionary,
        peer: IPAddress | null,
        body: ReadOnlyMemory<byte>,
        proxy: ProxyConfig
    ) =
    let mutable remoteIp: Result<string, unit> voption = ValueNone
    let mutable rawHost: string | null = null

    /// `ActionDispatch::RequestId`: the id the response's `X-Request-Id` carries, for logging.
    member val RequestId: string = "" with get, set

    /// The method the app sees (after `Rack::MethodOverride`).
    member _.Method = meth

    /// A request whose TLS-ness is worked out from the proxy headers and the connection's scheme
    /// (`Rack::Request#ssl?`), as `AssumeSsl` and the headers say.
    static member Create
        (
            meth: string,
            originalMethod: string,
            path: string,
            query: string | null,
            authority: string | null,
            scheme: string | null,
            headers: IHeaderDictionary,
            peer: IPAddress | null,
            body: ReadOnlyMemory<byte>,
            proxy: ProxyConfig
        ) : Request =
        let ssl = proxy.AssumeSsl || RequestHeaders.schemeIsHttps headers scheme
        Request(meth, originalMethod, path, query, authority, ssl, headers, peer, body, proxy)

    /// The method on the wire (`request.method` in Rails).
    member _.OriginalMethod = originalMethod

    member _.Headers = headers

    /// The TCP peer (`REMOTE_ADDR`).
    member _.Peer = peer

    member _.Header(name: string) : string | null = RequestHeaders.get headers name

    member _.IsGet = String.Equals(meth, "GET", StringComparison.Ordinal)
    member _.IsHead = String.Equals(meth, "HEAD", StringComparison.Ordinal)
    member _.IsPost = String.Equals(meth, "POST", StringComparison.Ordinal)

    /// `request.xhr?`
    member this.IsXhr =
        match this.Header Hdr.XRequestedWith with
        | null -> false
        | v -> v.Contains("xmlhttprequest", StringComparison.OrdinalIgnoreCase)

    member _.Path = path

    member _.QueryString: string =
        match query with
        | null -> ""
        | q -> q

    /// `request.fullpath`
    member this.Fullpath: string =
        match query with
        | null -> path
        | "" -> path
        | q -> path + "?" + q

    member _.IsSsl = ssl

    /// `request.protocol`: `"https://"` or `"http://"`.
    member _.Protocol = if ssl then "https://" else "http://"

    member private this.RawHostWithPort: string =
        match rawHost with
        | null ->
            let computed =
                match this.Header Hdr.XForwardedHost with
                | forwarded when not (String.IsNullOrWhiteSpace forwarded) ->
                    let forwarded = nonNull forwarded
                    let last = forwarded.Substring(forwarded.LastIndexOf ',' + 1)
                    last.TrimStart()
                | _ ->
                    match this.Header Hdr.Host with
                    | null ->
                        match authority with
                        | null -> "localhost"
                        | authority -> authority
                    | host -> host
            rawHost <- computed
            computed
        | raw -> raw

    member private _.StandardPort = if ssl then 443 else 80

    static member private SplitPort(raw: string) : struct (string * string | null) =
        match raw.LastIndexOf ':' with
        | -1 -> struct (raw, null)
        | colon ->
            let port = raw.Substring(colon + 1)
            if port.Length > 0 && Seq.forall Char.IsAsciiDigit port then
                struct (raw.Substring(0, colon), port)
            else
                struct (raw, null)

    /// `request.host` (X-Forwarded-Host aware, port stripped).
    member this.Host: string =
        let struct (host, _) = Request.SplitPort this.RawHostWithPort
        host

    member this.Port: int =
        let struct (_, port) = Request.SplitPort this.RawHostWithPort
        match port with
        | null -> this.StandardPort
        | port ->
            match UInt16.TryParse port with
            | true, n -> int n
            | _ -> this.StandardPort

    /// `request.host_with_port`: the port only when it isn't the scheme's default.
    member this.HostWithPort: string =
        let port = this.Port
        if port = this.StandardPort then this.Host else this.Host + ":" + string port

    /// `request.base_url`, e.g. `https://campfire.example.com`.
    member this.BaseUrl: string = this.Protocol + this.HostWithPort

    /// `request.url`
    member this.Url: string = this.BaseUrl + this.Fullpath

    member this.Origin: string | null = this.Header Hdr.Origin
    member this.Referer: string | null = this.Header Hdr.Referer
    member this.UserAgent: string | null = this.Header Hdr.UserAgent

    member this.ContentType: string | null =
        match this.Header Hdr.ContentType with
        | "" -> null
        | ct -> ct

    /// The `type/subtype` part of `Content-Type`, lowercased (`Rack::MediaType.type`).
    member this.MediaType: string | null = RequestHeaders.mediaType this.ContentType

    /// `request.raw_post` / `request.body.read`. Empty for multipart bodies, which are spooled
    /// into params instead.
    member _.RawPost: ReadOnlyMemory<byte> = body

    /// `request.remote_ip`, per `ActionDispatch::RemoteIp::GetIp`.
    member _.RemoteIp() : Result<string, Error> =
        let computed =
            match remoteIp with
            | ValueSome computed -> computed
            | ValueNone ->
                let computed = RequestHeaders.calculateRemoteIp headers peer proxy
                remoteIp <- ValueSome computed
                computed
        match computed with
        | Ok ip -> Ok ip
        | Error() -> Error IpSpoofAttack
