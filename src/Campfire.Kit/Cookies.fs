// Port of rust/crates/kit/src/cookies.rs
//
// `ActionDispatch::Cookies::CookieJar` with its plain, permanent, signed and encrypted jars.
//
// Semantics follow `action_dispatch/middleware/cookies.rb`:
// - setting a cookie only emits `Set-Cookie` when the value changed or an expiry was given;
// - every cookie gets `path=/` and `samesite=lax` unless told otherwise
//   (`cookies_same_site_protection = :lax` from `load_defaults 6.1`);
// - `permanent` means an `expires` 20 calendar years out, embedded in signed/encrypted metadata;
// - deleting only emits a header when the cookie was present;
// - secure cookies are dropped on plain-HTTP requests;
// - signed/encrypted values over 4096 bytes (name included) raise `CookieOverflow`.
//
// Header formatting is `Rack::Utils.set_cookie_header` (Rack 3.2).
namespace Campfire.Kit

open System
open System.Buffers
open System.Collections.Generic
open System.Text
open System.Text.Unicode
open Campfire.RailsCompat
open Campfire.RailsCompat.Clock
open Campfire.Kit

[<RequireQualifiedAccess>]
type SameSite =
    | Lax
    | Strict
    | None

/// A cookie to set, with Rails' option names.
type Cookie =
    { Value: string
      Path: string
      Domain: string | null
      Expires: Timestamp voption
      Permanent: bool
      Secure: bool
      HttpOnly: bool
      SameSite: SameSite voption
      Partitioned: bool }

    static member New(value: string) : Cookie =
        { Value = value
          Path = "/"
          Domain = null
          Expires = ValueNone
          Permanent = false
          Secure = false
          HttpOnly = false
          SameSite = ValueSome SameSite.Lax
          Partitioned = false }

    /// `cookies.permanent[...]`: expires 20 years from now.
    member this.AsPermanent() : Cookie = { this with Permanent = true }

    member this.WithExpires(at: Timestamp) : Cookie = { this with Expires = ValueSome at }
    member this.AsHttpOnly() : Cookie = { this with HttpOnly = true }
    member this.AsSecure() : Cookie = { this with Secure = true }
    member this.WithSameSite(sameSite: SameSite voption) : Cookie = { this with SameSite = sameSite }
    member this.WithPath(path: string) : Cookie = { this with Path = path }
    member this.WithDomain(domain: string) : Cookie = { this with Domain = domain }

/// Options for `cookies.delete(name, options)`.
type DeleteOptions =
    { Path: string
      Domain: string | null
      SameSite: SameSite voption }

    static member Default: DeleteOptions =
        { Path = "/"
          Domain = null
          SameSite = ValueSome SameSite.Lax }

/// A short list of named values that keeps insertion order and gets an index once it is long: cookie
/// names come from the client, so a header of tens of thousands of them must stay linear.
[<Sealed>]
type internal OrderedMap<'V>() =
    // Most requests set no cookie at all, so nothing is allocated until the first one.
    let mutable names: ResizeArray<string> | null = null
    let mutable values: ResizeArray<'V> | null = null
    let mutable index: Dictionary<string, int> | null = null

    member _.Count =
        match names with
        | null -> 0
        | names -> names.Count

    member _.NameAt(i: int) = (nonNull names)[i]
    member _.ValueAt(i: int) = (nonNull values)[i]

    member _.Find(name: string) : int =
        match names with
        | null -> -1
        | names ->
            match index with
            | null ->
                let mutable found = -1
                let mutable i = 0
                while found < 0 && i < names.Count do
                    if String.Equals(names[i], name, StringComparison.Ordinal) then found <- i
                    i <- i + 1
                found
            | index ->
                match index.TryGetValue name with
                | true, i -> i
                | _ -> -1

    /// Replaces in place, keeping the position, or adds at the end.
    member this.Upsert(name: string, value: 'V) : unit =
        match this.Find name with
        | -1 ->
            if isNull names then
                names <- ResizeArray<string>(4)
                values <- ResizeArray<'V>(4)
            let names = nonNull names
            names.Add name
            (nonNull values).Add value
            match index with
            | null ->
                if names.Count > 16 then
                    let built = Dictionary<string, int>(names.Count * 2, StringComparer.Ordinal)
                    for i in 0 .. names.Count - 1 do
                        built[names[i]] <- i
                    index <- built
            | index -> index[name] <- names.Count - 1
        | i -> (nonNull values)[i] <- value

    member this.Remove(name: string) : unit =
        match this.Find name with
        | -1 -> ()
        | i ->
            let names = nonNull names
            names.RemoveAt i
            (nonNull values).RemoveAt i
            match index with
            | null -> ()
            | _ ->
                let rebuilt = Dictionary<string, int>(names.Count * 2, StringComparer.Ordinal)
                for j in 0 .. names.Count - 1 do
                    rebuilt[names[j]] <- j
                index <- rebuilt

module CookieJar =
    [<Literal>]
    let MaxCookieSize = 4096

    [<Literal>]
    let private PermanentYears = 20

    /// `Rack::Utils.escape`, which Rack applies to every cookie value it writes.
    let escape (raw: string) : string = Cookies.escape raw

    let private hexValue (c: char) : int =
        if c >= '0' && c <= '9' then int c - int '0'
        elif c >= 'a' && c <= 'f' then int c - int 'a' + 10
        elif c >= 'A' && c <= 'F' then int c - int 'A' + 10
        else -1

    /// `URI.decode_www_form_component(value)` as UTF-8, or the value as it came when that fails.
    let private decodeValue (value: string) : string =
        if value.IndexOfAny [| '%'; '+' |] < 0 then
            value
        else
            let bytes = Encoding.UTF8.GetBytes value
            let decoded = ResizeArray<byte>(bytes.Length)
            let mutable i = 0
            let mutable ok = true
            while ok && i < bytes.Length do
                let b = bytes[i]
                if b = byte '+' then
                    decoded.Add(byte ' ')
                    i <- i + 1
                elif b = byte '%' then
                    if i + 2 < bytes.Length && hexValue (char bytes[i + 1]) >= 0 && hexValue (char bytes[i + 2]) >= 0 then
                        decoded.Add(byte (hexValue (char bytes[i + 1]) * 16 + hexValue (char bytes[i + 2])))
                        i <- i + 3
                    else
                        ok <- false
                else
                    decoded.Add b
                    i <- i + 1
            if not ok then
                value
            else
                let bytes = decoded.ToArray()
                if Utf8.IsValid(ReadOnlySpan<byte> bytes) then Encoding.UTF8.GetString bytes else value

    /// `Rack::Utils.parse_cookies_header`: split on `/; */`, first occurrence wins, values unescaped
    /// (kept raw when unescaping fails).
    let parseCookieHeader (header: string) : (string * string) list =
        let cookies = OrderedMap<string>()
        let mutable start = 0
        let mutable first = true
        let mutable go = true
        while go do
            let semi = header.IndexOf(';', start)
            let stop = if semi < 0 then header.Length else semi
            let mutable from = start
            if not first then
                while from < stop && header[from] = ' ' do
                    from <- from + 1
            first <- false
            if stop > from then
                let eq = header.IndexOf('=', from, stop - from)
                let key = if eq < 0 then header.Substring(from, stop - from) else header.Substring(from, eq - from)
                if cookies.Find key < 0 then
                    let value = if eq < 0 then "" else decodeValue (header.Substring(eq + 1, stop - eq - 1))
                    cookies.Upsert(key, value)
            if semi < 0 then go <- false else start <- semi + 1
        [ for i in 0 .. cookies.Count - 1 -> cookies.NameAt i, cookies.ValueAt i ]

    let private sameSiteAttribute (sameSite: SameSite voption) : string =
        match sameSite with
        | ValueNone -> ""
        | ValueSome SameSite.Lax -> "; samesite=lax"
        | ValueSome SameSite.Strict -> "; samesite=strict"
        | ValueSome SameSite.None -> "; samesite=none"

    /// `Rack::Utils.set_cookie_header(key, value_hash)`.
    let setCookieHeader (name: string) (cookie: Cookie) : string =
        let header = StringBuilder(name.Length + cookie.Value.Length + 96)
        header.Append(name).Append('=').Append(escape cookie.Value) |> ignore
        match cookie.Domain with
        | null -> ()
        | domain -> header.Append("; domain=").Append domain |> ignore
        header.Append("; path=").Append cookie.Path |> ignore
        match cookie.Expires with
        | ValueSome expires -> header.Append("; expires=").Append(KitClock.httpdate expires) |> ignore
        | ValueNone -> ()
        if cookie.Secure then header.Append "; secure" |> ignore
        if cookie.HttpOnly then header.Append "; httponly" |> ignore
        header.Append(sameSiteAttribute cookie.SameSite) |> ignore
        if cookie.Partitioned then header.Append "; partitioned" |> ignore
        header.ToString()

    /// `Rack::Utils.delete_set_cookie_header(key, options)`.
    let deleteCookieHeader (name: string) (options: DeleteOptions) : string =
        let header = StringBuilder(name.Length + 96)
        header.Append(name).Append '=' |> ignore
        match options.Domain with
        | null -> ()
        | domain -> header.Append("; domain=").Append domain |> ignore
        header
            .Append("; path=")
            .Append(options.Path)
            .Append("; max-age=0; expires=")
            .Append(KitClock.httpdate Timestamps.unixEpoch)
        |> ignore
        header.Append(sameSiteAttribute options.SameSite) |> ignore
        header.ToString()

    let internal checkForOverflow (name: string) (value: string) : Result<unit, Error> =
        let total = Encoding.UTF8.GetByteCount name + Encoding.UTF8.GetByteCount value
        if total > MaxCookieSize then
            Error(CookieOverflow $"{name} cookie overflowed with size {total} bytes")
        else
            Ok()

[<Sealed>]
type CookieJar(secrets: Secrets, clock: SharedClock) =
    /// The current value of every cookie: the request's, updated by sets and deletes.
    let cookies = OrderedMap<string>()
    let setCookies = OrderedMap<Cookie>()
    let deleteCookies = OrderedMap<DeleteOptions>()

    /// Build the jar from the request's `Cookie` header(s).
    static member FromHeaders(headers: string seq, secrets: Secrets, clock: SharedClock) : CookieJar =
        let jar = CookieJar(secrets, clock)
        for header in headers do
            jar.AddHeader header
        jar

    /// Add the cookies of one `Cookie` header line; a name already present keeps its value.
    member _.AddHeader(header: string) : unit =
        for (name, value) in CookieJar.parseCookieHeader header do
            if cookies.Find name < 0 then cookies.Upsert(name, value)

    /// `cookies[name]`.
    member _.Get(name: string) : string | null =
        match cookies.Find name with
        | -1 -> null
        | i -> cookies.ValueAt i

    member this.Contains(name: string) : bool = not (isNull (this.Get name))

    /// `cookies.signed[name]`.
    member this.Signed(name: string) : string | null =
        match this.Get name with
        | null -> null
        | raw ->
            match Cookies.verifySigned secrets name raw (clock.Now()) with
            | Some value -> value
            | None -> null

    /// `cookies.encrypted[name]`.
    member this.Encrypted(name: string) : Value voption =
        match this.Get name with
        | null -> ValueNone
        | raw -> Cookies.decrypt secrets name raw (clock.Now()) |> ValueOption.ofOption

    member private _.ResolveExpiry(cookie: Cookie) : Cookie =
        if cookie.Permanent then
            { cookie with Expires = ValueSome(KitClock.yearsFrom (clock.Now()) 20) }
        else
            cookie

    member private this.WriteValue(name: string, cookie: Cookie) : unit =
        let changed =
            match this.Get name with
            | null -> true
            | current -> not (String.Equals(current, cookie.Value, StringComparison.Ordinal))
        if changed || cookie.Expires.IsSome then
            cookies.Upsert(name, cookie.Value)
            setCookies.Upsert(name, cookie)
            deleteCookies.Remove name

    /// `cookies[name] = value` (or `cookies.permanent[name] = ...` with `Cookie.AsPermanent`).
    member this.Set(name: string, cookie: Cookie) : unit = this.WriteValue(name, this.ResolveExpiry cookie)

    member this.Set(name: string, value: string) : unit = this.Set(name, Cookie.New value)

    /// `cookies.signed[name] = value`.
    member this.SetSigned(name: string, cookie: Cookie) : Result<unit, Error> =
        let cookie = this.ResolveExpiry cookie
        let signed =
            { cookie with Value = Cookies.sign secrets name cookie.Value (ValueOption.toOption cookie.Expires) }
        match CookieJar.checkForOverflow name signed.Value with
        | Error e -> Error e
        | Ok() ->
            this.WriteValue(name, signed)
            Ok()

    member this.SetSigned(name: string, value: string) : Result<unit, Error> = this.SetSigned(name, Cookie.New value)

    /// `cookies.encrypted[name] = value`; `cookie.Value` is ignored in favor of `value`.
    member this.SetEncrypted(name: string, value: Value, cookie: Cookie) : Result<unit, Error> =
        let cookie = this.ResolveExpiry cookie
        let encrypted =
            { cookie with Value = Cookies.encrypt secrets name value (ValueOption.toOption cookie.Expires) }
        match CookieJar.checkForOverflow name encrypted.Value with
        | Error e -> Error e
        | Ok() ->
            this.WriteValue(name, encrypted)
            Ok()

    /// `cookies.delete(name)`: a no-op unless the cookie is present.
    member this.Delete(name: string) : unit = this.DeleteWith(name, DeleteOptions.Default)

    member _.DeleteWith(name: string, options: DeleteOptions) : unit =
        if cookies.Find name >= 0 then
            cookies.Remove name
            deleteCookies.Upsert(name, options)

    /// `cookies.deleted?(name)`.
    member _.IsDeleted(name: string) : bool = deleteCookies.Find name >= 0

    /// Whether the response needs any `Set-Cookie` at all.
    member _.HasChanges: bool = setCookies.Count > 0 || deleteCookies.Count > 0

    /// The `Set-Cookie` header values Rails would write, in order.
    member _.SetCookieHeaders(ssl: bool, host: string) : ResizeArray<string> =
        let headers = ResizeArray<string>()
        for i in 0 .. setCookies.Count - 1 do
            let cookie = setCookies.ValueAt i
            if ssl || not cookie.Secure || host.EndsWith(".onion", StringComparison.Ordinal) then
                headers.Add(CookieJar.setCookieHeader (setCookies.NameAt i) cookie)
        for i in 0 .. deleteCookies.Count - 1 do
            headers.Add(CookieJar.deleteCookieHeader (deleteCookies.NameAt i) (deleteCookies.ValueAt i))
        headers
