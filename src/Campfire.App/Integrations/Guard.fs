// Port of rust/crates/campfire/src/integrations/net/guard.rs
//
// `RestrictedHTTP::PrivateNetworkGuard` (reference/lib/restricted_http/private_network_guard.rb)
// and the parts of the surfguard gem it calls (`Surfguard.resolve_public_ips`,
// `Surfguard.blocked_address?`, gem revision 59e278c, default policy).
//
// A host goes in and a public address to pin comes out. Numeric hosts never reach DNS; names
// must be plain LDH labels. Every answer is classified and the blocked ones dropped; IPv4
// answers come before IPv6 ones, in resolver order within each family.
namespace Campfire.App.Integrations

open System
open System.Net
open System.Net.Sockets
open System.Threading.Tasks

type GuardError =
    /// `RestrictedHTTP::Violation`: the host only resolves to blocked addresses (or is malformed).
    | Violation of string
    /// `Surfguard::Unresolvable`: the lookup failed or came back empty.
    | Unresolvable

module GuardError =
    let message (error: GuardError) : string =
        match error with
        | Violation host -> $"Attempt to access private IP via {host}"
        | Unresolvable -> "Host could not be resolved"

module Guard =
    [<Literal>]
    let private MaxHostBytes = 255

    [<Literal>]
    let private MaxAddresses = 256

    let private isAsciiDigit (c: char) = c >= '0' && c <= '9'

    let private isAsciiAlnum (c: char) = isAsciiDigit c || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')

    let private isAsciiHexDigit (c: char) = isAsciiDigit c || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')

    /// `normalize_host` for a String host: ASCII, no NUL, not empty, at most 255 bytes, no zone.
    let private normalHost (host: string) : bool =
        host |> Seq.forall (fun c -> c < '\u0080')
        && not (host.Contains '\000')
        && host <> ""
        && host.Length <= MaxHostBytes
        && not (host.Contains '%')

    // --- Parsing address literals -----------------------------------------------------------------------

    /// Rust's `str::parse::<Ipv6Addr>`: hex groups, `::`, an optional trailing dotted quad.
    let private parseIpv6 (text: string) : IPAddress option =
        if text <> "" && text |> Seq.forall (fun c -> isAsciiHexDigit c || c = ':' || c = '.') then
            match IPAddress.TryParse text with
            | true, NonNull ip when ip.AddressFamily = AddressFamily.InterNetworkV6 -> Some ip
            | _ -> None
        else
            None

    /// `IPAddr.new(text)` for a single address: dotted-quad IPv4, or IPv6 with optional brackets.
    let private ipLiteral (text: string) : IPAddress option =
        if text.StartsWith('[') && text.EndsWith(']') then
            parseIpv6 (text.Substring(1, text.Length - 2))
        elif text.Contains ':' then
            parseIpv6 text
        else
            // IPAddr only takes four decimal octets without leading zeros ("01" is rejected)
            let octets = text.Split '.'
            let valid (o: string) =
                o <> "" && o |> Seq.forall isAsciiDigit && (o.Length = 1 || not (o.StartsWith '0')) && o.Length <= 3 && int o <= 255
            if octets.Length = 4 && octets |> Array.forall valid then
                Some(IPAddress(octets |> Array.map byte))
            else
                None

    /// `host_address?(IPAddr.new(text))`: a full-length address literal.
    let private fullWidthHostLiteral (text: string) : bool = (ipLiteral text).IsSome

    /// 1 to 4 dot-separated (empty parts ignored) decimal or 0x-hex numbers.
    let private legacyIpv4Shape (text: string) : bool =
        let parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries)
        parts.Length >= 1
        && parts.Length <= 4
        && parts
           |> Array.forall (fun part ->
               if part.StartsWith "0x" || part.StartsWith "0X" then
                   let hex = part.Substring 2
                   hex <> "" && hex |> Seq.forall isAsciiHexDigit
               else
                   part |> Seq.forall isAsciiDigit)

    let private numericHostCandidate (host: string) : bool = host.Contains ':' || legacyIpv4Shape host

    let private validHostSyntax (host: string) : bool =
        if host.Contains ':' || legacyIpv4Shape host || fullWidthHostLiteral host then
            true
        else
            let host = if host.EndsWith '.' then host.Substring(0, host.Length - 1) else host
            host.Split '.'
            |> Array.forall (fun label ->
                label <> ""
                && label.Length <= 63
                && isAsciiAlnum label[0]
                && isAsciiAlnum label[label.Length - 1]
                && label |> Seq.forall (fun c -> isAsciiAlnum c || c = '-'))

    let private malformedNumericHostCandidate (host: string) : bool =
        if host.Contains ':' then
            false
        else
            let trimmed = host.TrimStart([| '%'; '/' |])
            let core =
                match trimmed.IndexOfAny [| '%'; '/' |] with
                | -1 -> trimmed
                | at -> trimmed.Substring(0, at)
            let malformed = core <> host || (core.Split '.' |> Array.exists (fun part -> part = ""))
            malformed && legacyIpv4Shape core && not (fullWidthHostLiteral host)

    let private parseRadix (digits: string) (radix: int) : uint64 option =
        let mutable value = 0UL
        let mutable ok = true
        for c in digits do
            let digit =
                if isAsciiDigit c then int c - int '0'
                elif c >= 'a' && c <= 'f' then int c - int 'a' + 10
                else int c - int 'A' + 10
            if ok then
                let next = value * uint64 radix + uint64 digit
                if value > (UInt64.MaxValue - uint64 digit) / uint64 radix then ok <- false else value <- next
        if ok then Some value else None

    /// glibc `__inet_aton_exact`: 1-4 parts in decimal, octal (leading 0) or hex (0x); the last part
    /// fills the remaining bytes.
    let inetAton (text: string) : IPAddress option =
        let parts = text.Split '.'
        if parts.Length = 0 || parts.Length > 4 then
            None
        else
            let values =
                parts
                |> Array.map (fun part ->
                    let digits, radix =
                        if part.StartsWith "0x" || part.StartsWith "0X" then part.Substring 2, 16
                        elif part.Length > 1 && part.StartsWith '0' then part.Substring 1, 8
                        else part, 10
                    let validDigit (c: char) =
                        match radix with
                        | 16 -> isAsciiHexDigit c
                        | 8 -> c >= '0' && c <= '7'
                        | _ -> isAsciiDigit c
                    if part = "" || not (digits |> Seq.forall validDigit) then
                        None
                    else
                        let value = if digits = "" then Some 0UL else parseRadix digits radix
                        value |> Option.filter (fun v -> v <= uint64 UInt32.MaxValue))
            if values |> Array.exists Option.isNone then
                None
            else
                let values = values |> Array.map Option.get
                let last = values[values.Length - 1]
                let leading = values[0 .. values.Length - 2]
                if leading |> Array.exists (fun v -> v > 0xffUL) then
                    None
                else
                    let remainingBits = 32 - 8 * leading.Length
                    if remainingBits < 32 && last >= (1UL <<< remainingBits) then
                        None
                    else
                        let mutable address = last
                        leading |> Array.iteri (fun i v -> address <- address ||| (v <<< (24 - 8 * i)))
                        let a = uint32 address
                        Some(IPAddress [| byte (a >>> 24); byte (a >>> 16); byte (a >>> 8); byte a |])

    /// glibc's `getaddrinfo(..., AI_NUMERICHOST)`: `inet_aton` forms for IPv4, `inet_pton` for IPv6.
    let private getaddrinfoNumeric (host: string) : IPAddress option =
        if host.Contains ':' then parseIpv6 host else inetAton host

    type private Numeric =
        /// Surfguard raises `InvalidInput`: the caller gets no addresses.
        | Invalid
        | Literal of IPAddress list
        /// Not numeric: ask the resolver.
        | Name

    /// `numeric_literals`: `getaddrinfo(host, AI_NUMERICHOST)`, then an `IPAddr` literal (which accepts
    /// brackets), else a name for DNS unless it looks numeric.
    let private numericLiterals (host: string) : Numeric =
        if not (validHostSyntax host) || malformedNumericHostCandidate host then
            Invalid
        else
            match getaddrinfoNumeric host with
            | Some ip -> Literal [ ip ]
            | None ->
                match ipLiteral host with
                | Some ip -> Literal [ ip ]
                | None -> if numericHostCandidate host then Invalid else Name

    /// `normalize_answers`: at most 256 answers, deduplicated, and at least one.
    let private normalizeAnswers (raw: IPAddress list) : Result<IPAddress list, GuardError> =
        if raw.Length > MaxAddresses then
            Error Unresolvable
        else
            match List.distinct raw with
            | [] -> Error Unresolvable
            | answers -> Ok answers

    // --- Classification (Surfguard.blocked_address?, default policy) ---------------------------------------

    type private V4Range = uint32 * int

    type private V6Range = UInt128 * int

    let private v4 (a: int) (b: int) (c: int) (d: int) (prefix: int) : V4Range =
        ((uint32 a <<< 24) ||| (uint32 b <<< 16) ||| (uint32 c <<< 8) ||| uint32 d), prefix

    let private v6 (segments: int list) (prefix: int) : V6Range =
        let mutable value = UInt128.Zero
        for segment in segments do
            value <- (value <<< 16) ||| UInt128(0UL, uint64 segment)
        value, prefix

    /// `Surfguard::DISALLOWED_IPV4`
    let private disallowedIpv4List: V4Range list =
        [ v4 0 0 0 0 8
          v4 10 0 0 0 8
          v4 100 64 0 0 10
          v4 127 0 0 0 8
          v4 168 63 129 16 32
          v4 169 254 0 0 16
          v4 172 16 0 0 12
          v4 192 0 0 0 24
          v4 192 0 2 0 24
          v4 192 88 99 0 24
          v4 192 168 0 0 16
          v4 198 18 0 0 15
          v4 198 51 100 0 24
          v4 203 0 113 0 24
          v4 224 0 0 0 4
          v4 240 0 0 0 4 ]

    /// `Surfguard::DISALLOWED_IPV6`
    let private disallowedIpv6List: V6Range list =
        [ v6 [ 0; 0; 0; 0; 0; 0; 0; 0 ] 128
          v6 [ 0x100; 0; 0; 0; 0; 0; 0; 0 ] 64
          v6 [ 0x100; 0; 0; 1; 0; 0; 0; 0 ] 64
          v6 [ 0x2001; 0; 0; 0; 0; 0; 0; 0 ] 32
          v6 [ 0x2001; 2; 0; 0; 0; 0; 0; 0 ] 48
          v6 [ 0x2001; 0xdb8; 0; 0; 0; 0; 0; 0 ] 32
          v6 [ 0x2002; 0; 0; 0; 0; 0; 0; 0 ] 16
          v6 [ 0x3fff; 0; 0; 0; 0; 0; 0; 0 ] 20
          v6 [ 0x5f00; 0; 0; 0; 0; 0; 0; 0 ] 16
          v6 [ 0xfec0; 0; 0; 0; 0; 0; 0; 0 ] 10
          v6 [ 0xff00; 0; 0; 0; 0; 0; 0; 0 ] 8 ]

    /// `Surfguard::IANA_ALLOCATED_IPV6_UNICAST`
    let private ianaAllocatedIpv6Unicast: V6Range list =
        [ v6 [ 0x2001; 0; 0; 0; 0; 0; 0; 0 ] 23
          v6 [ 0x2001; 0x200; 0; 0; 0; 0; 0; 0 ] 23
          v6 [ 0x2001; 0x400; 0; 0; 0; 0; 0; 0 ] 23
          v6 [ 0x2001; 0x600; 0; 0; 0; 0; 0; 0 ] 23
          v6 [ 0x2001; 0x800; 0; 0; 0; 0; 0; 0 ] 22
          v6 [ 0x2001; 0xc00; 0; 0; 0; 0; 0; 0 ] 23
          v6 [ 0x2001; 0xe00; 0; 0; 0; 0; 0; 0 ] 23
          v6 [ 0x2001; 0x1200; 0; 0; 0; 0; 0; 0 ] 23
          v6 [ 0x2001; 0x1400; 0; 0; 0; 0; 0; 0 ] 22
          v6 [ 0x2001; 0x1800; 0; 0; 0; 0; 0; 0 ] 23
          v6 [ 0x2001; 0x1a00; 0; 0; 0; 0; 0; 0 ] 23
          v6 [ 0x2001; 0x1c00; 0; 0; 0; 0; 0; 0 ] 22
          v6 [ 0x2001; 0x2000; 0; 0; 0; 0; 0; 0 ] 19
          v6 [ 0x2001; 0x4000; 0; 0; 0; 0; 0; 0 ] 23
          v6 [ 0x2001; 0x4200; 0; 0; 0; 0; 0; 0 ] 23
          v6 [ 0x2001; 0x4400; 0; 0; 0; 0; 0; 0 ] 23
          v6 [ 0x2001; 0x4600; 0; 0; 0; 0; 0; 0 ] 23
          v6 [ 0x2001; 0x4800; 0; 0; 0; 0; 0; 0 ] 23
          v6 [ 0x2001; 0x4a00; 0; 0; 0; 0; 0; 0 ] 23
          v6 [ 0x2001; 0x4c00; 0; 0; 0; 0; 0; 0 ] 23
          v6 [ 0x2001; 0x5000; 0; 0; 0; 0; 0; 0 ] 20
          v6 [ 0x2001; 0x8000; 0; 0; 0; 0; 0; 0 ] 19
          v6 [ 0x2001; 0xa000; 0; 0; 0; 0; 0; 0 ] 20
          v6 [ 0x2001; 0xb000; 0; 0; 0; 0; 0; 0 ] 20
          v6 [ 0x2002; 0; 0; 0; 0; 0; 0; 0 ] 16
          v6 [ 0x2003; 0; 0; 0; 0; 0; 0; 0 ] 18
          v6 [ 0x2400; 0; 0; 0; 0; 0; 0; 0 ] 12
          v6 [ 0x2410; 0; 0; 0; 0; 0; 0; 0 ] 12
          v6 [ 0x2600; 0; 0; 0; 0; 0; 0; 0 ] 12
          v6 [ 0x2610; 0; 0; 0; 0; 0; 0; 0 ] 23
          v6 [ 0x2620; 0; 0; 0; 0; 0; 0; 0 ] 23
          v6 [ 0x2630; 0; 0; 0; 0; 0; 0; 0 ] 12
          v6 [ 0x2800; 0; 0; 0; 0; 0; 0; 0 ] 12
          v6 [ 0x2a00; 0; 0; 0; 0; 0; 0; 0 ] 12
          v6 [ 0x2a10; 0; 0; 0; 0; 0; 0; 0 ] 12
          v6 [ 0x2c00; 0; 0; 0; 0; 0; 0; 0 ] 12 ]

    /// `Surfguard::GLOBALLY_REACHABLE_IETF_ASSIGNMENTS`
    let private globallyReachableIetfAssignments: V6Range list =
        [ v6 [ 0x2001; 3; 0; 0; 0; 0; 0; 0 ] 32; v6 [ 0x2001; 4; 0x112; 0; 0; 0; 0; 0 ] 48 ]

    let private ietfProtocolAssignments = v6 [ 0x2001; 0; 0; 0; 0; 0; 0; 0 ] 23
    let private nat64WellKnown = v6 [ 0x64; 0xff9b; 0; 0; 0; 0; 0; 0 ] 96
    let private nat64LocalUse = v6 [ 0x64; 0xff9b; 1; 0; 0; 0; 0; 0 ] 48
    let private ipv4Mapped = v6 [ 0; 0; 0; 0; 0; 0xffff; 0; 0 ] 96
    let private ipv4Translatable = v6 [ 0; 0; 0; 0; 0xffff; 0; 0; 0 ] 96
    let private ipv4Compatible = v6 [ 0; 0; 0; 0; 0; 0; 0; 0 ] 96
    let private uniqueLocal = v6 [ 0xfc00; 0; 0; 0; 0; 0; 0; 0 ] 7
    let private linkLocalV6 = v6 [ 0xfe80; 0; 0; 0; 0; 0; 0; 0 ] 10

    let private inV4 (ip: uint32) ((network, prefix): V4Range) : bool = prefix = 0 || ((ip ^^^ network) >>> (32 - prefix)) = 0u

    let private inV6 (ip: UInt128) ((network, prefix): V6Range) : bool =
        prefix = 0 || ((ip ^^^ network) >>> (128 - prefix)) = UInt128.Zero

    /// `disallowed_ipv4?`: `private?`, `loopback?` and `link_local?` are all inside the list.
    let private disallowedIpv4 (ip: uint32) : bool = disallowedIpv4List |> List.exists (inV4 ip)

    let private disallowedIpv6 (ip: UInt128) : bool =
        if globallyReachableIetfAssignments |> List.exists (inV6 ip) then
            false
        elif inV6 ip uniqueLocal || ip = UInt128.One || inV6 ip linkLocalV6 || inV6 ip ietfProtocolAssignments then
            true
        elif disallowedIpv6List |> List.exists (inV6 ip) then
            true
        else
            not (ianaAllocatedIpv6Unicast |> List.exists (inV6 ip))

    let private ipv4Value (ip: IPAddress) : uint32 =
        let b = ip.GetAddressBytes()
        (uint32 b[0] <<< 24) ||| (uint32 b[1] <<< 16) ||| (uint32 b[2] <<< 8) ||| uint32 b[3]

    let private ipv6Value (ip: IPAddress) : UInt128 =
        let mutable value = UInt128.Zero
        for b in ip.GetAddressBytes() do
            value <- (value <<< 8) ||| UInt128(0UL, uint64 b)
        value

    /// `Surfguard.blocked_address?(ip)`
    let blockedAddress (ip: IPAddress) : bool =
        if ip.AddressFamily = AddressFamily.InterNetwork then
            disallowedIpv4 (ipv4Value ip)
        else
            let ip = ipv6Value ip
            if inV6 ip ipv4Mapped || inV6 ip ipv4Compatible || inV6 ip nat64LocalUse then
                true
            elif inV6 ip nat64WellKnown || inV6 ip ipv4Translatable then
                disallowedIpv4 (uint32 (ip &&& UInt128(0UL, uint64 UInt32.MaxValue)))
            else
                disallowedIpv6 ip

    // --- Resolution -----------------------------------------------------------------------------------------

    /// `Surfguard.resolve_public_ips(host)`. Malformed input comes back empty rather than raising.
    let resolvePublicIps (resolver: IResolver) (host: string) : Task<Result<IPAddress list, GuardError>> =
        task {
            if not (normalHost host) then
                return Ok []
            else
                let! answers =
                    task {
                        match numericLiterals host with
                        | Invalid -> return Ok None
                        | Literal addresses -> return Ok(Some addresses)
                        | Name ->
                            match! resolver.Lookup host with
                            | Ok addresses -> return Ok(Some addresses)
                            | Error _ -> return Error Unresolvable
                    }
                match answers with
                | Error e -> return Error e
                | Ok None -> return Ok []
                | Ok(Some addresses) ->
                    match normalizeAnswers addresses with
                    | Error e -> return Error e
                    | Ok addresses ->
                        let v4, v6 =
                            addresses
                            |> List.filter (fun ip -> not (blockedAddress ip))
                            |> List.partition (fun ip -> ip.AddressFamily = AddressFamily.InterNetwork)
                        return Ok(v4 @ v6)
        }

    /// `RestrictedHTTP::PrivateNetworkGuard.resolve(hostname)`: the first public address.
    let resolve (resolver: IResolver) (host: string) : Task<Result<IPAddress, GuardError>> =
        task {
            match! resolvePublicIps resolver host with
            | Error e -> return Error e
            | Ok(ip :: _) -> return Ok ip
            | Ok [] -> return Error(Violation host)
        }
