// Port of rust/crates/richtext/src/uri.rs
//
// The slice of Ruby's `URI.parse` (uri 1.1, RFC 3986 parser) that the opengraph URL checks and
// tweet URL normalization rely on, including which inputs raise which error.
namespace Campfire.RichText

open System
open System.Text

type UriError =
    /// `URI::InvalidURIError`, which callers rescue.
    | InvalidUri
    /// `URI::InvalidComponentError` (from `URI::MailTo`), which nothing rescues.
    | InvalidComponent

type RubyUri =
    { Scheme: string option
      Userinfo: string option
      Host: string option
      Port: uint64 option
      Path: string option
      Opaque: string option
      Query: string option
      Fragment: string option }

module RubyUri =
    /// `uri.is_a?(URI::HTTP)`, which includes `URI::HTTPS`.
    let isHttp (uri: RubyUri) : bool =
        match uri.Scheme with
        | Some s -> s.Equals("http", StringComparison.OrdinalIgnoreCase) || s.Equals("https", StringComparison.OrdinalIgnoreCase)
        | None -> false

    let private asciiUpper (s: string) : string = String(s.ToCharArray() |> Array.map (fun c -> if c >= 'a' && c <= 'z' then char (int c - 32) else c))

    let private defaultPort (uri: RubyUri) : uint64 option =
        match uri.Scheme |> Option.map asciiUpper with
        | Some "HTTP"
        | Some "WS" -> Some 80UL
        | Some "HTTPS"
        | Some "WSS" -> Some 443UL
        | Some "FTP" -> Some 21UL
        | Some "LDAP" -> Some 389UL
        | Some "LDAPS" -> Some 636UL
        | _ -> None

    /// `URI::Generic#to_s`.
    let toS (uri: RubyUri) : string =
        let s = StringBuilder()
        match uri.Scheme with
        | Some scheme -> s.Append(scheme).Append ':' |> ignore
        | None -> ()
        match uri.Opaque with
        | Some opaque -> s.Append opaque |> ignore
        | None ->
            if uri.Host.IsSome || uri.Scheme = Some "file" || uri.Scheme = Some "postgres" then s.Append "//" |> ignore
            match uri.Userinfo with
            | Some userinfo -> s.Append(userinfo).Append '@' |> ignore
            | None -> ()
            match uri.Host with
            | Some host -> s.Append host |> ignore
            | None -> ()
            match uri.Port with
            | Some port when Some port <> defaultPort uri -> s.Append(':').Append(port) |> ignore
            | _ -> ()
            s.Append(defaultArg uri.Path "") |> ignore
            match uri.Query with
            | Some query -> s.Append('?').Append query |> ignore
            | None -> ()
        match uri.Fragment with
        | Some fragment -> s.Append('#').Append fragment |> ignore
        | None -> ()
        s.ToString()

    /// `/\A(?:[^@,;]+@[^@,;]+(?:\z|[,;]))*\z/`
    let private mailtoToValid (``to``: string) : bool =
        let mutable i = 0
        let special (c: char) = c = '@' || c = ',' || c = ';'
        let mutable valid = true
        while valid && i < ``to``.Length do
            let start = i
            while i < ``to``.Length && not (special ``to``[i]) do
                i <- i + 1
            if i = start || i >= ``to``.Length || ``to``[i] <> '@' then
                valid <- false
            else
                i <- i + 1
                let domain = i
                while i < ``to``.Length && not (special ``to``[i]) do
                    i <- i + 1
                if i = domain then
                    valid <- false
                elif i < ``to``.Length then
                    if ``to``[i] = '@' then valid <- false else i <- i + 1
        valid

    /// The initializers of the scheme classes `URI.for` picks that can raise.
    let private checkSchemeClass (uri: RubyUri) : Result<unit, UriError> =
        match uri.Scheme |> Option.map asciiUpper with
        | Some "MAILTO" ->
            let opaque =
                match uri.Opaque with
                | Some o -> Some o
                | None -> uri.Query |> Option.map (fun q -> "?" + q)
            match opaque with
            | None -> Error InvalidComponent
            | Some opaque ->
                let ``to`` =
                    match opaque.IndexOf '?' with
                    | -1 -> opaque
                    | q -> opaque.Substring(0, q)
                if mailtoToValid ``to`` then Ok() else Error InvalidComponent
        | Some "LDAP"
        | Some "LDAPS" -> if uri.Fragment.IsSome || uri.Path.IsNone then Error InvalidUri else Ok()
        | Some "FTP" -> if uri.Path.IsNone then Error InvalidUri else Ok()
        | _ -> Ok()

    let private isHexDigit (b: char) = (b >= '0' && b <= '9') || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F')

    /// `URI::Generic#query=`: rejects `%` followed by two non-hex characters and escapes the rest.
    let private escapeQuery (query: string) : Result<string, UriError> =
        let cleaned = query.ToCharArray() |> Array.filter (fun b -> b <> '\t' && b <> '\r' && b <> '\n')
        let mutable bad = false
        for w in 0 .. cleaned.Length - 3 do
            if cleaned[w] = '%' && not (isHexDigit cleaned[w + 1]) && not (isHexDigit cleaned[w + 2]) then bad <- true
        if bad then
            Error InvalidUri
        else
            let out = StringBuilder()
            for i in 0 .. cleaned.Length - 1 do
                let b = cleaned[i]
                let escape = i + 2 < cleaned.Length && b = '%' && isHexDigit cleaned[i + 1] && isHexDigit cleaned[i + 2]
                // b'!' | b'$'..=b'&' | b'('..=b';' | b'=' | b'?'..=b'_' | b'a'..=b'~'
                let plain =
                    b = '!' || (b >= '$' && b <= '&') || (b >= '(' && b <= ';') || b = '=' || (b >= '?' && b <= '_') || (b >= 'a' && b <= '~')
                if escape || plain then out.Append b |> ignore else out.Append('%').Append((int b).ToString("X2")) |> ignore
            Ok(out.ToString())

    // --- RFC 3986 matching, mirroring URI::RFC3986_Parser::RFC3986_URI -----------------------

    /// [!$&-.0-9;=A-Z_a-z~] — note `&-.` covers & ' ( ) * + , - .
    let private isUnreservedOrSub (b: char) =
        b = '!' || b = '$' || (b >= '&' && b <= '.') || (b >= '0' && b <= '9') || b = ';' || b = '=' || (b >= 'A' && b <= 'Z') || b = '_'
        || (b >= 'a' && b <= 'z') || b = '~'

    let private pctAt (s: string) (i: int) = i + 2 < s.Length && s[i] = '%' && isHexDigit s[i + 1] && isHexDigit s[i + 2]

    /// Consumes `(?:%\h\h|[class])*` possessively and returns the end index.
    let private takeWhileClass (s: string) (start: int) (cls: char -> bool) : int =
        let mutable i = start
        let mutable fin = false
        while not fin do
            if pctAt s i then i <- i + 3
            elif i < s.Length && cls s[i] then i <- i + 1
            else fin <- true
        i

    let private segChar (b: char) = isUnreservedOrSub b || b = ':' || b = '@' || b = '/'
    let private segNcChar (b: char) = isUnreservedOrSub b || b = '@'
    let private fragmentChar (b: char) = isUnreservedOrSub b || b = ':' || b = '@' || b = '/' || b = '?'
    let private userinfoChar (b: char) = isUnreservedOrSub b || b = ':'

    /// Rust's `std::net::Ipv6Addr` parser: groups of up to four hex digits, one `::`, and a
    /// trailing dotted quad without leading zeros.
    let private isIpv6 (s: string) : bool =
        let mutable pos = 0
        let peek () = if pos < s.Length then int s[pos] else -1
        let readGiven (c: char) =
            if peek () = int c then
                pos <- pos + 1
                true
            else
                false
        // read_number: digits of the radix, at most `maxDigits`, no leading zero unless allowed
        let readNumber (radix: int) (maxDigits: int) (allowZeroPrefix: bool) : int voption =
            let start = pos
            let hasLeadingZero = peek () = int '0'
            let mutable result = 0
            let mutable count = 0
            let mutable ok = true
            let digit (c: char) =
                if c >= '0' && c <= '9' then int c - int '0'
                elif radix = 16 && c >= 'a' && c <= 'f' then int c - int 'a' + 10
                elif radix = 16 && c >= 'A' && c <= 'F' then int c - int 'A' + 10
                else -1
            while ok && pos < s.Length && digit s[pos] >= 0 do
                result <- result * radix + digit s[pos]
                pos <- pos + 1
                count <- count + 1
                if count > maxDigits then ok <- false
            if not ok || count = 0 || (not allowZeroPrefix && hasLeadingZero && count > 1) then
                pos <- start
                ValueNone
            else
                ValueSome result
        let readSeparator (sep: char) (index: int) (inner: unit -> bool) : bool =
            let start = pos
            let ok = (index = 0 || readGiven sep) && inner ()
            if not ok then pos <- start
            ok
        let readIpv4 () : bool =
            let start = pos
            let mutable ok = true
            for i in 0 .. 3 do
                if ok then
                    ok <-
                        readSeparator '.' i (fun () ->
                            match readNumber 10 3 false with
                            | ValueSome v when v <= 255 -> true
                            | ValueSome _ ->
                                false
                            | ValueNone -> false)
            if not ok then pos <- start
            ok
        // read_groups: returns the group count and whether it ended in an IPv4 address
        let readGroups (limit: int) : struct (int * bool) =
            let mutable i = 0
            let mutable result = ValueNone
            while result.IsNone && i < limit do
                let mutable consumed = false
                // A trailing embedded IPv4 address needs at least two groups left.
                if i < limit - 1 then
                    if readSeparator ':' i readIpv4 then
                        consumed <- true
                        result <- ValueSome(struct (i + 2, true))
                if not consumed then
                    if readSeparator ':' i (fun () -> (readNumber 16 4 true).IsSome) then i <- i + 1
                    else result <- ValueSome(struct (i, false))
            match result with
            | ValueSome r -> r
            | ValueNone -> struct (limit, false)
        let struct (headSize, headIpv4) = readGroups 8
        let parsed =
            if headSize = 8 then
                true
            elif headIpv4 then
                false
            elif not (readGiven ':') || not (readGiven ':') then
                false
            else
                // The :: must contain at least one set of zeroes, so the tail has at most 7 groups.
                let limit = 8 - (headSize + 1)
                readGroups limit |> ignore
                true
        parsed && pos = s.Length

    /// Matches the IP-literal alternative of HOST (a bracketed address); returns its end.
    let private ipLiteral (s: string) (i: int) : int voption =
        if i >= s.Length || s[i] <> '[' then
            ValueNone
        else
            match s.IndexOf(']', i) with
            | -1 -> ValueNone
            | close ->
                let inner = s.Substring(i + 1, close - i - 1)
                let valid =
                    if inner.Length > 0 && (inner[0] = 'v' || inner[0] = 'V') then
                        match inner.Substring(1).IndexOf '.' with
                        | -1 -> false
                        | dot ->
                            let hex = inner.Substring(1, dot)
                            let rest = inner.Substring(dot + 2)
                            hex.Length > 0
                            && hex |> Seq.forall isHexDigit
                            && rest.Length > 0
                            && rest |> Seq.forall (fun b -> isUnreservedOrSub b || b = ':')
                    else
                        isIpv6 inner && not (inner.Contains '%')
                if valid then ValueSome(close + 1) else ValueNone

    [<Struct>]
    type private Tail =
        { HierEnd: int
          Query: struct (int * int) voption
          Fragment: struct (int * int) voption }

    /// Splits off `(?:\?(?<query>[^#]*+))?(?:\#(?<fragment>FRAGMENT))?\z` from the end of the hier-part.
    let private parseQueryFragmentPositions (s: string) (start: int) : Tail voption =
        let mutable hierEnd = s.Length
        let mutable k = start
        while hierEnd = s.Length && k < s.Length do
            if s[k] = '?' || s[k] = '#' then hierEnd <- k
            k <- k + 1
        let mutable i = hierEnd
        let mutable query = ValueNone
        if i < s.Length && s[i] = '?' then
            let mutable qEnd = s.Length
            let mutable j = i + 1
            while qEnd = s.Length && j < s.Length do
                if s[j] = '#' then qEnd <- j
                j <- j + 1
            query <- ValueSome(struct (i + 1, qEnd))
            i <- qEnd
        let mutable fragment = ValueNone
        let mutable ok = true
        if i < s.Length && s[i] = '#' then
            let fEnd = takeWhileClass s (i + 1) fragmentChar
            if fEnd <> s.Length then
                ok <- false
            else
                fragment <- ValueSome(struct (i + 1, s.Length))
                i <- s.Length
        if ok && i = s.Length then ValueSome { HierEnd = hierEnd; Query = query; Fragment = fragment } else ValueNone

    [<Struct>]
    type private Authority =
        { UserInfo: string option
          AuthHost: string
          AuthPort: uint64 option
          AuthEnd: int }

    let private parseAuthority (value: string) (start: int) (limit: int) : Authority voption =
        let s = value.Substring(0, limit)
        let mutable i = start
        let mutable userinfo = None
        let uiEnd = takeWhileClass s i userinfoChar
        if uiEnd < s.Length && s[uiEnd] = '@' then
            userinfo <- Some(value.Substring(i, uiEnd - i))
            i <- uiEnd + 1
        let hostEnd =
            match ipLiteral s i with
            | ValueSome e -> e
            | ValueNone -> takeWhileClass s i isUnreservedOrSub
        let host = value.Substring(i, hostEnd - i)
        let mutable end_ = hostEnd
        let mutable port = None
        if end_ < s.Length && s[end_] = ':' then
            let mutable digitsEnd = s.Length
            let mutable j = end_ + 1
            while digitsEnd = s.Length && j < s.Length do
                if not (s[j] >= '0' && s[j] <= '9') then digitsEnd <- j
                j <- j + 1
            let digits = value.Substring(end_ + 1, digitsEnd - end_ - 1)
            port <-
                if digits.Length = 0 then None
                else
                    match UInt64.TryParse(digits, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
                    | true, p -> Some p
                    | _ -> Some UInt64.MaxValue
            end_ <- digitsEnd
        if end_ < s.Length && s[end_] <> '/' then
            ValueNone
        else
            ValueSome { UserInfo = userinfo; AuthHost = host; AuthPort = port; AuthEnd = end_ }

    let private splitAbsolute (value: string) : RubyUri option =
        let s = value
        // scheme
        if s.Length = 0 || not ((s[0] >= 'a' && s[0] <= 'z') || (s[0] >= 'A' && s[0] <= 'Z')) then
            None
        else
            let mutable i = 1
            while i < s.Length && (Char.IsAsciiLetterOrDigit s[i] || s[i] = '+' || s[i] = '-' || s[i] = '.') do
                i <- i + 1
            if i >= s.Length || s[i] <> ':' then
                None
            else
                // `URI::Generic#set_scheme` downcases it.
                let scheme = String(value.Substring(0, i).ToCharArray() |> Array.map (fun c -> if c >= 'A' && c <= 'Z' then char (int c + 32) else c))
                let restStart = i + 1
                match parseQueryFragmentPositions s restStart with
                | ValueNone -> None
                | ValueSome tail ->
                    let hier = s.Substring(restStart, tail.HierEnd - restStart)
                    let slice (v: struct (int * int) voption) =
                        match v with
                        | ValueSome(struct (a, b)) -> Some(value.Substring(a, b - a))
                        | ValueNone -> None
                    let mutable uri =
                        { Scheme = Some scheme
                          Userinfo = None
                          Host = None
                          Port = None
                          Path = None
                          Opaque = None
                          Query = slice tail.Query
                          Fragment = slice tail.Fragment }
                    if hier.StartsWith("//", StringComparison.Ordinal) then
                        match parseAuthority value (restStart + 2) tail.HierEnd with
                        | ValueNone -> None
                        | ValueSome authority ->
                            // path-abempty: (?:/seg*)?
                            let path = value.Substring(authority.AuthEnd, tail.HierEnd - authority.AuthEnd)
                            if path.Length > 0 && (path[0] <> '/' || takeWhileClass s authority.AuthEnd segChar <> tail.HierEnd) then
                                None
                            else
                                Some
                                    { uri with
                                        Userinfo = authority.UserInfo
                                        Host = Some authority.AuthHost
                                        Port = authority.AuthPort
                                        Path = Some path }
                    elif hier.StartsWith("/", StringComparison.Ordinal) then
                        // path-absolute: /((?!/)seg++)?
                        if takeWhileClass s restStart segChar <> tail.HierEnd then
                            None
                        else
                            Some { uri with Path = Some(value.Substring(restStart, tail.HierEnd - restStart)) }
                    elif hier.Length > 0 then
                        // path-rootless becomes the opaque part, with the query folded back in
                        if takeWhileClass s restStart segChar <> tail.HierEnd then
                            None
                        else
                            let mutable opaque = value.Substring(restStart, tail.HierEnd - restStart)
                            match uri.Query with
                            | Some q ->
                                opaque <- opaque + "?" + q
                                uri <- { uri with Query = None }
                            | None -> ()
                            Some { uri with Opaque = Some opaque }
                    else
                        Some { uri with Path = Some "" }

    /// RFC3986_relative_ref: only validity matters here, since a relative reference is never HTTP.
    let private splitRelative (value: string) : RubyUri option =
        let s = value
        match parseQueryFragmentPositions s 0 with
        | ValueNone -> None
        | ValueSome tail ->
            let hier = s.Substring(0, tail.HierEnd)
            let valid =
                if hier.StartsWith("//", StringComparison.Ordinal) then
                    match parseAuthority value 2 tail.HierEnd with
                    | ValueSome a -> takeWhileClass s a.AuthEnd segChar = tail.HierEnd
                    | ValueNone -> false
                elif hier.StartsWith("/", StringComparison.Ordinal) || hier.Length = 0 then
                    takeWhileClass s 0 segChar = tail.HierEnd
                else
                    let first = takeWhileClass s 0 segNcChar
                    first > 0 && (first = tail.HierEnd || (s[first] = '/' && takeWhileClass s first segChar = tail.HierEnd))
            if valid then
                let slice (v: struct (int * int) voption) =
                    match v with
                    | ValueSome(struct (a, b)) -> Some(value.Substring(a, b - a))
                    | ValueNone -> None
                Some
                    { Scheme = None
                      Userinfo = None
                      Host = None
                      Port = None
                      Path = Some(value.Substring(0, tail.HierEnd))
                      Opaque = None
                      Query = slice tail.Query
                      Fragment = slice tail.Fragment }
            else
                None

    /// `URI.parse(value)`.
    let parse (value: string) : Result<RubyUri, UriError> =
        if not (value |> Seq.forall (fun c -> c < '\u0080')) then
            Error InvalidUri
        else
            match splitAbsolute value |> Option.orElse (splitRelative value) with
            | None -> Error InvalidUri
            | Some uri ->
                // URI::Generic#initialize assigns the query through `query=`, which rejects bad escapes
                let queried =
                    match uri.Query with
                    | Some query -> escapeQuery query |> Result.map (fun q -> { uri with Query = Some q })
                    | None -> Ok uri
                match queried with
                | Error e -> Error e
                | Ok uri ->
                    let uri = { uri with Port = uri.Port |> Option.orElse (defaultPort uri) }
                    match checkSchemeClass uri with
                    | Error e -> Error e
                    | Ok() -> Ok uri
