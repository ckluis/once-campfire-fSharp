// Port of rust/crates/campfire/src/integrations/opengraph/html.rs
//
// The slice of `Nokogiri::HTML(html)` (libxml2's legacy HTML parser, in recovery mode) that `Opengraph::Document`
// reads: every `<meta>` element's attributes.
//
// The body string comes from `Opengraph::Fetch`, tagged UTF-8, so libxml2 decodes it as UTF-8 whatever the page
// declares, taking a byte that isn't valid UTF-8 as Latin-1. Then it tokenizes the way its pre-HTML5 parser does:
// `<script>`/`<style>` hold raw text, comments and `<!...>`/`<?...>` markup are skipped, tag and attribute names are
// lowercased, the first of a repeated attribute wins, and attribute values decode HTML 4 entities only when
// terminated by `;` and numeric references with or without one (an invalid one cuts the value short, as the NUL it
// produces ends libxml2's C string).
//
// Rust steps through bytes; everything the scanner matches is ASCII, so here it steps through UTF-16 units and only
// ever splits the text between characters, as it does there.
namespace Campfire.App.Integrations

open System
open System.Collections.Generic
open System.Text
open System.Text.Unicode

type MetaElement =
    { Attributes: (string * string) list }

    member this.Attr(name: string) : string option =
        this.Attributes |> List.tryFind (fun (n, _) -> n = name) |> Option.map snd

    member this.HasAttr(name: string) : bool = this.Attributes |> List.exists (fun (n, _) -> n = name)

module OpengraphHtml =
    /// libxml2 reading a UTF-8 buffer: valid sequences decode, any other byte is taken as Latin-1.
    let decode (bytes: byte[]) : string =
        if Utf8.IsValid bytes then
            Encoding.UTF8.GetString bytes
        else
            let out = StringBuilder(bytes.Length)
            let mutable i = 0
            while i < bytes.Length do
                let mutable rune = Unchecked.defaultof<Rune>
                let mutable consumed = 0
                match Rune.DecodeFromUtf8(ReadOnlySpan(bytes, i, bytes.Length - i), &rune, &consumed) with
                | Buffers.OperationStatus.Done ->
                    out.Append(rune.ToString()) |> ignore
                    i <- i + consumed
                | _ ->
                    out.Append(char bytes[i]) |> ignore
                    i <- i + 1
            out.ToString()

    /// How many attributes of one tag are kept; the rest are parsed and dropped. Real tags have a handful, and a
    /// page with thousands in one tag gains nothing from them.
    [<Literal>]
    let private MaxAttributes = 256

    let private isBlank (c: int) : bool = c = int ' ' || c = int '\t' || c = int '\n' || c = int '\r'

    let private isAlpha (c: int) : bool = (c >= int 'a' && c <= int 'z') || (c >= int 'A' && c <= int 'Z')

    let private isAlnum (c: int) : bool = isAlpha c || (c >= int '0' && c <= int '9')

    let private lowerAscii (c: char) : char = if c >= 'A' && c <= 'Z' then char (int c + 32) else c

    /// `eq_ignore_ascii_case`: only A-Z fold (`OrdinalIgnoreCase` also folds a few non-ASCII letters).
    let private equalsIgnoreAscii (a: string) (b: string) : bool =
        a.Length = b.Length && Seq.forall2 (fun x y -> lowerAscii x = lowerAscii y) a b

    /// The value of the digit `c` in `radix` (ASCII only), or -1.
    let private digit (c: int) (radix: int) : int =
        let value =
            if c >= int '0' && c <= int '9' then c - int '0'
            elif c >= int 'a' && c <= int 'z' then c - int 'a' + 10
            elif c >= int 'A' && c <= int 'Z' then c - int 'A' + 10
            else 99
        if value < radix then value else -1

    [<Sealed>]
    type private Scanner(text: string) =
        let mutable pos = 0

        member _.Pos
            with get () = pos
            and set value = pos <- value

        member _.Length = text.Length

        /// The unit `ahead` places on, or -1 past the end.
        member _.Peek(ahead: int) : int = if pos + ahead < text.Length then int text[pos + ahead] else -1

        member _.StartsWithIgnoreCase(literal: string) : bool =
            if pos + literal.Length > text.Length then
                false
            else
                let mutable same = true
                let mutable i = 0
                while same && i < literal.Length do
                    if lowerAscii text[pos + i] <> lowerAscii literal[i] then same <- false
                    i <- i + 1
                same

        /// The text from `start` to here, which begins and ends at ASCII units.
        member _.TextFrom(start: int) : string = text.Substring(start, pos - start)

        member this.SkipBlanks() : unit =
            while isBlank (this.Peek 0) do
                pos <- pos + 1

        /// Moves past the next `c` (or to the end).
        member this.SkipPast(c: char) : unit =
            let mutable go = true
            while go && pos < text.Length do
                let next = text[pos]
                pos <- pos + 1
                if next = c then go <- false

        /// `htmlParseHTMLName`: `[A-Za-z_:.][A-Za-z0-9:_.-]*`, lowercased.
        member this.HtmlName() : string option =
            let first = this.Peek 0
            if not (isAlpha first || first = int '_' || first = int ':' || first = int '.') then
                None
            else
                let start = pos
                while (let c = this.Peek 0 in isAlnum c || c = int ':' || c = int '-' || c = int '_' || c = int '.') do
                    pos <- pos + 1
                Some((this.TextFrom start).ToLowerInvariant())

        member this.EndTag() : unit =
            pos <- pos + 2
            if this.HtmlName().IsSome then this.SkipPast '>'

        /// `<!--...-->` (with `<!-->` and `<!--->` closing at once, and `--!>` accepted), `<!DOCTYPE...>`, and any
        /// other `<!...>` skipped as a bogus comment.
        member this.MarkupDeclaration() : unit =
            if this.Peek 2 = int '-' && this.Peek 3 = int '-' then
                pos <- pos + 4
                if this.Peek 0 = int '>' then
                    pos <- pos + 1
                elif this.Peek 0 = int '-' && this.Peek 1 = int '>' then
                    pos <- pos + 2
                else
                    let mutable closed = false
                    while not closed && pos < text.Length do
                        if this.StartsWithIgnoreCase "-->" then
                            pos <- pos + 3
                            closed <- true
                        elif this.StartsWithIgnoreCase "--!>" then
                            pos <- pos + 4
                            closed <- true
                        else
                            pos <- pos + 1
            else
                this.SkipPast '>'

        /// `htmlParseHTMLAttribute`: up to the quote, or (unquoted) a blank or `>`.
        member this.AttributeText(stop: int) : string =
            let endsText (c: int) = c = int '&' || c = stop || (stop = -1 && (c = int '>' || isBlank c))
            let out = StringBuilder()
            let mutable truncated = false
            let mutable go = true
            while go do
                let start = pos
                while (let c = this.Peek 0 in c <> -1 && not (endsText c)) do
                    pos <- pos + 1
                if not truncated then out.Append(text, start, pos - start) |> ignore
                if this.Peek 0 <> int '&' then
                    go <- false
                else
                    let decoded = if this.Peek 1 = int '#' then this.CharRef() else Some(this.EntityRef())
                    match decoded with
                    | Some decoded when not truncated -> out.Append decoded |> ignore
                    | Some _ -> ()
                    | None -> truncated <- true
            out.ToString()

        /// `htmlParseCharRef`: `None` for a value that isn't a valid XML character.
        member this.CharRef() : string option =
            let hex = (let c = this.Peek 2 in c = int 'x' || c = int 'X')
            pos <- pos + (if hex then 3 else 2)
            let radix = if hex then 16 else 10
            let mutable value = 0
            let mutable go = true
            while go do
                let c = this.Peek 0
                if c = int ';' then
                    pos <- pos + 1
                    go <- false
                else
                    let d = if c = -1 then -1 else digit c radix
                    if d < 0 then
                        go <- false
                    else
                        if value < 0x110000 then value <- value * radix + d
                        pos <- pos + 1
            let isChar =
                value = 0x9 || value = 0xA || value = 0xD || (value >= 0x20 && value <= 0xD7FF) || (value >= 0xE000 && value <= 0xFFFD) || (value >= 0x10000 && value <= 0x10FFFF)
            if isChar then Some(Char.ConvertFromUtf32 value) else None

        /// `htmlParseEntityRef`: a known name followed by `;` decodes; anything else stays as written.
        member this.EntityRef() : string =
            pos <- pos + 1
            let start = pos
            if (let c = this.Peek 0 in isAlpha c || c = int '_' || c = int ':') then
                while (let c = this.Peek 0 in isAlnum c || c = int '_' || c = int ':' || c = int '.' || c = int '-') do
                    pos <- pos + 1
            let name = this.TextFrom start
            let index = if name <> "" && this.Peek 0 = int ';' then Array.BinarySearch(OpengraphEntities.Names, name, StringComparer.Ordinal) else -1
            if index >= 0 then
                pos <- pos + 1
                Char.ConvertFromUtf32 OpengraphEntities.CodePoints[index]
            else
                "&" + name

        /// `htmlParseAttValue`
        member this.AttributeValue() : string =
            match this.Peek 0 with
            | c when c = int '"' || c = int '\'' ->
                pos <- pos + 1
                let value = this.AttributeText c
                if this.Peek 0 = c then pos <- pos + 1
                value
            | _ -> this.AttributeText -1

        /// `htmlParseStartTag`: returns the name, the element, and whether it ended with `/>`.
        member this.StartTag() : string * MetaElement * bool =
            pos <- pos + 1
            let name = defaultArg (this.HtmlName()) ""
            let attributes = List<string * string>()
            this.SkipBlanks()
            let mutable go = true
            while go do
                match this.Peek 0 with
                | -1 -> go <- false
                | c when c = int '>' -> go <- false
                | c when c = int '/' && this.Peek 1 = int '>' -> go <- false
                | _ ->
                    match this.HtmlName() with
                    | Some attribute ->
                        this.SkipBlanks()
                        let value =
                            if this.Peek 0 = int '=' then
                                pos <- pos + 1
                                this.SkipBlanks()
                                this.AttributeValue()
                            else
                                ""
                        if attributes.Count < MaxAttributes && not (attributes.Exists(fun (n, _) -> n = attribute)) then
                            attributes.Add(attribute, value)
                    | None ->
                        // Dump the bogus attribute string up to the next blank or the end of the tag
                        let mutable skipping = true
                        while skipping do
                            let c = this.Peek 0
                            if c = -1 || isBlank c || c = int '>' || (c = int '/' && this.Peek 1 = int '>') then skipping <- false
                            else pos <- pos + 1
                    this.SkipBlanks()
            let selfClosing = this.Peek 0 = int '/'
            if selfClosing then pos <- pos + 2
            elif this.Peek 0 = int '>' then pos <- pos + 1
            name, { Attributes = List.ofSeq attributes }, selfClosing

        /// `htmlParseScript`: everything up to `</name` (any case) is text.
        member this.RawText(name: string) : unit =
            let ending = "</" + name
            while pos < text.Length && not (this.StartsWithIgnoreCase ending) do
                pos <- pos + 1

    /// The `<meta>` elements of the document, in document order.
    let metaElements (html: string) : MetaElement list =
        // A NUL ends libxml2's input
        let html =
            match html.IndexOf '\000' with
            | -1 -> html
            | nul -> html.Substring(0, nul)
        let scanner = Scanner html
        let metas = List<MetaElement>()
        while scanner.Peek 0 <> -1 do
            if scanner.Peek 0 <> int '<' then
                scanner.Pos <- scanner.Pos + 1
            else
                match scanner.Peek 1 with
                | c when c = int '/' -> scanner.EndTag()
                | c when c = int '!' -> scanner.MarkupDeclaration()
                | c when c = int '?' -> scanner.SkipPast '>'
                | c when isAlpha c ->
                    let name, element, selfClosing = scanner.StartTag()
                    if name = "meta" then metas.Add element
                    elif (name = "script" || name = "style") && not selfClosing then scanner.RawText name
                | _ -> scanner.Pos <- scanner.Pos + 1
        List.ofSeq metas

    /// `content[/charset\s*=\s*([\w-]+)/i, 1]`
    let private charsetIn (content: string) : string option =
        let isSpace (c: char) = c = ' ' || c = '\t' || c = '\n' || c = '\011' || c = '\012' || c = '\r'
        let isWord (c: char) = Char.IsAsciiLetterOrDigit c || c = '_' || c = '-'
        let mutable result: string option = None
        let mutable at = 0
        while result.IsNone && at < content.Length do
            if not (at + 7 <= content.Length && equalsIgnoreAscii (content.Substring(at, 7)) "charset") then
                at <- at + 1
            else
                let mutable i = at + 7
                while i < content.Length && isSpace content[i] do
                    i <- i + 1
                if i < content.Length && content[i] = '=' then
                    i <- i + 1
                    while i < content.Length && isSpace content[i] do
                        i <- i + 1
                    let start = i
                    while i < content.Length && isWord content[i] do
                        i <- i + 1
                    if i > start then result <- Some(content.Substring(start, i - start))
                if result.IsNone then at <- at + 1
        result

    /// `Nokogiri::HTML4::Document#meta_encoding`: the first `meta[@charset]`, else the charset in the first
    /// `http-equiv="Content-Type"` meta with a `content`.
    let metaEncoding (metas: MetaElement list) : string option =
        match metas |> List.tryFind (fun m -> m.HasAttr "charset") with
        | Some meta -> meta.Attr "charset"
        | None ->
            metas
            |> List.tryFind (fun m ->
                m.HasAttr "content"
                && (match m.Attr "http-equiv" with
                    | Some v -> equalsIgnoreAscii v "content-type"
                    | None -> false))
            |> Option.bind (fun meta -> meta.Attr "content" |> Option.bind charsetIn)
