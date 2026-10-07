// Port of rust/crates/kit/src/format.rs
namespace Campfire.Kit

open System
open System.Collections.Generic

/// A registered MIME type (`Mime[:html]` and friends). Equal when the symbols are.
[<CustomEquality; NoComparison>]
type Mime =
    { Symbol: string
      String: string
      Synonyms: string[]
      Extensions: string[] }

    override this.Equals(other: obj) =
        match other with
        | :? Mime as other -> String.Equals(this.Symbol, other.Symbol, StringComparison.Ordinal)
        | _ -> false

    override this.GetHashCode() = this.Symbol.GetHashCode()
    override this.ToString() = this.String

    member this.Is(symbol: string) = String.Equals(this.Symbol, symbol, StringComparison.Ordinal)

    /// `Mime::Type#match?`: `pattern` appears in the type or one of its synonyms.
    member internal this.Matches(pattern: string) =
        this.String.Contains(pattern, StringComparison.Ordinal)
        || this.Synonyms |> Array.exists (fun s -> s.Contains(pattern, StringComparison.Ordinal))

type Format = Mime

/// `Mime::Type.lookup` found a string that isn't a MIME type at all (`InvalidMimeType`).
type InvalidMimeType = InvalidMimeType of string

/// MIME types and format negotiation: `Mime::Type`, `Mime::Type.parse` (the `Accept` header),
/// `ActionDispatch::Http::MimeNegotiation#formats` and `respond_to`'s `negotiate_mime`.
module Format =
    let private mime symbol string synonyms extensions : Mime =
        { Symbol = symbol
          String = string
          Synonyms = Array.ofList synonyms
          Extensions = Array.ofList extensions }

    // `action_dispatch/http/mime_types.rb`, then turbo-rails' `:turbo_stream`, in registration order
    // (the order matters for `text/*` expansion).
    let Html = mime "html" "text/html" [ "application/xhtml+xml" ] [ "xhtml" ]
    let Text = mime "text" "text/plain" [] [ "txt" ]
    let Js = mime "js" "text/javascript" [ "application/javascript"; "application/x-javascript" ] []
    let Css = mime "css" "text/css" [] []
    let Ics = mime "ics" "text/calendar" [] []
    let Csv = mime "csv" "text/csv" [] []
    let Vcf = mime "vcf" "text/vcard" [] []
    let Vtt = mime "vtt" "text/vtt" [] [ "vtt" ]
    let Md = mime "md" "text/markdown" [] [ "md"; "markdown" ]
    let Png = mime "png" "image/png" [] [ "png" ]
    let Jpeg = mime "jpeg" "image/jpeg" [] [ "jpg"; "jpeg"; "jpe"; "pjpeg" ]
    let Gif = mime "gif" "image/gif" [] [ "gif" ]
    let Bmp = mime "bmp" "image/bmp" [] [ "bmp" ]
    let Tiff = mime "tiff" "image/tiff" [] [ "tif"; "tiff" ]
    let Svg = mime "svg" "image/svg+xml" [] []
    let Webp = mime "webp" "image/webp" [] [ "webp" ]
    let Mpeg = mime "mpeg" "video/mpeg" [] [ "mpg"; "mpeg"; "mpe" ]
    let Mp3 = mime "mp3" "audio/mpeg" [] [ "mp1"; "mp2"; "mp3" ]
    let Ogg = mime "ogg" "audio/ogg" [] [ "oga"; "ogg"; "spx"; "opus" ]
    let M4a = mime "m4a" "audio/aac" [ "audio/mp4" ] [ "m4a"; "mpg4"; "aac" ]
    let Webm = mime "webm" "video/webm" [] [ "webm" ]
    let Mp4 = mime "mp4" "video/mp4" [] [ "mp4"; "m4v" ]
    let Otf = mime "otf" "font/otf" [] [ "otf" ]
    let Ttf = mime "ttf" "font/ttf" [] [ "ttf" ]
    let Woff = mime "woff" "font/woff" [] [ "woff" ]
    let Woff2 = mime "woff2" "font/woff2" [] [ "woff2" ]
    let Xml = mime "xml" "application/xml" [ "text/xml"; "application/x-xml" ] []
    let Rss = mime "rss" "application/rss+xml" [] []
    let Atom = mime "atom" "application/atom+xml" [] []
    let Yaml = mime "yaml" "application/x-yaml" [ "text/yaml" ] [ "yml"; "yaml" ]
    let MultipartForm = mime "multipart_form" "multipart/form-data" [] []
    let UrlEncodedForm = mime "url_encoded_form" "application/x-www-form-urlencoded" [] []
    let Json = mime "json" "application/json" [ "text/x-json"; "application/jsonrequest"; "application/problem+json" ] []
    let Pdf = mime "pdf" "application/pdf" [] [ "pdf" ]
    let Zip = mime "zip" "application/zip" [] [ "zip" ]
    let Gzip = mime "gzip" "application/gzip" [ "application/x-gzip" ] [ "gz" ]
    let TurboStream = mime "turbo_stream" "text/vnd.turbo-stream.html" [] []

    /// `Mime::ALL`: the `*/*` wildcard, only meaningful in negotiation.
    let All: Mime = mime "*/*" "*/*" [] []

    let Registered: Mime[] =
        [| Html; Text; Js; Css; Ics; Csv; Vcf; Vtt; Md; Png; Jpeg; Gif; Bmp; Tiff; Svg; Webp; Mpeg; Mp3; Ogg; M4a; Webm
           Mp4; Otf; Ttf; Woff; Woff2; Xml; Rss; Atom; Yaml; MultipartForm; UrlEncodedForm; Json; Pdf; Zip; Gzip; TurboStream |]

    /// `Mime[ext]` / `Mime::Type.lookup_by_extension`.
    let lookupByExtension (extension: string) : Format voption =
        let mutable found = ValueNone
        let mutable i = 0
        while found.IsNone && i < Registered.Length do
            let m = Registered[i]
            if String.Equals(m.Symbol, extension, StringComparison.Ordinal) || Array.contains extension m.Extensions then
                found <- ValueSome m
            i <- i + 1
        found

    let private lookupExact (string: string) : Format voption =
        let mutable found = ValueNone
        let mutable i = 0
        while found.IsNone && i < Registered.Length do
            let m = Registered[i]
            if String.Equals(m.String, string, StringComparison.Ordinal) || Array.contains string m.Synonyms then
                found <- ValueSome m
            i <- i + 1
        found

    /// The text before the first `;`, without trailing whitespace.
    let private baseOf (string: string) : string =
        let semicolon = string.IndexOf ';'
        (if semicolon < 0 then string else string.Substring(0, semicolon)).TrimEnd()

    /// `Mime::Type::MIME_REGEXP`, loosely: `type/subtype` with name characters, optional params.
    let private validMimeType (string: string) : bool =
        let baseType = baseOf string
        if baseType = "*/*" then
            true
        else
            let isNameByte (c: char) =
                (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || "!#$&-^_.+".IndexOf c >= 0
            let nameOk (s: string) =
                s.Length > 0
                && s.Length <= 127
                && ((s[0] >= 'a' && s[0] <= 'z') || (s[0] >= 'A' && s[0] <= 'Z') || (s[0] >= '0' && s[0] <= '9'))
                && Seq.forall isNameByte s
            match baseType.IndexOf '/' with
            | -1 -> false
            | slash ->
                let kind = baseType.Substring(0, slash)
                let sub = baseType.Substring(slash + 1)
                nameOk kind && (sub = "*" || nameOk sub)

    /// `Mime::Type.lookup`: exact type string or synonym, else the part before `;`.
    /// `ValueNone` is a valid but unregistered type; `Error` is `InvalidMimeType`.
    let lookup (string: string) : Result<Format voption, InvalidMimeType> =
        match lookupExact string with
        | ValueSome mime -> Ok(ValueSome mime)
        | ValueNone ->
            let baseType = baseOf string
            match lookupExact baseType with
            | ValueSome mime -> Ok(ValueSome mime)
            | ValueNone ->
                if baseType = "*/*" then Ok(ValueSome All)
                elif validMimeType string then Ok ValueNone
                else Error(InvalidMimeType string)

    [<AllowNullLiteral>]
    type private AcceptItem(index: int, name: string, q: float) =
        member val Index = index
        member val Name = name with get, set

        /// `(q.to_f * 100).to_i`, kept in a float so that q-values past `i64` still order as Ruby's
        /// Integers do. An infinite one, where Rails raises FloatDomainError, sorts first (or last).
        member val Q = q with get, set

    /// Rust's `u8::is_ascii_whitespace`: space, tab, line feed, form feed and carriage return.
    let private isAsciiWhitespace (c: char) = c = ' ' || c = '\t' || c = '\n' || c = '\012' || c = '\r'

    /// `PARAMETER_SEPARATOR_REGEXP = /;\s*q="?/`: where the first one starts and ends.
    let private findQSeparator (s: string) : struct (int * int) voption =
        let mutable result = ValueNone
        let mutable i = 0
        while result.IsNone && i < s.Length do
            if s[i] = ';' then
                let mutable j = i + 1
                while j < s.Length && isAsciiWhitespace s[j] do
                    j <- j + 1
                if j + 1 < s.Length && s[j] = 'q' && s[j + 1] = '=' then
                    result <- ValueSome(struct (i, (if j + 2 < s.Length && s[j + 2] = '"' then j + 3 else j + 2)))
            i <- i + 1
        result

    /// `String#split` with a separator finder. It drops trailing empty fields, so a `q=` with nothing
    /// after it leaves `q` nil (1.0), not "" (0.0).
    let private rubySplit (s: string) (find: string -> struct (int * int) voption) : string list =
        let fields = ResizeArray<string>()
        let mutable rest = s
        let mutable go = true
        while go do
            match find rest with
            | ValueSome(struct (start, ``end``)) ->
                fields.Add(rest.Substring(0, start))
                rest <- rest.Substring ``end``
            | ValueNone -> go <- false
        fields.Add rest
        while fields.Count > 0 && fields[fields.Count - 1] = "" do
            fields.RemoveAt(fields.Count - 1)
        List.ofSeq fields

    /// `ACCEPT_HEADER_REGEXP = /[^,\s"](?:[^,"]|"[^"]*")*/`
    let private scanAcceptItems (header: string) : string list =
        let items = ResizeArray<string>()
        let mutable i = 0
        while i < header.Length do
            if header[i] = ',' || isAsciiWhitespace header[i] || header[i] = '"' then
                i <- i + 1
            else
                let start = i
                i <- i + 1
                let mutable go = true
                while go && i < header.Length && header[i] <> ',' do
                    if header[i] = '"' then
                        match header.IndexOf('"', i + 1) with
                        | -1 -> go <- false
                        | close -> i <- close + 1
                    else
                        i <- i + 1
                items.Add(header.Substring(start, i - start))
        List.ofSeq items

    /// `TRAILING_STAR_REGEXP = /^(text|application)\/\*/`: every registered type matching `text/`
    /// or `application/`, in registration order.
    let private trailingStar (accept: string) : Format list voption =
        let kind =
            [ "text/*"; "application/*" ] |> List.tryFind (fun p -> accept.StartsWith(p, StringComparison.Ordinal))
        match kind with
        | None -> ValueNone
        | Some kind ->
            let prefix = kind.Substring(0, kind.Length - 1)
            ValueSome(Registered |> Array.filter (fun m -> m.Matches prefix) |> List.ofArray)

    /// `Mime::Type::AcceptList.sort!`'s XML juggling: `text/xml` folds into `application/xml`, which
    /// then yields to any `+xml` type of the same quality listed after it.
    let private sortXml (list: ResizeArray<AcceptItem>) =
        let find (name: string) = list.FindIndex(fun i -> i.Name = name)
        let textXml = find "text/xml"
        let mutable appXml = find "application/xml"
        if textXml >= 0 && appXml >= 0 then
            let mutable textIdx = textXml
            let mutable appIdx = appXml
            list[appIdx].Q <- max list[appIdx].Q list[textIdx].Q
            if appIdx > textIdx then
                let tmp = list[appIdx]
                list[appIdx] <- list[textIdx]
                list[textIdx] <- tmp
                let t = appIdx
                appIdx <- textIdx
                textIdx <- t
            list.RemoveAt textIdx
            appXml <- appIdx
        elif textXml >= 0 then
            list[textXml].Name <- "application/xml"
            appXml <- -1
        else
            ()
        if appXml >= 0 then
            let mutable appIdx = appXml
            let appQ = list[appIdx].Q
            let mutable idx = appIdx
            let mutable go = true
            while go && idx < list.Count do
                if list[idx].Q < appQ then
                    go <- false
                else
                    if list[idx].Name.EndsWith("+xml", StringComparison.Ordinal) then
                        let tmp = list[appIdx]
                        list[appIdx] <- list[idx]
                        list[idx] <- tmp
                        appIdx <- idx
                    idx <- idx + 1

    let private collect (items: Format voption seq) : Format list = items |> Seq.choose ValueOption.toOption |> List.ofSeq

    /// `Mime::Type.parse(accept_header)`, keeping only registered types and `*/*` (the
    /// `formats.select!` that follows in `MimeNegotiation#formats`).
    let parseAccept (header: string) : Result<Format list, InvalidMimeType> =
        if not (header.Contains ',') then
            let header =
                match findQSeparator header with
                | ValueSome(struct (index, _)) -> header.Substring(0, index).Trim()
                | ValueNone -> header
            if String.IsNullOrWhiteSpace header then
                Ok []
            else
                match trailingStar header with
                | ValueSome expanded -> Ok expanded
                | ValueNone -> lookup header |> Result.map (fun m -> collect [ m ])
        else
            let list = ResizeArray<AcceptItem>()
            let mutable index = 0
            for item in scanAcceptItems header do
                // `params, q = header.split(PARAMETER_SEPARATOR_REGEXP)`
                match rubySplit item findQSeparator with
                | [] -> ()
                | parameters :: rest ->
                    let q = List.tryHead rest
                    let parameters = parameters.Trim()
                    if parameters <> "" then
                        let names =
                            match trailingStar parameters with
                            | ValueSome expanded -> expanded |> List.map (fun m -> m.String)
                            | ValueNone -> [ parameters ]
                        for name in names do
                            let q =
                                match q with
                                | Some q -> Campfire.Ruby.Ruby.toF q
                                | None when name = "*/*" -> 0.0
                                | None -> 1.0
                            list.Add(AcceptItem(index, name, Math.Truncate(q * 100.0)))
                            index <- index + 1
            // -0.0 ties with 0.0, as both are Ruby's 0 (`to_f` never gives NaN).
            let sorted =
                list
                |> Seq.sortWith (fun a b ->
                    let byQ = if a.Q > b.Q then -1 elif a.Q < b.Q then 1 else 0
                    if byQ <> 0 then byQ else compare a.Index b.Index)
                |> ResizeArray
            sortXml sorted
            let formats = ResizeArray<Format>()
            let mutable failure = ValueNone
            let mutable i = 0
            while failure.IsNone && i < sorted.Count do
                match lookup sorted[i].Name with
                | Error e -> failure <- ValueSome e
                | Ok(ValueSome mime) -> if not (formats.Contains mime) then formats.Add mime
                | Ok ValueNone -> ()
                i <- i + 1
            match failure with
            | ValueSome e -> Error e
            | ValueNone -> Ok(List.ofSeq formats)

    /// Everything `MimeNegotiation#formats` looks at.
    [<Struct>]
    type NegotiationInput =
        {
            /// `params[:format]` (path extension captured by the router, or `?format=`).
            FormatParam: string | null
            Accept: string | null
            ContentType: string | null
            Path: string
            Xhr: bool
        }

    let emptyInput: NegotiationInput =
        { FormatParam = null
          Accept = null
          ContentType = null
          Path = ""
          Xhr = false }

    /// `request.content_mime_type`.
    let contentMimeType (contentType: string | null) : Result<Format voption, InvalidMimeType> =
        match contentType with
        | null -> Ok ValueNone
        | ct ->
            let comma = ct.IndexOfAny [| ','; ';' |]
            let baseType = (if comma < 0 then ct else ct.Substring(0, comma)).Trim().ToLowerInvariant()
            if baseType = "" then Ok ValueNone else lookup baseType

    /// `BROWSER_LIKE_ACCEPTS = /,\s*\*\/\*|\*\/\*\s*,/`
    let private browserLike (accept: string) : bool =
        let compact = String(accept.ToCharArray() |> Array.filter (fun c -> not (Char.IsWhiteSpace c)))
        compact.Contains(",*/*", StringComparison.Ordinal) || compact.Contains("*/*,", StringComparison.Ordinal)

    let private validAcceptHeader (input: NegotiationInput) : bool =
        let accept = match input.Accept with null -> "" | a -> a
        let present = not (String.IsNullOrWhiteSpace accept)
        (input.Xhr && (present || not (String.IsNullOrEmpty input.ContentType))) || (present && not (browserLike accept))

    let private formatFromPathExtension (path: string) : Format voption =
        match path.LastIndexOf '.' with
        | -1 -> ValueNone
        | dot ->
            let ext = path.Substring(dot + 1)
            let isWordChar (c: char) = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c = '_'
            if ext = "" || not (Seq.forall isWordChar ext) then ValueNone else lookupByExtension ext

    /// `request.formats`.
    let formats (input: NegotiationInput) : Result<Format list, InvalidMimeType> =
        match input.FormatParam with
        | null ->
            if validAcceptHeader input then
                let accept = match input.Accept with null -> "" | a -> a.Trim()
                if accept = "" then
                    contentMimeType input.ContentType |> Result.map (fun m -> collect [ m ])
                else
                    parseAccept accept
            else
                match formatFromPathExtension input.Path with
                | ValueSome format -> Ok [ format ]
                | ValueNone -> Ok [ (if input.Xhr then Js else Html) ]
        | format -> Ok(collect [ lookupByExtension format ])

    /// `request.should_apply_vary_header?`: `!params_readable? && use_accept_header &&
    /// valid_accept_header`, i.e. the format came from the `Accept` header, not a format param.
    let shouldApplyVaryHeader (input: NegotiationInput) : bool =
        isNull input.FormatParam && validAcceptHeader input

    /// `request.negotiate_mime(order)`: the first acceptable format `order` offers.
    let negotiate (formats: Format list) (order: Format list) : Format voption =
        let rec go (rest: Format list) =
            match rest with
            | priority :: rest ->
                if priority = All then List.tryHead order |> ValueOption.ofOption
                elif List.contains priority order then ValueSome priority
                else go rest
            | [] -> if List.contains All order then List.tryHead formats |> ValueOption.ofOption else ValueNone
        go formats
