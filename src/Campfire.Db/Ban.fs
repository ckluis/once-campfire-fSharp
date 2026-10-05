// Port of rust/crates/db/src/models/ban.rs
// `reference/app/models/ban.rb`
namespace Campfire.Db

open System
open System.Buffers.Binary
open System.Net
open System.Net.Sockets

type Ban =
    { Id: int64
      UserId: int64
      IpAddress: string
      CreatedAt: Timestamp
      UpdatedAt: Timestamp }

/// An address as `IPAddr` holds it: an IPv4 as 32 bits, an IPv6 as 128.
type internal IpAddress =
    | V4 of uint32
    | V6 of UInt128

module Ban =
    [<Literal>]
    let private select = "SELECT " + Columns.Ban + " FROM \"bans\""

    [<Literal>]
    let private forUserSql = select + " WHERE \"bans\".\"user_id\" = ?"

    let private fromRow (r: Row) : Ban =
        { Id = r.Int64 0
          UserId = r.Int64 1
          IpAddress = r.Text 2
          CreatedAt = r.Timestamp 3
          UpdatedAt = r.Timestamp 4 }

    /// `Ban.banned?(ip_address)`
    let banned (conn: Conn) (ipAddress: string) : bool =
        conn.Exists("""SELECT 1 AS one FROM "bans" WHERE "bans"."ip_address" = ? LIMIT 1""", [| S ipAddress |])

    let forUser (conn: Conn) (userId: int64) : Ban list = conn.QueryAll(forUserSql, [| I userId |], fromRow)

    /// `a.b.c.d`, each part decimal without leading zeros, as Rust's `Ipv4Addr` and `IPAddr` read it.
    let private parseV4 (text: string) : uint32 option =
        let parts = text.Split '.'
        if parts.Length <> 4 then
            None
        else
            let part (p: string) =
                if p.Length = 0 || p.Length > 3 || not (p |> Seq.forall Char.IsAsciiDigit) || (p.Length > 1 && p[0] = '0') then
                    None
                else
                    let n = Int32.Parse p
                    if n > 255 then None else Some(uint32 n)
            match part parts[0], part parts[1], part parts[2], part parts[3] with
            | Some a, Some b, Some c, Some d -> Some((a <<< 24) ||| (b <<< 16) ||| (c <<< 8) ||| d)
            | _ -> None

    let private parseV6 (text: string) : UInt128 option =
        if not (text.Contains ':') || text.Contains '%' || text.Contains '/' || text.Contains '[' then
            None
        else
            match IPAddress.TryParse text with
            | true, ip ->
                match ip with
                | null -> None
                | ip when ip.AddressFamily = AddressFamily.InterNetworkV6 ->
                    Some(BinaryPrimitives.ReadUInt128BigEndian(ReadOnlySpan(ip.GetAddressBytes())))
                | _ -> None
            | _ -> None

    /// `IPAddr.new`: an address, optionally with a `/prefix` (masked), or an IPv6 in brackets.
    let private parseIpAddr (text: string) : IpAddress option =
        let address, prefix =
            match text.IndexOf '/' with
            | -1 -> Some text, Some None
            | i ->
                let digits = text.Substring(i + 1)
                let prefix = if digits.Length > 0 && digits |> Seq.forall Char.IsAsciiDigit && digits.Length < 10 then Some(Some(UInt32.Parse digits)) else None
                Some(text.Substring(0, i)), prefix
        match address, prefix with
        | Some address, Some prefix ->
            let address =
                if address.Length >= 2 && address[0] = '[' && address[address.Length - 1] = ']' then
                    address.Substring(1, address.Length - 2)
                else
                    address
            let ip =
                match parseV4 address with
                | Some v4 -> Some(V4 v4)
                | None -> parseV6 address |> Option.map V6
            match ip, prefix with
            | Some(V4 v4), Some bits when bits <= 32u ->
                let mask = if bits = 0u then 0u else UInt32.MaxValue <<< int (32u - bits)
                Some(V4(v4 &&& mask))
            | Some(V6 v6), Some bits when bits <= 128u ->
                let mask = if bits = 0u then UInt128.Zero else UInt128.MaxValue <<< int (128u - bits)
                Some(V6(v6 &&& mask))
            | Some _, Some _ -> None
            | ip, None -> ip
            | None, _ -> None
        | _ -> None

    // Ruby's IPAddr predicates, including their IPv4-mapped IPv6 handling, which only
    // checks the `ffff` bits (`@addr & 0xffff_0000_0000 == 0xffff_0000_0000`).

    let private mappedV4 (ip: IpAddress) : uint32 option =
        match ip with
        | V6 addr ->
            let mask = UInt128(0UL, 0xffff_0000_0000UL)
            if (addr &&& mask) = mask then Some(uint32 (addr &&& UInt128(0UL, 0xffff_ffffUL))) else None
        | V4 _ -> None

    let private isLoopback (ip: IpAddress) : bool =
        match ip with
        | V4 v4 -> (v4 &&& 0xff00_0000u) = 0x7f00_0000u
        | V6 v6 -> v6 = UInt128.One || (mappedV4 ip |> Option.exists (fun a -> (a &&& 0xff00_0000u) = 0x7f00_0000u))

    let private privateV4 (a: uint32) : bool =
        (a &&& 0xff00_0000u) = 0x0a00_0000u || (a &&& 0xfff0_0000u) = 0xac10_0000u || (a &&& 0xffff_0000u) = 0xc0a8_0000u

    let private isPrivate (ip: IpAddress) : bool =
        match ip with
        | V4 v4 -> privateV4 v4
        | V6 v6 -> (v6 >>> 121) = UInt128(0UL, 0xfcUL >>> 1) || (mappedV4 ip |> Option.exists privateV4)

    let private isLinkLocal (ip: IpAddress) : bool =
        match ip with
        | V4 v4 -> (v4 &&& 0xffff_0000u) = 0xa9fe_0000u
        | V6 v6 -> (v6 >>> 118) = UInt128(0UL, 0xfe80UL >>> 6) || (mappedV4 ip |> Option.exists (fun a -> (a &&& 0xffff_0000u) = 0xa9fe_0000u))

    /// `ip_address_is_public`
    let validate (ipAddress: string) : Errors =
        match parseIpAddr ipAddress with
        | Some ip when isLoopback ip || isPrivate ip || isLinkLocal ip ->
            Errors.empty |> Errors.add "ip_address" "cannot be a private or internal IP address"
        | Some _ -> Errors.empty
        | None -> Errors.empty |> Errors.add "ip_address" "is not a valid IP address"

    /// `bans.create!(ip_address:)`, validating the address is public.
    let create (tx: Tx) (userId: int64) (ipAddress: string) : Ban =
        Err.validate (validate ipAddress)
        let now = tx.Now()
        let id =
            tx.Conn.QueryRow(
                """INSERT INTO "bans" ("created_at", "ip_address", "updated_at", "user_id") VALUES (?, ?, ?, ?) RETURNING "id" """,
                [| T now; S ipAddress; T now; I userId |],
                fun r -> r.Int64 0
            )
        { Id = id
          UserId = userId
          IpAddress = ipAddress
          CreatedAt = now
          UpdatedAt = now }
