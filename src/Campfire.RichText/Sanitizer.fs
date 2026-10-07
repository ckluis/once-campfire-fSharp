// Port of rust/crates/richtext/src/sanitizer.rs
//
// `Rails::HTML5::SafeListSanitizer` with a `Rails::HTML::PermitScrubber`, over Loofah's HTML5
// scrubbing helpers (rails-html-sanitizer 1.7.1, loofah 2.25.2).
//
// Every sanitization layer in the pipeline is this one scrubber with a different pair of tag and
// attribute allowlists, so it is written once here rather than expressed through a sanitizer
// library, whose URL, comment, foreign-content and attribute-escaping rules differ from Loofah's.
namespace Campfire.RichText

open System
open System.Collections.Generic
open System.Text
open Campfire.RichText.Html5ever
open Campfire.RichText.Unicode

/// A tag and attribute allowlist, as passed to `sanitize(html, tags:, attributes:)`.
[<Sealed>]
type SafeList(tags: string[], attributes: string[]) =
    let tagSet = HashSet<string>(tags, StringComparer.Ordinal)
    let attributeSet = HashSet<string>(attributes, StringComparer.Ordinal)
    member _.Tags = tags
    member _.Attributes = attributes
    member _.AllowsTag(name: string) = tagSet.Contains name
    member _.AllowsAttribute(name: string) = attributeSet.Contains name

module SafeListData =
    /// `Rails::HTML::Concern::Scrubber::SafeList::DEFAULT_ALLOWED_TAGS`
    let defaultAllowedTags =
        [| "a"; "abbr"; "acronym"; "address"; "b"; "big"; "blockquote"; "br"; "cite"; "code"; "dd"; "del"; "dfn"; "div"; "dl"; "dt"; "em"; "h1"
           "h2"; "h3"; "h4"; "h5"; "h6"; "hr"; "i"; "img"; "ins"; "kbd"; "li"; "mark"; "ol"; "p"; "pre"; "samp"; "small"; "span"; "strong"; "sub"
           "sup"; "time"; "tt"; "ul"; "var" |]

    /// `Rails::HTML::Concern::Scrubber::SafeList::DEFAULT_ALLOWED_ATTRIBUTES` without `name`, which let
    /// a message clobber the page's DOM globals (`<img name="body">` shadows `document.body`). Nothing
    /// Campfire's composer writes has one.
    let defaultAllowedAttributes =
        [| "abbr"; "alt"; "cite"; "class"; "datetime"; "height"; "href"; "lang"; "src"; "title"; "width"; "xml:lang" |]

    /// `ContentFilters::EDITOR_FORMATTING_TAGS` (reference/app/helpers/content_filters.rb)
    let editorFormattingTags = [| "s"; "u"; "mark"; "table"; "thead"; "tbody"; "tfoot"; "tr"; "th"; "td" |]

    /// `ContentFilters::EDITOR_FORMATTING_ATTRIBUTES`
    let editorFormattingAttributes = [| "data-language" |]

    /// `ActionText::Attachment::ATTRIBUTES`
    let attachmentAttributes =
        [| "sgid"; "content-type"; "url"; "href"; "filename"; "filesize"; "width"; "height"; "previewable"; "presentation"; "caption"; "content" |]

    /// `existing` followed by the items of `extra` it doesn't already hold.
    let extendUnique (existing: string[]) (extra: string[]) : string[] =
        let result = ResizeArray<string>(existing)
        for item in extra do
            if not (result.Contains item) then result.Add item
        result.ToArray()

    /// `ContentFilters::SanitizeTags::ALLOWED_TAGS`
    let sanitizeTagsAllowedTags : string[] =
        Array.concat
            [ [| "a"; "abbr"; "acronym"; "address"; "b"; "big"; "blockquote"; "br"; "cite"; "code"; "dd"; "del"; "dfn"; "div"; "dl"; "dt"; "em"
                 "h1"; "h2"; "h3"; "h4"; "h5"; "h6"; "hr"; "i"; "ins"; "kbd"; "li"; "ol"; "p"; "pre"; "samp"; "small"; "span"; "strong"; "sub"
                 "sup"; "time"; "tt"; "ul"; "var" |]
              editorFormattingTags
              [| "action-text-attachment"; "figure"; "figcaption" |] ]

module SafeList =
    open SafeListData

    /// Action View's `sanitize(html)` with no options: the sanitizer's class-level defaults.
    let defaults : SafeList = SafeList(defaultAllowedTags, defaultAllowedAttributes)

    /// `ActionText::ContentHelper.allowed_tags`/`allowed_attributes` as configured at boot: Action
    /// Text's defaults, then Lexxy's additions (lexxy/engine.rb, "lexxy.sanitization"), then
    /// Campfire's (reference/lib/rails_ext/action_text_allowed_tags.rb).
    let actionText : SafeList =
        let tags =
            Array.concat
                [ defaultAllowedTags
                  [| "action-text-attachment"; "figure"; "figcaption" |]
                  [| "video"; "audio"; "source"; "embed"; "table"; "tbody"; "tr"; "th"; "td" |] ]
        let tags = extendUnique tags editorFormattingTags
        let attributes =
            Array.concat
                [ defaultAllowedAttributes
                  attachmentAttributes
                  [| "controls"; "poster"; "data-language"; "style"; "value"; "start" |] ]
        let attributes = extendUnique attributes editorFormattingAttributes
        SafeList(tags, attributes)

    /// `ContentFilters::SanitizeAttributes`: SanitizeTags' tags, Action Text's attributes plus `class`.
    let contentFilter : SafeList =
        SafeList(sanitizeTagsAllowedTags, extendUnique actionText.Attributes [| "class" |])

    /// `MessagesHelper::AUTO_LINK_ALLOWED_TAGS`/`AUTO_LINK_ALLOWED_ATTRIBUTES`.
    let autoLink : SafeList =
        let tags = extendUnique defaultAllowedTags editorFormattingTags
        let attributes = Array.append defaultAllowedAttributes editorFormattingAttributes
        SafeList(tags, attributes)

module internal UriScrub =
    let isWhitespace = Chars.isWhitespace

    let private allowedProtocols =
        HashSet<string>(
            [ "afs"; "aim"; "callto"; "data"; "ed2k"; "fax"; "ftp"; "gopher"; "http"; "https"; "irc"; "line"; "mailto"; "modem"; "news"
              "nntp"; "rsync"; "rtsp"; "sftp"; "sms"; "ssh"; "tag"; "tel"; "telnet"; "urn"; "webcal"; "xmpp" ]
        )

    let private allowedUriDataMediatypes =
        HashSet<string>([ "image/gif"; "image/jpeg"; "image/png"; "text/css"; "text/plain" ])

    /// `Loofah::HTML5::Scrub::CONTROL_CHARACTERS`: /[`\u0000- \u007f\u0080-ā]/
    let isControlCharacter (c: char) : bool = c = '`' || c <= ' ' || c = '\u007f' || (c >= '\u0080' && c <= 'ā')

    let private withoutControls (s: string) : string =
        let mutable any = false
        for c in s do
            if isControlCharacter c then any <- true
        if not any then
            s
        else
            let out = StringBuilder(s.Length)
            for c in s do
                if not (isControlCharacter c) then out.Append c |> ignore
            out.ToString()

    let private isDigitOf (radix: int) (c: char) =
        (c >= '0' && c <= '9') || (radix = 16 && ((c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))

    /// The code point of a numeric reference's digits, when `CGI.unescapeHTML` decodes it: any number
    /// of leading zeros, and below U+10FFFF (cgi's `escape.c` skips `cc >= charlimit`, 0x10ffff for
    /// UTF-8). Surrogates are left alone too, as a Rust string can't hold them.
    let private numericReference (digits: string) (radix: int) : int voption =
        if digits.Length = 0 || not (digits |> Seq.forall (isDigitOf radix)) then
            ValueNone
        else
            let significant = digits.TrimStart '0'
            // Past 7 decimal or 6 hex digits, it's past U+10FFFF anyway.
            if significant.Length > (if radix = 16 then 6 else 7) then
                ValueNone
            else
                let code = if significant.Length = 0 then 0 else Convert.ToInt32(significant, radix)
                if code < 0x10ffff && not (code >= 0xd800 && code <= 0xdfff) then ValueSome code else ValueNone

    /// `CGI.unescapeHTML`: the five named entities plus terminated numeric references.
    let cgiUnescapeHtml (s: string) : string =
        if s.IndexOf '&' < 0 then
            s
        else
            let out = StringBuilder(s.Length)
            let mutable i = 0
            while i < s.Length do
                let amp = s.IndexOf('&', i)
                if amp < 0 then
                    out.Append(s, i, s.Length - i) |> ignore
                    i <- s.Length
                else
                    out.Append(s, i, amp - i) |> ignore
                    let startsWith (name: string) = String.CompareOrdinal(s, amp, name, 0, name.Length) = 0
                    if startsWith "&apos;" then
                        out.Append '\'' |> ignore
                        i <- amp + 6
                    elif startsWith "&amp;" then
                        out.Append '&' |> ignore
                        i <- amp + 5
                    elif startsWith "&quot;" then
                        out.Append '"' |> ignore
                        i <- amp + 6
                    elif startsWith "&gt;" then
                        out.Append '>' |> ignore
                        i <- amp + 4
                    elif startsWith "&lt;" then
                        out.Append '<' |> ignore
                        i <- amp + 4
                    else
                        // "&#" digits ";" or "&#x" digits ";": the first ";" ends the reference, and
                        // anything but digits before it means there is none.
                        let mutable decoded = ValueNone
                        let mutable consumed = 0
                        if amp + 1 < s.Length && s[amp + 1] = '#' then
                            let hex = amp + 2 < s.Length && (s[amp + 2] = 'x' || s[amp + 2] = 'X')
                            let radix = if hex then 16 else 10
                            let digitsStart = amp + (if hex then 3 else 2)
                            let mutable e = digitsStart
                            while e < s.Length && isDigitOf radix s[e] do
                                e <- e + 1
                            if e < s.Length && s[e] = ';' then
                                match numericReference (s.Substring(digitsStart, e - digitsStart)) radix with
                                | ValueSome code ->
                                    decoded <- ValueSome code
                                    consumed <- e + 1 - amp
                                | ValueNone -> ()
                        match decoded with
                        | ValueSome code ->
                            out.Append(Char.ConvertFromUtf32 code) |> ignore
                            i <- amp + consumed
                        | ValueNone ->
                            out.Append '&' |> ignore
                            i <- amp + 1
            out.ToString()

    /// `Loofah::HTML5::Scrub.decode_numeric_character_references`: `&#(x[0-9a-f]+|[0-9]+);?` (case
    /// insensitive), skipping references with too many significant digits or invalid code points.
    let decodeNumericCharacterReferences (s: string) : string =
        let out = StringBuilder(s.Length)
        let mutable i = 0
        let mutable copied = 0
        while i < s.Length do
            if s[i] = '&' && i + 1 < s.Length && s[i + 1] = '#' then
                let start = i + 2
                let hex = start < s.Length && (s[start] = 'x' || s[start] = 'X')
                let digitsStart = if hex then start + 1 else start
                let mutable e = digitsStart
                while e < s.Length && isDigitOf (if hex then 16 else 10) s[e] do
                    e <- e + 1
                if e > digitsStart then
                    let digits = s.Substring(digitsStart, e - digitsStart)
                    let fullEnd = if e < s.Length && s[e] = ';' then e + 1 else e
                    let significant = digits.TrimStart '0'
                    let limit = if hex then 6 else 7
                    let decoded =
                        if significant.Length <= limit then
                            let code = if significant.Length = 0 then 0 else Convert.ToInt32(significant, (if hex then 16 else 10))
                            // `char::from_u32`: not a surrogate, and a scalar value
                            if code <= 0x10ffff && not (code >= 0xd800 && code <= 0xdfff) then ValueSome code else ValueNone
                        else
                            ValueNone
                    out.Append(s, copied, i - copied) |> ignore
                    match decoded with
                    | ValueSome code -> out.Append(Char.ConvertFromUtf32 code) |> ignore
                    | ValueNone -> out.Append(s, i, fullEnd - i) |> ignore
                    i <- fullEnd
                    copied <- i
                else
                    i <- i + 1
            else
                i <- i + 1
        out.Append(s, copied, s.Length - copied) |> ignore
        out.ToString()

    /// `s` is already lowercase, so the separator's case-insensitive parts are plain comparisons.
    let private startsWithAt (s: string) (start: int) (prefix: string) =
        start + prefix.Length <= s.Length && String.CompareOrdinal(s, start, prefix, 0, prefix.Length) = 0

    /// The length of the `PROTOCOL_SEPARATOR` (`/:|(&#0*58)|(&#x0*3a)|(%|&#37;)3A/i`) at `start`.
    let private separatorLen (s: string) (start: int) : int voption =
        if start < s.Length && s[start] = ':' then
            ValueSome 1
        else
            let countZeros (from: int) =
                let mutable z = from
                while z < s.Length && s[z] = '0' do
                    z <- z + 1
                z - from
            let mutable result = ValueNone
            if startsWithAt s start "&#x" then
                let zeros = countZeros (start + 3)
                if startsWithAt s (start + 3 + zeros) "3a" then result <- ValueSome(3 + zeros + 2)
            if result.IsNone && startsWithAt s start "&#" then
                let zeros = countZeros (start + 2)
                if startsWithAt s (start + 2 + zeros) "58" then result <- ValueSome(2 + zeros + 2)
            if result.IsNone && startsWithAt s start "%3a" then result <- ValueSome 3
            if result.IsNone && startsWithAt s start "&#37;3a" then result <- ValueSome 7
            result

    /// Matches `\A[a-z][a-z0-9+\-.]*` followed by `PROTOCOL_SEPARATOR` and returns the scheme.
    let private protocolBeforeSeparator (s: string) : string voption =
        if s.Length = 0 || not (s[0] >= 'a' && s[0] <= 'z') then
            ValueNone
        else
            let mutable e = 1
            while e < s.Length && ((s[e] >= 'a' && s[e] <= 'z') || (s[e] >= '0' && s[e] <= '9') || s[e] = '+' || s[e] = '-' || s[e] = '.') do
                e <- e + 1
            // The class can't contain the start of a separator, so the scheme is the longest run.
            match separatorLen s e with
            | ValueSome _ -> ValueSome(s.Substring(0, e))
            | ValueNone -> ValueNone

    let private isTchar (c: char) =
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || "!#$%&'*+-.^_`|~".IndexOf c >= 0

    /// `Loofah::HTML5::Scrub.data_uri_mediatype`
    let private dataUriMediatype (s: string) : string voption =
        let rest = if s.StartsWith("data:", StringComparison.Ordinal) then s.Substring 5 else s
        match rest.IndexOf ',' with
        | -1 -> ValueNone
        | comma ->
            let metadata = rest.Substring(0, comma)
            let metadata = if metadata.EndsWith(";base64", StringComparison.Ordinal) then metadata.Substring(0, metadata.Length - 7) else metadata
            let first = (metadata.Split ';').[0]
            let mediatype = first.Trim([| ' '; '\t'; '\n'; '\u000b'; '\u000c'; '\r'; '\000' |])
            let valid =
                match mediatype.IndexOf '/' with
                | -1 -> false
                | slash ->
                    let t = mediatype.Substring(0, slash)
                    let sub = mediatype.Substring(slash + 1)
                    t.Length > 0 && sub.Length > 0 && t |> Seq.forall isTchar && sub |> Seq.forall isTchar
            ValueSome(if valid then mediatype else "text/plain")

    /// `Loofah::HTML5::Scrub.allowed_uri?`
    let allowedUri (uri: string) : bool =
        let decoded = decodeNumericCharacterReferences (cgiUnescapeHtml (withoutControls uri))
        let s = withoutControls decoded
        let s = s.Replace("&Tab;", "").Replace("&NewLine;", "")
        let s = s.Replace("&colon;", ":")
        let s = Chars.toLowercase s
        match protocolBeforeSeparator s with
        | ValueNone -> true
        | ValueSome protocol ->
            if not (allowedProtocols.Contains protocol) then false
            elif protocol = "data" then
                match dataUriMediatype s with
                | ValueSome m -> allowedUriDataMediatypes.Contains m
                | ValueNone -> false
            else
                true

module Sanitizer =
    open UriScrub

    /// `ContentFilters::SanitizeTags::ALLOWED_TAGS`
    let sanitizeTagsAllowedTags = SafeListData.sanitizeTagsAllowedTags

    /// `ActionText::Attachment::ATTRIBUTES`
    let attachmentAttributes = SafeListData.attachmentAttributes

    /// `Loofah::HTML5::SafeList::ATTR_VAL_IS_URI`
    let private attrValIsUri (name: string) =
        match name with
        | "action" | "cite" | "href" | "longdesc" | "poster" | "preload" | "src" | "xlink:href" | "xml:base" -> true
        | _ -> false

    /// What `force_correct_attribute_escaping` changes: spaces, double quotes, and the C0 controls
    /// other than tab, newline and carriage return.
    let private isEscaped (c: char) = c = ' ' || c = '"' || (c < ' ' && c <> '\t' && c <> '\n' && c <> '\r')

    let private escapeAttributeValue (value: string) : string =
        let out = StringBuilder(value.Length)
        for c in value do
            match c with
            | ' ' -> out.Append "%20" |> ignore
            | '"' -> out.Append "%22" |> ignore
            | '\t' | '\n' | '\r' -> out.Append c |> ignore
            | c when c < ' ' -> ()
            | c -> out.Append c |> ignore
        out.ToString()

    /// `Loofah::HTML5::Scrub.force_correct_attribute_escaping!` (libxml2 builds only, which CRuby is):
    /// spaces and double quotes in `href`, `action`, `src` and an `a`'s `name` become `%20` and `%22`.
    /// The value is written back through `Nokogiri::XML::Attr#value=`, where libxml2 drops the C0
    /// controls XML 1.0 doesn't allow.
    let private forceCorrectAttributeEscaping (attrs: ResizeArray<Attr>) (isA: bool) =
        for attr in attrs do
            let name = attr.QualifiedName
            let qualifies = name = "href" || name = "action" || name = "src" || (name = "name" && isA)
            if qualifies && attr.Value |> Seq.exists isEscaped then attr.Value <- escapeAttributeValue attr.Value

    /// Lexxy's `ALLOWED_STYLE_PROPERTIES`.
    let private allowedStyleProperties = [| "color"; "background-color" |]

    /// What Rust's `(?i)` folds an ASCII letter with: its other case, and for s and k the long s and
    /// the Kelvin sign, which Unicode case folding makes equal to them.
    let private foldLetter (c: char) : char =
        if c >= 'A' && c <= 'Z' then char (int c + 32)
        elif c = 'ſ' then 's'
        elif c = 'K' then 'k'
        else c

    let private isLetter (c: char) =
        let f = foldLetter c
        f >= 'a' && f <= 'z'

    let private isHexDigit (c: char) =
        let f = foldLetter c
        (f >= '0' && f <= '9') || (f >= 'a' && f <= 'f')

    /// `(?i)\A(?:[a-z]+|#[0-9a-f]{3,8}|var\(\s*--[a-z0-9_-]+\s*\)|(?:rgb|rgba|hsl|hsla)\([0-9a-z.,%\s/+-]*\))\z`:
    /// A color keyword, a hex color, a custom property (`var(--highlight-1)`, as Lexxy's highlights
    /// are), or an `rgb()`/`hsl()` color: nothing that can load a URL, escape, or run an expression.
    let private isPlainColor (value: string) : bool =
        let n = value.Length
        let lowered = String(value.ToCharArray() |> Array.map foldLetter)
        let skipWs (from: int) =
            let mutable i = from
            while i < n && isWhitespace value[i] do
                i <- i + 1
            i
        // [a-z]+
        let keyword = n > 0 && value |> Seq.forall isLetter
        // #[0-9a-f]{3,8}
        let hex = n >= 4 && n <= 9 && value[0] = '#' && value.Substring 1 |> Seq.forall isHexDigit
        // var\(\s*--[a-z0-9_-]+\s*\)
        let var =
            lowered.StartsWith("var(", StringComparison.Ordinal)
            && (let i = skipWs 4
                i + 1 < n
                && value[i] = '-'
                && value[i + 1] = '-'
                && (let nameStart = i + 2
                    let mutable e = nameStart
                    while e < n && (let f = foldLetter value[e] in (f >= 'a' && f <= 'z') || (f >= '0' && f <= '9') || f = '_' || f = '-') do
                        e <- e + 1
                    e > nameStart && (let close = skipWs e in close = n - 1 && value[close] = ')')))
        // (?:rgb|rgba|hsl|hsla)\([0-9a-z.,%\s/+-]*\)
        let functional =
            let prefix =
                if lowered.StartsWith("rgba(", StringComparison.Ordinal) || lowered.StartsWith("hsla(", StringComparison.Ordinal) then 5
                elif lowered.StartsWith("rgb(", StringComparison.Ordinal) || lowered.StartsWith("hsl(", StringComparison.Ordinal) then 4
                else 0
            prefix > 0
            && n > prefix
            && value[n - 1] = ')'
            && (let mutable ok = true
                for i in prefix .. n - 2 do
                    let c = value[i]
                    let f = foldLetter c
                    if not ((f >= '0' && f <= '9') || (f >= 'a' && f <= 'z') || c = '.' || c = ',' || c = '%' || isWhitespace c || c = '/' || c = '+' || c = '-') then
                        ok <- false
                ok)
        keyword || hex || var || functional

    /// Where Loofah's `scrub_css_attribute` runs `style` through its CSS scrubber, this keeps only what
    /// Lexxy writes: highlight colors (`color` and `background-color`, which Lexxy's own paste filter
    /// also limits `style` to) with plain color values. A message's presentation drops `style` in
    /// auto_link anyway, but the HTML body bots and webhooks get (`Presenter::body_html`) keeps it.
    let private scrubStyle (dom: Dom) (node: NodeId) =
        match dom.Attr(node, "style") with
        | ValueNone -> ()
        | ValueSome style ->
            let trimWs (s: string) =
                let mutable a = 0
                let mutable b = s.Length
                while a < b && isWhitespace s[a] do
                    a <- a + 1
                while b > a && isWhitespace s[b - 1] do
                    b <- b - 1
                s.Substring(a, b - a)
            let toAsciiLower (s: string) = String(s.ToCharArray() |> Array.map (fun c -> if c >= 'A' && c <= 'Z' then char (int c + 32) else c))
            let declarations =
                style.Split ';'
                |> Array.filter (fun d -> (trimWs d).Length > 0)
                |> Array.map (fun d ->
                    match d.IndexOf ':' with
                    | -1 -> toAsciiLower (trimWs d), ""
                    | colon -> toAsciiLower (trimWs (d.Substring(0, colon))), trimWs (d.Substring(colon + 1)))
            let allowed (property: string, value: string) = Array.contains property allowedStyleProperties && isPlainColor value
            if declarations |> Array.forall allowed && declarations.Length > 0 then
                ()
            else
                let scrubbed =
                    declarations
                    |> Array.filter allowed
                    |> Array.map (fun (property, value) -> property + ": " + value + ";")
                    |> String.concat ""
                if scrubbed.Length = 0 then dom.RemoveAttr(node, "style") |> ignore else dom.SetAttr(node, "style", scrubbed)

    /// `PermitScrubber#scrub_attributes` with an attribute allowlist. Attributes are visited in order,
    /// and each allowed one re-escapes every URL attribute on the node as it goes, so a later URL is
    /// checked in its re-escaped form (" javascript:" has become "%20javascript:" and passes).
    let private scrubAttributes (dom: Dom) (node: NodeId) (list: SafeList) =
        match dom.Element node with
        | ValueNone -> ()
        | ValueSome element ->
            let isA = element.Name.Local = "a"
            let attrs = element.Attrs
            let mutable i = 0
            while i < attrs.Count do
                let attr = attrs[i]
                let name = attr.QualifiedName
                let scrubbed = not (list.AllowsAttribute name) || (attrValIsUri name && not (allowedUri attr.Value))
                let blankSrc = name = "src" && attr.Value |> Seq.forall isWhitespace
                if scrubbed then
                    attrs.RemoveAt i
                else
                    // A blank src goes, but the escaping still runs for the attributes after it
                    if blankSrc then attrs.RemoveAt i else i <- i + 1
                    forceCorrectAttributeEscaping attrs isA
            scrubStyle dom node

    /// `Rails::HTML::PermitScrubber#scrub`
    let private scrub (dom: Dom) (node: NodeId) (list: SafeList) =
        if not (dom.IsText node) then
            let keep =
                match dom.LocalName node with
                | ValueSome name -> list.AllowsTag name
                | ValueNone -> false
            if not keep then
                // Unwrap HTML elements (and comments, which have no children); drop foreign (SVG,
                // MathML) elements together with their contents, since they carry a namespace.
                let foreign = dom.IsElement node && not (dom.IsHtmlElement node)
                if not foreign then
                    for child in Seq.toArray (dom.Children node) do
                        dom.InsertBefore(node, child)
                dom.Detach node
            else
                scrubAttributes dom node list

    /// `Loofah::Scrubber#traverse_conditionally_bottom_up`: children (as they were before any of them
    /// was scrubbed) first, then the node itself.
    let rec private scrubBottomUp (dom: Dom) (node: NodeId) (list: SafeList) =
        for child in Seq.toArray (dom.Children node) do
            scrubBottomUp dom child list
        scrub dom node list

    let private scrubbed (html: string) (list: SafeList) : Result<struct (Dom * NodeId), ParseError> =
        let dom = Dom()
        match dom.ParseFragment html with
        | Error e -> Error e
        | Ok fragment ->
            for child in Seq.toArray (dom.Children fragment) do
                scrubBottomUp dom child list
            Ok(struct (dom, fragment))

    /// `SafeListSanitizer#sanitize(html, tags:, attributes:)`.
    let sanitize (html: string) (list: SafeList) : Result<string, ParseError> =
        if html.Length = 0 then
            Ok ""
        else
            scrubbed html list |> Result.map (fun (struct (dom, fragment)) -> dom.ToHtml fragment)

    /// `sanitize`, serialized with `<` and `>` escaped in attribute values too, so that the result can
    /// be scanned with regular expressions (auto_link) without mistaking an attribute for text.
    /// Rails serializes them raw; the two are the same DOM.
    let sanitizeWithEscapedAttributeBrackets (html: string) (list: SafeList) : Result<string, ParseError> =
        if html.Length = 0 then
            Ok ""
        else
            scrubbed html list
            |> Result.map (fun (struct (dom, fragment)) -> dom.ToHtmlWithEscapedAttributeBrackets fragment)

    /// `Loofah::HTML5::Scrub.allowed_uri?`
    let allowedUri (uri: string) : bool = UriScrub.allowedUri uri

    /// `CGI.unescapeHTML`
    let cgiUnescapeHtml (s: string) : string = UriScrub.cgiUnescapeHtml s
