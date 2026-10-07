// Port of rust/crates/richtext/src/autolink.rs
//
// rails_autolink 1.1.8's `auto_link(text, html: { target: "_blank" }, sanitize_options: ...)`, as
// `MessagesHelper#message_presentation` calls it. It works on the serialized HTML with regular
// expressions, so the exact serialization from the earlier steps matters.
//
// One deliberate difference closes a stored XSS in rails_autolink: the sanitized HTML is
// serialized with `<` and `>` escaped in attribute values. Nokogiri leaves them raw, so a URL after
// a `>` in a `title` looked like text to `auto_linked?`, and the `<a href="...">` inserted there
// closed the attribute and turned the rest of its value into markup. With them escaped, every `<`
// and `>` in the text is a tag's, so auto_link only ever inserts links between tags.
//
// Rust's regular expressions are matched by hand here: .NET's differ in what `\w`, `\b` and
// `(?i)` mean, and each one below is small enough to say exactly.
module Campfire.RichText.Autolink

open System
open System.Collections.Generic
open System.Text
open Campfire.Ruby
open Campfire.RichText.RubyExt
open Campfire.RichText.Unicode

/// What Rust's `(?i)` folds an ASCII letter with: its other case, and for s and k the long s and the
/// Kelvin sign, which Unicode case folding makes equal to them.
let private fold (c: char) : char =
    if c >= 'A' && c <= 'Z' then char (int c + 32)
    elif c = 'ſ' then 's'
    elif c = 'K' then 'k'
    else c

let private schemes =
    [| "ed2k"; "ftp"; "http"; "https"; "irc"; "mailto"; "news"; "gopher"; "nntp"; "telnet"; "webcal"; "xmpp"; "callto"; "feed"; "svn"
       "urn"; "aim"; "rsync"; "tag"; "ssh"; "sftp"; "rtsp"; "afs"; "file" |]

/// `[^ \t\r\n\x0B\x0C<\u{A0}"]`: what a URL is made of after its start.
let private isUrlChar (c: char) : bool =
    not (c = ' ' || c = '\t' || c = '\r' || c = '\n' || c = '\u000b' || c = '\u000c' || c = '<' || c = ' ' || c = '"')

/// One match of `AUTO_LINK_RE`: where it starts and ends, and whether a scheme started it (the
/// regular expression's capture group).
[<Struct>]
type private UrlMatch = { Start: int; End: int; HasScheme: bool }

let private isAsciiWordClass (c: char) = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c = '_'

/// The match of `AUTO_LINK_RE` that starts at `i`, if any:
/// `(?:(?i:((?:ed2k|ftp|...|file):))//|(?i:www)\.[a-zA-Z0-9_])[^ \t\r\n\x0B\x0C<\u{A0}"]+`.
let private urlMatchAt (text: string) (i: int) : UrlMatch voption =
    let n = text.Length
    let tail (from: int) =
        let mutable e = from
        while e < n && isUrlChar text[e] do
            e <- e + 1
        e
    let mutable result = ValueNone
    // A scheme, then "://"
    let mutable s = 0
    while result.IsNone && s < schemes.Length do
        let name = schemes[s]
        if i + name.Length + 2 < n && text[i + name.Length] = ':' && text[i + name.Length + 1] = '/' && text[i + name.Length + 2] = '/' then
            let mutable same = true
            for k in 0 .. name.Length - 1 do
                if fold text[i + k] <> name[k] then same <- false
            if same then
                let from = i + name.Length + 3
                let e = tail from
                // The tail needs at least one character
                if e > from then result <- ValueSome { Start = i; End = e; HasScheme = true }
        s <- s + 1
    if result.IsNone && i + 5 < n && fold text[i] = 'w' && fold text[i + 1] = 'w' && fold text[i + 2] = 'w' && text[i + 3] = '.' && isAsciiWordClass text[i + 4] then
        let e = tail (i + 5)
        if e > i + 5 then result <- ValueSome { Start = i; End = e; HasScheme = false }
    result

/// `AUTO_EMAIL_RE` without its lookbehind, which is checked separately:
/// `\A[a-zA-Z0-9_.!#$%+-]\.?[a-zA-Z0-9_.!#$%&'*/=?^`{|}~+-]*@[a-zA-Z0-9_-]+(?:\.[a-zA-Z0-9_-]+)+`.
/// Returns where the match, anchored at `start`, ends.
let private emailMatchEnd (text: string) (start: int) : int voption =
    let n = text.Length
    let firstClass (c: char) = isAsciiWordClass c || ".!#$%+-".IndexOf c >= 0
    let localClass (c: char) = isAsciiWordClass c || ".!#$%&'*/=?^`{|}~+-".IndexOf c >= 0
    let labelClass (c: char) = isAsciiWordClass c || c = '-'
    if start >= n || not (firstClass text[start]) then
        ValueNone
    else
        let mutable i = start + 1
        if i < n && text[i] = '.' then i <- i + 1
        while i < n && localClass text[i] do
            i <- i + 1
        if i >= n || text[i] <> '@' then
            ValueNone
        else
            i <- i + 1
            let labelStart = i
            while i < n && labelClass text[i] do
                i <- i + 1
            if i = labelStart then
                ValueNone
            else
                let mutable groups = 0
                let mutable fin = false
                while not fin do
                    if i < n && text[i] = '.' then
                        let mutable e = i + 1
                        while e < n && labelClass text[e] do
                            e <- e + 1
                        if e > i + 1 then
                            i <- e
                            groups <- groups + 1
                        else
                            fin <- true
                    else
                        fin <- true
                if groups > 0 then ValueSome i else ValueNone

let private isEmailLocalChar (c: char) : bool = Char.IsAsciiLetterOrDigit c || "_.!#$%&'*/=?^`{|}~+-".IndexOf c >= 0

/// `AUTO_LINK_CRE[2]`: `/<a\b.*?>/i`, where `.` stops at newlines; anchored, as it is only tried
/// at a `<`. Returns where the match ends.
let private openAnchorEnd (text: string) (i: int) : int voption =
    let n = text.Length
    if i + 2 > n || text[i] <> '<' || fold text[i + 1] <> 'a' || text[i + 1] = 'ſ' then
        ValueNone
    else
        // \b after the "a": the next character mustn't be a word character
        let boundary =
            if i + 2 >= n then
                true
            else
                let cp =
                    if Char.IsHighSurrogate text[i + 2] && i + 3 < n && Char.IsLowSurrogate text[i + 3] then
                        Char.ConvertToUtf32(text[i + 2], text[i + 3])
                    else
                        int text[i + 2]
                not (Chars.isWordCodePoint cp)
        if not boundary then
            ValueNone
        else
            let mutable j = i + 2
            let mutable result = ValueNone
            let mutable fin = false
            while not fin && j < n do
                if text[j] = '>' then
                    result <- ValueSome(j + 1)
                    fin <- true
                elif text[j] = '\n' then
                    fin <- true
                else
                    j <- j + 1
            result

/// What `auto_linked?(left, right)` asks about the text around a match, where `left` is everything
/// before it and `right` everything after. rails_autolink runs its regular expressions over the
/// whole of `left` for every match, which is quadratic (cubic for `rindex`); this indexes the text
/// once so that each question is a binary search. The answers are the same.
[<Sealed>]
type TagIndex(text: string) =
    /// Positions of `<` and of `>`, ascending.
    let lts = ResizeArray<int>()
    let gts = ResizeArray<int>()
    /// The first `\n` that ends a line inside an unclosed tag, after which `AUTO_LINK_CRE[0]`
    /// (`/<[^>]+$/`, whose `$` also matches before a newline) matches every `left`.
    let mutable firstDanglingNewline = -1
    /// `(start, end)` of each `AUTO_LINK_CRE[2]` match: ascending and disjoint.
    let openAnchors = ResizeArray<struct (int * int)>()
    /// Positions of `</a>` (`AUTO_LINK_CRE[3]`, in any case), ascending.
    let closeAnchors = ResizeArray<int>()

    do
        // The first `<` since the last `>`
        let mutable unclosedLt = -1
        for i in 0 .. text.Length - 1 do
            match text[i] with
            | '<' ->
                lts.Add i
                if unclosedLt < 0 then unclosedLt <- i
                if i + 3 < text.Length && text[i + 1] = '/' && (text[i + 2] = 'a' || text[i + 2] = 'A') && text[i + 3] = '>' then
                    closeAnchors.Add i
                let afterPrevious = openAnchors.Count = 0 || (let struct (_, e) = openAnchors[openAnchors.Count - 1] in e <= i)
                if afterPrevious then
                    match openAnchorEnd text i with
                    | ValueSome e -> openAnchors.Add(struct (i, e))
                    | ValueNone -> ()
            | '>' ->
                gts.Add i
                unclosedLt <- -1
            | '\n' when firstDanglingNewline < 0 && unclosedLt >= 0 && unclosedLt + 2 <= i -> firstDanglingNewline <- i
            | _ -> ()

    /// The number of items of `list` that are below `limit`.
    static member private PartitionPoint(list: ResizeArray<int>, pred: int -> bool) : int =
        let mutable lo = 0
        let mutable hi = list.Count
        while lo < hi do
            let mid = (lo + hi) / 2
            if pred list[mid] then lo <- mid + 1 else hi <- mid
        lo

    /// `left =~ /<[^>]+$/`
    member private this.OpenTagAtLineEnd(start: int) : bool =
        if firstDanglingNewline >= 0 && firstDanglingNewline < start then
            true
        else
            // At the end of `left`: a `<` after the last `>`, with at least one character after it
            let gtCount = TagIndex.PartitionPoint(gts, fun p -> p < start)
            let lastGt = if gtCount = 0 then -1 else gts[gtCount - 1]
            let ltIndex = TagIndex.PartitionPoint(lts, fun p -> gtCount > 0 && p <= lastGt)
            ltIndex < lts.Count && lts[ltIndex] + 2 <= start

    /// `right =~ /^[^>]*>/`, which matches whenever `right` has a `>` at all.
    member private this.ClosesTag(finish: int) : bool = gts.Count > 0 && gts[gts.Count - 1] >= finish

    /// `(i = left.rindex(/<a\b.*?>/i)) && left[i..] !~ /<\/a>/i`: the last `<a ...>` wholly in
    /// `left` isn't closed before `left` ends.
    member private this.InsideAnchor(start: int) : bool =
        // The anchors that end at or before `start`
        let mutable lo = 0
        let mutable hi = openAnchors.Count
        while lo < hi do
            let mid = (lo + hi) / 2
            let struct (_, e) = openAnchors[mid]
            if e <= start then lo <- mid + 1 else hi <- mid
        if lo = 0 then
            false
        else
            let struct (_, anchorEnd) = openAnchors[lo - 1]
            let closeIndex = TagIndex.PartitionPoint(closeAnchors, fun p -> p < anchorEnd)
            not (closeIndex < closeAnchors.Count && closeAnchors[closeIndex] + 4 <= start)

    /// `auto_linked?(text[..start], text[end..])`: inside a tag, or inside an unclosed `<a>`.
    member this.AutoLinked(start: int, finish: int) : bool =
        (this.OpenTagAtLineEnd start && this.ClosesTag finish) || this.InsideAnchor start

/// How many of each bracket a URL has, kept up to date as trailing punctuation is stripped (rather
/// than recounted for each character stripped, as rails_autolink does).
[<Sealed>]
type private BracketCounts(href: string) =
    let counts = Array.zeroCreate<int> 6
    static let brackets = [| '['; ']'; '('; ')'; '{'; '}' |]
    do
        for c in href do
            let i = Array.IndexOf(brackets, c)
            if i >= 0 then counts[i] <- counts[i] + 1
    member _.Count(bracket: char) : int =
        let i = Array.IndexOf(brackets, bracket)
        if i >= 0 then counts[i] else 0
    member _.Remove(c: char) =
        let i = Array.IndexOf(brackets, c)
        if i >= 0 then counts[i] <- counts[i] - 1

/// Brackets whose closing half may end a URL when the URL opened it.
let private openingBracket (closing: char) : char voption =
    match closing with
    | ']' -> ValueSome '['
    | ')' -> ValueSome '('
    | '}' -> ValueSome '{'
    | _ -> ValueNone

let private autoLinkUrls (text: string) : Result<string, ParseError> =
    let out = StringBuilder(text.Length)
    let mutable last = 0
    let tags = TagIndex text
    let mutable error = ValueNone
    let mutable i = 0
    while error.IsNone && i < text.Length do
        match urlMatchAt text i with
        | ValueNone -> i <- i + 1
        | ValueSome m ->
            out.Append(text, last, m.Start - last) |> ignore
            last <- m.End
            i <- m.End
            let mutable href = text.Substring(m.Start, m.End - m.Start)
            if tags.AutoLinked(m.Start, m.End) then
                out.Append href |> ignore
            else
                let punctuation = ResizeArray<string>()
                let brackets = BracketCounts href
                let mutable stripping = true
                while stripping do
                    let struct (cp, startIndex) = Chars.lastCodePoint href
                    if cp < 0 || (Chars.isWordCodePoint cp || cp = int '/' || cp = int '-' || cp = int '=' || cp = int ';') then
                        stripping <- false
                    else
                        let c = href.Substring startIndex
                        href <- href.Substring(0, startIndex)
                        punctuation.Add c
                        if c.Length = 1 then
                            brackets.Remove c[0]
                            match openingBracket c[0] with
                            | ValueSome opening when brackets.Count opening > brackets.Count c[0] ->
                                href <- href + c
                                punctuation.RemoveAt(punctuation.Count - 1)
                                stripping <- false
                            | _ -> ()
                let mutable trailingGt = ""
                if href.EndsWith("&gt;", StringComparison.Ordinal) then
                    href <- href.Substring(0, href.Length - 4)
                    trailingGt <- "&gt;"
                let linkText = href
                if not m.HasScheme then href <- "http://" + href
                match Sanitizer.sanitize linkText SafeList.defaults, Sanitizer.sanitize href SafeList.defaults with
                | Ok linkText, Ok href ->
                    // content_tag(:a, link_text, attrs, false): nothing escaped but double quotes in attributes
                    out.Append("<a target=\"_blank\" href=\"").Append(href.Replace("\"", "&quot;")).Append("\">").Append(linkText).Append("</a>") |> ignore
                    // SafeBuffer#+ escapes the (unsafe) punctuation string
                    let trailing = String.Join("", punctuation |> Seq.rev)
                    out.Append(Erb.htmlEscape trailing).Append(trailingGt) |> ignore
                | Error e, _
                | _, Error e -> error <- ValueSome e
    match error with
    | ValueSome e -> Error e
    | ValueNone ->
        out.Append(text, last, text.Length - last) |> ignore
        Ok(out.ToString())

let private autoLinkEmailAddresses (text: string) : Result<string, ParseError> =
    let out = StringBuilder(text.Length)
    let mutable copied = 0
    let mutable position = 0
    let tags = TagIndex text
    let mutable error = ValueNone
    while error.IsNone && position < text.Length do
        let precededByLocalChar = position > 0 && isEmailLocalChar text[position - 1]
        let found = if precededByLocalChar then ValueNone else emailMatchEnd text position
        match found with
        | ValueNone -> position <- position + (if Char.IsHighSurrogate text[position] && position + 1 < text.Length then 2 else 1)
        | ValueSome finish ->
            let start = position
            let email = text.Substring(start, finish - start)
            out.Append(text, copied, start - copied) |> ignore
            if tags.AutoLinked(start, finish) then
                out.Append email |> ignore
            else
                match Sanitizer.sanitize email SafeList.defaults with
                | Error e -> error <- ValueSome e
                | Ok sanitized ->
                    // display_text is only sanitized (and so marked safe) when sanitizing changed the address
                    let display = if sanitized = email then Erb.htmlEscape email else sanitized
                    let href = "mailto:" + (Campfire.Ruby.Uri.urlEncode sanitized).Replace("%40", "@")
                    out.Append("<a target=\"_blank\" href=\"").Append(Erb.htmlEscape href).Append("\">").Append(display).Append("</a>") |> ignore
            copied <- finish
            position <- Math.Max(finish, position + 1)
    match error with
    | ValueSome e -> Error e
    | ValueNone ->
        out.Append(text, copied, text.Length - copied) |> ignore
        Ok(out.ToString())

/// `auto_link(html, html: { target: "_blank" }, sanitize_options: { tags:, attributes: })`
let autoLink (text: string) (sanitizeOptions: SafeList) : Result<string, ParseError> =
    if isBlank text then
        Ok ""
    else
        match Sanitizer.sanitizeWithEscapedAttributeBrackets text sanitizeOptions with
        | Error e -> Error e
        | Ok sanitized ->
            match autoLinkUrls sanitized with
            | Error e -> Error e
            | Ok linked -> autoLinkEmailAddresses linked
