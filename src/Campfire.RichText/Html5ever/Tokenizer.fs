// Port of rust/crates/richtext/vendor/html5ever/src/tokenizer/{mod,char_ref/mod,states,interface}.rs
// and markup5ever's buffer_queue.rs.
//
// Differences from the Rust, none visible in the tokens the tree builder gets:
//  - The whole body is one buffer, so "not enough input to decide" is the end of the input, and
//    `eat` answers false there, as it does once `end()` has run in Rust.
//  - Parse errors are bare `ProcessParseError` calls (the tree builder only uses them to forget
//    that a newline after <pre> is to be skipped); no messages, no line numbers, no profiling.
//  - Doctype tokens carry nothing (a fragment parse never reads them).
//  - '\n' is not in the character sets `pop_except_from` stops at, as in Rust's SIMD data-state
//    path: it only existed there to count lines.
//  - Each `step` is one pass of a state's loop, so `Run` re-enters it where Rust loops.
namespace Campfire.RichText.Html5ever

open System
open System.Collections.Generic
open System.Text

type ScriptEscape =
    | Escaped
    | DoubleEscaped

type DoctypeIdKind =
    | PublicId
    | SystemId

type AttrValueKind =
    | Unquoted
    | SingleQuoted
    | DoubleQuoted

/// `states::RawKind`, with `ScriptDataEscaped(kind)` flattened into two cases.
type RawKind =
    | Rcdata
    | Rawtext
    | ScriptData
    | ScriptDataEscapedKind
    | ScriptDataDoubleEscapedKind

type State =
    | Data
    | Plaintext
    | TagOpen
    | EndTagOpen
    | TagName
    | RawData of RawKind
    | RawLessThanSign of RawKind
    | RawEndTagOpen of RawKind
    | RawEndTagName of RawKind
    | ScriptDataEscapeStart of ScriptEscape
    | ScriptDataEscapeStartDash
    | ScriptDataEscapedDash of ScriptEscape
    | ScriptDataEscapedDashDash of ScriptEscape
    | ScriptDataDoubleEscapeEnd
    | BeforeAttributeName
    | AttributeName
    | AfterAttributeName
    | BeforeAttributeValue
    | AttributeValue of AttrValueKind
    | AfterAttributeValueQuoted
    | SelfClosingStartTag
    | BogusComment
    | MarkupDeclarationOpen
    | CommentStart
    | CommentStartDash
    | Comment
    | CommentLessThanSign
    | CommentLessThanSignBang
    | CommentLessThanSignBangDash
    | CommentLessThanSignBangDashDash
    | CommentEndDash
    | CommentEnd
    | CommentEndBang
    | Doctype
    | BeforeDoctypeName
    | DoctypeName
    | AfterDoctypeName
    | AfterDoctypeKeyword of DoctypeIdKind
    | BeforeDoctypeIdentifier of DoctypeIdKind
    | DoctypeIdentifierDoubleQuoted of DoctypeIdKind
    | DoctypeIdentifierSingleQuoted of DoctypeIdKind
    | AfterDoctypeIdentifier of DoctypeIdKind
    | BetweenDoctypePublicAndSystemIdentifiers
    | BogusDoctype
    | CdataSection
    | CdataSectionBracket
    | CdataSectionEnd

type TokenSinkResult =
    | Continue
    | Script of int
    | Plaintext
    | RawDataState of RawKind

/// What the tokenizer feeds: the tree builder, through the depth limit.
[<AbstractClass>]
type TokenSink() =
    abstract ProcessTag: Tag -> TokenSinkResult
    abstract ProcessComment: string -> unit
    abstract ProcessChars: string -> unit
    abstract ProcessNull: unit -> unit
    abstract ProcessEof: unit -> unit
    abstract ProcessDoctype: unit -> unit
    abstract ProcessParseError: unit -> unit
    abstract End: unit -> unit
    /// Used in the markup declaration open state: with it false, CDATA is a bogus comment.
    abstract AdjustedCurrentNodePresentButNotInHtmlNamespace: unit -> bool

[<Struct>]
type SetResult =
    | NoInput
    | FromSet of c: char
    | NotFromSet of s: string

/// markup5ever's `BufferQueue`, over one string and the chunks pushed back in front of it.
[<Sealed; AllowNullLiteral>]
type Input(text: string) =
    let mutable main = text
    let mutable mainPos = 0
    // Pushed-back chunks, the last one first in line.
    let pushed = Stack<struct (string * int ref)>()

    member _.IsEmpty = pushed.Count = 0 && mainPos >= main.Length

    /// Throws away everything, as the depth limit does to stop the tokenizer.
    member _.Clear() =
        pushed.Clear()
        mainPos <- main.Length

    member _.PushFront(s: string) =
        if s.Length > 0 then pushed.Push(struct (s, ref 0))

    /// The next character without consuming it, or -1.
    member _.Peek() : int =
        if pushed.Count > 0 then
            let struct (s, pos) = pushed.Peek()
            int s[pos.Value]
        elif mainPos < main.Length then
            int main[mainPos]
        else
            -1

    member _.Next() : int =
        if pushed.Count > 0 then
            let struct (s, pos) = pushed.Peek()
            let c = s[pos.Value]
            pos.Value <- pos.Value + 1
            if pos.Value >= s.Length then pushed.Pop() |> ignore
            int c
        elif mainPos < main.Length then
            let c = main[mainPos]
            mainPos <- mainPos + 1
            int c
        else
            -1

    /// A character from `set` (code points below 64), or the longest run in front of the queue
    /// that holds none.
    member _.PopExceptFrom(set: uint64) : SetResult =
        let inline member' (c: char) = c < '@' && ((set >>> int c) &&& 1UL) <> 0UL
        if pushed.Count > 0 then
            let struct (s, pos) = pushed.Peek()
            let start = pos.Value
            let mutable n = start
            while n < s.Length && not (member' s[n]) do
                n <- n + 1
            if n > start then
                pos.Value <- n
                if n >= s.Length then pushed.Pop() |> ignore
                NotFromSet(s.Substring(start, n - start))
            else
                pos.Value <- start + 1
                if pos.Value >= s.Length then pushed.Pop() |> ignore
                FromSet s[start]
        elif mainPos < main.Length then
            let start = mainPos
            let mutable n = start
            while n < main.Length && not (member' main[n]) do
                n <- n + 1
            if n > start then
                mainPos <- n
                NotFromSet(main.Substring(start, n - start))
            else
                mainPos <- start + 1
                FromSet main[start]
        else
            NoInput

    /// Consumes `pat` if the queue starts with it (comparing as `eq` says), and says whether it did.
    member this.Eat(pat: string, ignoreCase: bool) : bool =
        // Look at the queue front to back without consuming.
        let chunks = pushed.ToArray()
        let mutable chunk = 0
        let mutable pos = if chunks.Length > 0 then (let struct (_, p) = chunks[0] in p.Value) else mainPos
        let mutable matched = 0
        let mutable ok = true
        while ok && matched < pat.Length do
            // Advance to a chunk that has a character left.
            let mutable c = -1
            if chunk < chunks.Length then
                let struct (s, _) = chunks[chunk]
                c <- int s[pos]
                pos <- pos + 1
                if pos >= s.Length then
                    chunk <- chunk + 1
                    pos <- (if chunk < chunks.Length then (let struct (_, p) = chunks[chunk] in p.Value) else mainPos)
            elif pos < main.Length then
                c <- int main[pos]
                pos <- pos + 1
            if c < 0 then
                ok <- false
            else
                let p = int pat[matched]
                let same =
                    if ignoreCase then
                        (if c >= int 'A' && c <= int 'Z' then c + 32 else c) = (if p >= int 'A' && p <= int 'Z' then p + 32 else p)
                    else
                        c = p
                if same then matched <- matched + 1 else ok <- false
        if ok then
            for _ in 1 .. pat.Length do
                this.Next() |> ignore
        ok

/// `char_ref::CharRefTokenizer`'s states.
type private CharRefState =
    | Begin
    | Octothorpe
    | Numeric of uint32
    | NumericSemicolon
    | Named
    | BogusName

type Proc =
    | Continue = 0
    | Suspend = 1
    | Script = 2

module internal TokenizerChars =
    let inline isAsciiAlphanumeric (c: char) = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')

    /// `lower_ascii_letter`: the lowercase letter for an ASCII letter, else -1. (Not `inline`, which the
    /// F# compiler can't expand in the test assembly; the JIT inlines it.)
    [<System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)>]
    let lowerAsciiLetter (c: char) : int =
        if c >= 'a' && c <= 'z' then int c
        elif c >= 'A' && c <= 'Z' then int c + 32
        else -1

    let inline toAsciiLowercase (c: char) : char = if c >= 'A' && c <= 'Z' then char (int c + 32) else c

    let inline isSpace (c: char) = c = '\t' || c = '\n' || c = '\012' || c = ' '

    /// `small_char_set!`
    let charSet (chars: char list) : uint64 = chars |> List.fold (fun bits c -> bits ||| (1UL <<< int c)) 0UL

    let dataSet = charSet [ '\r'; '\000'; '&'; '<' ]
    let rawtextSet = charSet [ '\r'; '\000'; '<' ]
    let escapedSet = charSet [ '\r'; '\000'; '-'; '<' ]
    let plaintextSet = charSet [ '\r'; '\000' ]
    let doubleQuotedSet = charSet [ '\r'; '"'; '&'; '\000' ]
    let singleQuotedSet = charSet [ '\r'; '\''; '&'; '\000' ]
    let unquotedSet = charSet [ '\r'; '\t'; '\n'; '\012'; ' '; '&'; '>'; '\000' ]

    /// One-character strings for ASCII, so a character token doesn't allocate.
    let charStrings : string[] = Array.init 128 (fun i -> string (char i))

    let charString (c: char) : string = if int c < 128 then charStrings[int c] else string c

    let c1Replacements : int[] =
        [| 0x20ac; -1; 0x201a; 0x0192; 0x201e; 0x2026; 0x2020; 0x2021; 0x02c6; 0x2030; 0x0160; 0x2039; 0x0152; -1; 0x017d; -1
           -1; 0x2018; 0x2019; 0x201c; 0x201d; 0x2022; 0x2013; 0x2014; 0x02dc; 0x2122; 0x0161; 0x203a; 0x0153; -1; 0x017e; 0x0178 |]

open TokenizerChars

[<Sealed; AllowNullLiteral>]
type Tokenizer(sink: TokenSink, initialState: State, maxAttributes: int) =
    let mutable state = initialState
    let mutable atEof = false
    let mutable charRef: CharRefTokenizer = null
    let mutable currentChar = '\000'
    let mutable reconsume = false
    let mutable ignoreLf = false
    let mutable currentTagKind = StartTag
    let currentTagName = StringBuilder()
    let mutable currentTagSelfClosing = false
    let mutable currentTagAttrs = ResizeArray<TagAttribute>()
    let currentAttrName = StringBuilder()
    let currentAttrValue = StringBuilder()
    let currentComment = StringBuilder()
    let mutable lastStartTagName: string | null = null
    let tempBuf = StringBuilder()
    let mutable tooManyAttributes = false

    /// Has a tag started more attributes than `max_attributes`?
    member _.TooManyAttributes = tooManyAttributes

    member _.State
        with get () = state
        and set v = state <- v

    member _.Sink = sink

    // --- Input -------------------------------------------------------------------------------

    /// `get_preprocessed_char`: a carriage return is a line feed, and a line feed right after one is
    /// skipped. Returns -1 when the input ends in the skipped one.
    member private _.GetPreprocessedChar(c0: char, input: Input) : int =
        let mutable c = int c0
        let mutable ended = false
        if ignoreLf then
            ignoreLf <- false
            if c = int '\n' then
                c <- input.Next()
                if c < 0 then ended <- true
        if ended then
            -1
        else
            if c = int '\r' then
                ignoreLf <- true
                c <- int '\n'
            currentChar <- char c
            c

    /// Gets the next input character, or -1.
    member this.GetChar(input: Input) : int =
        if reconsume then
            reconsume <- false
            int currentChar
        else
            match input.Next() with
            | -1 -> -1
            | c -> this.GetPreprocessedChar(char c, input)

    member this.Peek(input: Input) : int = if reconsume then int currentChar else input.Peek()

    member this.DiscardChar(input: Input) =
        // peek() deals in raw characters, so a discard is one raw character too.
        if reconsume then reconsume <- false else input.Next() |> ignore

    member private this.PopExceptFrom(input: Input, set: uint64) : SetResult =
        if reconsume || ignoreLf then
            match this.GetChar input with
            | -1 -> NoInput
            | c -> FromSet(char c)
        else
            match input.PopExceptFrom set with
            | FromSet c ->
                match this.GetPreprocessedChar(c, input) with
                | -1 -> NoInput
                | c -> FromSet(char c)
            | other -> other

    member private this.Eat(input: Input, pat: string, ignoreCase: bool) : bool =
        if ignoreLf then
            ignoreLf <- false
            if this.Peek input = int '\n' then this.DiscardChar input
        input.Eat(pat, ignoreCase)

    // --- Emitting ----------------------------------------------------------------------------

    member this.EmitError() = sink.ProcessParseError()

    member private this.EmitChar(c: char) =
        if c = '\000' then sink.ProcessNull() else sink.ProcessChars(charString c)

    member private this.EmitChars(s: string) = sink.ProcessChars s

    member private this.EmitTempBuf() =
        let s = tempBuf.ToString()
        tempBuf.Clear() |> ignore
        this.EmitChars s

    member private this.EmitCurrentComment() =
        let s = currentComment.ToString()
        currentComment.Clear() |> ignore
        sink.ProcessComment s

    member private this.AtMaxAttributes = maxAttributes >= 0 && currentTagAttrs.Count >= maxAttributes

    member private this.FinishAttribute() =
        if currentAttrName.Length > 0 then
            // Drop attributes past the limit without scanning for a duplicate, as Gumbo does.
            if this.AtMaxAttributes then
                currentAttrName.Clear() |> ignore
                currentAttrValue.Clear() |> ignore
            else
                let name = currentAttrName.ToString()
                let mutable dup = false
                for a in currentTagAttrs do
                    if a.Name.Local = name then dup <- true
                if dup then
                    this.EmitError()
                else
                    currentTagAttrs.Add { Name = QualName.unprefixed Ns.Empty name; Value = currentAttrValue.ToString() }
                currentAttrName.Clear() |> ignore
                currentAttrValue.Clear() |> ignore

    member private this.CreateAttribute(c: char) =
        this.FinishAttribute()
        if this.AtMaxAttributes then tooManyAttributes <- true
        currentAttrName.Append c |> ignore

    member private this.DiscardTag() =
        currentTagName.Clear() |> ignore
        currentTagSelfClosing <- false
        currentTagAttrs <- ResizeArray<TagAttribute>()

    member private this.CreateTag(kind: TagKind, c: char) =
        this.DiscardTag()
        currentTagName.Append c |> ignore
        currentTagKind <- kind

    member private this.HaveAppropriateEndTag() =
        match lastStartTagName with
        | null -> false
        | last -> currentTagKind = EndTag && currentTagName.Length = last.Length && currentTagName.ToString() = last

    member private this.EmitCurrentTag() : Proc =
        this.FinishAttribute()
        let name = currentTagName.ToString()
        currentTagName.Clear() |> ignore
        match currentTagKind with
        | StartTag -> lastStartTagName <- name
        | EndTag ->
            if currentTagAttrs.Count > 0 then this.EmitError()
            if currentTagSelfClosing then this.EmitError()
        let tag =
            { Kind = currentTagKind
              Name = name
              SelfClosing = currentTagSelfClosing
              Attrs = currentTagAttrs.ToArray() }
        currentTagAttrs <- ResizeArray<TagAttribute>()
        match sink.ProcessTag tag with
        | Continue -> Proc.Continue
        | Plaintext ->
            state <- State.Plaintext
            Proc.Continue
        | Script _ ->
            state <- Data
            Proc.Script
        | RawDataState kind ->
            state <- RawData kind
            Proc.Continue

    member private this.ConsumeCharRef() =
        charRef <- CharRefTokenizer(match state with AttributeValue _ -> true | _ -> false)

    member private this.To(s: State) : Proc =
        state <- s
        Proc.Continue

    member private this.Reconsume(s: State) : Proc =
        reconsume <- true
        state <- s
        Proc.Continue

    member private this.EmitTag(s: State) : Proc =
        state <- s
        this.EmitCurrentTag()

    // --- Character references ---------------------------------------------------------------

    member internal this.ProcessCharRef(result: string) =
        let chars = if result.Length = 0 then "&" else result
        let mutable i = 0
        while i < chars.Length do
            let n = if Char.IsHighSurrogate chars[i] && i + 1 < chars.Length then 2 else 1
            let piece = chars.Substring(i, n)
            match state with
            | Data
            | RawData Rcdata -> sink.ProcessChars piece
            | AttributeValue _ -> currentAttrValue.Append piece |> ignore
            | s -> failwithf "state %A should not be reachable in process_char_ref" s
            i <- i + n

    member internal _.ClearIgnoreLf() = ignoreLf <- false

    member private this.StepCharRefTokenizer(input: Input) : Proc =
        let tok = charRef
        charRef <- null
        match tok.Step(this, input) with
        | CharRefStatus.Done ->
            this.ProcessCharRef(tok.Result)
            Proc.Continue
        | CharRefStatus.Stuck ->
            charRef <- tok
            Proc.Suspend
        | _ ->
            charRef <- tok
            Proc.Continue

    // --- The state machine -------------------------------------------------------------------

    /// Runs the state machine for as long as it can.
    member this.Run(input: Input) : Proc =
        let mutable result = Proc.Continue
        while result = Proc.Continue do
            result <- this.Step input
        result

    /// Feeds the input to the tokenizer, and keeps going after each `</script>`.
    member this.Feed(input: Input) =
        if not input.IsEmpty then
            while this.Run input = Proc.Script do
                ()

    member private this.Step(input: Input) : Proc =
        if not (isNull charRef) then
            this.StepCharRefTokenizer input
        else
            match state with
            // data
            | Data ->
                match this.PopExceptFrom(input, dataSet) with
                | NoInput -> Proc.Suspend
                | FromSet '\000' ->
                    this.EmitError()
                    this.EmitChar '\000'
                    Proc.Continue
                | FromSet '&' ->
                    this.ConsumeCharRef()
                    Proc.Continue
                | FromSet '<' -> this.To TagOpen
                | FromSet c ->
                    this.EmitChar c
                    Proc.Continue
                | NotFromSet b ->
                    this.EmitChars b
                    Proc.Continue
            // rcdata
            | RawData Rcdata ->
                match this.PopExceptFrom(input, dataSet) with
                | NoInput -> Proc.Suspend
                | FromSet '\000' ->
                    this.EmitError()
                    this.EmitChar '�'
                    Proc.Continue
                | FromSet '&' ->
                    this.ConsumeCharRef()
                    Proc.Continue
                | FromSet '<' -> this.To(RawLessThanSign Rcdata)
                | FromSet c ->
                    this.EmitChar c
                    Proc.Continue
                | NotFromSet b ->
                    this.EmitChars b
                    Proc.Continue
            // rawtext, script data
            | RawData((Rawtext | ScriptData) as kind) ->
                match this.PopExceptFrom(input, rawtextSet) with
                | NoInput -> Proc.Suspend
                | FromSet '\000' ->
                    this.EmitError()
                    this.EmitChar '�'
                    Proc.Continue
                | FromSet '<' -> this.To(RawLessThanSign kind)
                | FromSet c ->
                    this.EmitChar c
                    Proc.Continue
                | NotFromSet b ->
                    this.EmitChars b
                    Proc.Continue
            // script data escaped
            | RawData ScriptDataEscapedKind ->
                match this.PopExceptFrom(input, escapedSet) with
                | NoInput -> Proc.Suspend
                | FromSet '\000' ->
                    this.EmitError()
                    this.EmitChar '�'
                    Proc.Continue
                | FromSet '-' ->
                    this.EmitChar '-'
                    this.To(ScriptDataEscapedDash Escaped)
                | FromSet '<' -> this.To(RawLessThanSign ScriptDataEscapedKind)
                | FromSet c ->
                    this.EmitChar c
                    Proc.Continue
                | NotFromSet b ->
                    this.EmitChars b
                    Proc.Continue
            // script data double escaped
            | RawData ScriptDataDoubleEscapedKind ->
                match this.PopExceptFrom(input, escapedSet) with
                | NoInput -> Proc.Suspend
                | FromSet '\000' ->
                    this.EmitError()
                    this.EmitChar '�'
                    Proc.Continue
                | FromSet '-' ->
                    this.EmitChar '-'
                    this.To(ScriptDataEscapedDash DoubleEscaped)
                | FromSet '<' ->
                    this.EmitChar '<'
                    this.To(RawLessThanSign ScriptDataDoubleEscapedKind)
                | FromSet c ->
                    this.EmitChar c
                    Proc.Continue
                | NotFromSet b ->
                    this.EmitChars b
                    Proc.Continue
            // plaintext
            | State.Plaintext ->
                match this.PopExceptFrom(input, plaintextSet) with
                | NoInput -> Proc.Suspend
                | FromSet '\000' ->
                    this.EmitError()
                    this.EmitChar '�'
                    Proc.Continue
                | FromSet c ->
                    this.EmitChar c
                    Proc.Continue
                | NotFromSet b ->
                    this.EmitChars b
                    Proc.Continue
            // tag open
            | TagOpen ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    let c = char ci
                    match c with
                    | '!' -> this.To MarkupDeclarationOpen
                    | '/' -> this.To EndTagOpen
                    | '?' ->
                        this.EmitError()
                        currentComment.Clear() |> ignore
                        this.Reconsume BogusComment
                    | c ->
                        match lowerAsciiLetter c with
                        | -1 ->
                            this.EmitError()
                            this.EmitChar '<'
                            this.Reconsume Data
                        | cl ->
                            this.CreateTag(StartTag, char cl)
                            this.To TagName
            // end tag open
            | EndTagOpen ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    let c = char ci
                    match c with
                    | '>' ->
                        this.EmitError()
                        this.To Data
                    | c ->
                        match lowerAsciiLetter c with
                        | -1 ->
                            this.EmitError()
                            currentComment.Clear() |> ignore
                            this.Reconsume BogusComment
                        | cl ->
                            this.CreateTag(EndTag, char cl)
                            this.To TagName
            // tag name
            | TagName ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    let c = char ci
                    if isSpace c then this.To BeforeAttributeName
                    else
                        match c with
                        | '/' -> this.To SelfClosingStartTag
                        | '>' -> this.EmitTag Data
                        | '\000' ->
                            this.EmitError()
                            currentTagName.Append '�' |> ignore
                            Proc.Continue
                        | c ->
                            currentTagName.Append(toAsciiLowercase c) |> ignore
                            Proc.Continue
            // script data escaped less-than sign
            | RawLessThanSign ScriptDataEscapedKind ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    let c = char ci
                    match c with
                    | '/' ->
                        tempBuf.Clear() |> ignore
                        this.To(RawEndTagOpen ScriptDataEscapedKind)
                    | c ->
                        match lowerAsciiLetter c with
                        | -1 ->
                            this.EmitChar '<'
                            this.Reconsume(RawData ScriptDataEscapedKind)
                        | cl ->
                            tempBuf.Clear().Append(char cl) |> ignore
                            this.EmitChar '<'
                            this.EmitChar c
                            this.To(ScriptDataEscapeStart DoubleEscaped)
            // script data double escaped less-than sign
            | RawLessThanSign ScriptDataDoubleEscapedKind ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '/' ->
                        tempBuf.Clear() |> ignore
                        this.EmitChar '/'
                        this.To ScriptDataDoubleEscapeEnd
                    | _ -> this.Reconsume(RawData ScriptDataDoubleEscapedKind)
            // rcdata, rawtext and script data less-than sign
            | RawLessThanSign kind ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '/' ->
                        tempBuf.Clear() |> ignore
                        this.To(RawEndTagOpen kind)
                    | '!' when kind = ScriptData ->
                        this.EmitChar '<'
                        this.EmitChar '!'
                        this.To(ScriptDataEscapeStart Escaped)
                    | _ ->
                        this.EmitChar '<'
                        this.Reconsume(RawData kind)
            // end tag open in raw text
            | RawEndTagOpen kind ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    let c = char ci
                    match lowerAsciiLetter c with
                    | -1 ->
                        this.EmitChar '<'
                        this.EmitChar '/'
                        this.Reconsume(RawData kind)
                    | cl ->
                        this.CreateTag(EndTag, char cl)
                        tempBuf.Append c |> ignore
                        this.To(RawEndTagName kind)
            // end tag name in raw text
            | RawEndTagName kind ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    let c = char ci
                    let mutable result = Proc.Continue
                    let mutable decided = false
                    if this.HaveAppropriateEndTag() then
                        if isSpace c then
                            tempBuf.Clear() |> ignore
                            result <- this.To BeforeAttributeName
                            decided <- true
                        elif c = '/' then
                            tempBuf.Clear() |> ignore
                            result <- this.To SelfClosingStartTag
                            decided <- true
                        elif c = '>' then
                            tempBuf.Clear() |> ignore
                            result <- this.EmitTag Data
                            decided <- true
                    if decided then
                        result
                    else
                        match lowerAsciiLetter c with
                        | -1 ->
                            this.DiscardTag()
                            this.EmitChar '<'
                            this.EmitChar '/'
                            this.EmitTempBuf()
                            this.Reconsume(RawData kind)
                        | cl ->
                            currentTagName.Append(char cl) |> ignore
                            tempBuf.Append c |> ignore
                            Proc.Continue
            // script data double escape start
            | ScriptDataEscapeStart DoubleEscaped ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    let c = char ci
                    if isSpace c || c = '/' || c = '>' then
                        let esc = if tempBuf.Length = 6 && tempBuf.ToString() = "script" then DoubleEscaped else Escaped
                        this.EmitChar c
                        this.To(RawData(if esc = DoubleEscaped then ScriptDataDoubleEscapedKind else ScriptDataEscapedKind))
                    else
                        match lowerAsciiLetter c with
                        | -1 -> this.Reconsume(RawData ScriptDataEscapedKind)
                        | cl ->
                            tempBuf.Append(char cl) |> ignore
                            this.EmitChar c
                            Proc.Continue
            // script data escape start
            | ScriptDataEscapeStart Escaped ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '-' ->
                        this.EmitChar '-'
                        this.To ScriptDataEscapeStartDash
                    | _ -> this.Reconsume(RawData ScriptData)
            | ScriptDataEscapeStartDash ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '-' ->
                        this.EmitChar '-'
                        this.To(ScriptDataEscapedDashDash Escaped)
                    | _ -> this.Reconsume(RawData ScriptData)
            // script data (double) escaped dash
            | ScriptDataEscapedDash kind ->
                let escapedKind = if kind = DoubleEscaped then ScriptDataDoubleEscapedKind else ScriptDataEscapedKind
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '-' ->
                        this.EmitChar '-'
                        this.To(ScriptDataEscapedDashDash kind)
                    | '<' ->
                        if kind = DoubleEscaped then this.EmitChar '<'
                        this.To(RawLessThanSign escapedKind)
                    | '\000' ->
                        this.EmitError()
                        this.EmitChar '�'
                        this.To(RawData escapedKind)
                    | c ->
                        this.EmitChar c
                        this.To(RawData escapedKind)
            // script data (double) escaped dash dash
            | ScriptDataEscapedDashDash kind ->
                let escapedKind = if kind = DoubleEscaped then ScriptDataDoubleEscapedKind else ScriptDataEscapedKind
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '-' ->
                        this.EmitChar '-'
                        Proc.Continue
                    | '<' ->
                        if kind = DoubleEscaped then this.EmitChar '<'
                        this.To(RawLessThanSign escapedKind)
                    | '>' ->
                        this.EmitChar '>'
                        this.To(RawData ScriptData)
                    | '\000' ->
                        this.EmitError()
                        this.EmitChar '�'
                        this.To(RawData escapedKind)
                    | c ->
                        this.EmitChar c
                        this.To(RawData escapedKind)
            // script data double escape end
            | ScriptDataDoubleEscapeEnd ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    let c = char ci
                    if isSpace c || c = '/' || c = '>' then
                        let esc = if tempBuf.Length = 6 && tempBuf.ToString() = "script" then Escaped else DoubleEscaped
                        this.EmitChar c
                        this.To(RawData(if esc = DoubleEscaped then ScriptDataDoubleEscapedKind else ScriptDataEscapedKind))
                    else
                        match lowerAsciiLetter c with
                        | -1 -> this.Reconsume(RawData ScriptDataDoubleEscapedKind)
                        | cl ->
                            tempBuf.Append(char cl) |> ignore
                            this.EmitChar c
                            Proc.Continue
            // before attribute name
            | BeforeAttributeName ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    let c = char ci
                    if isSpace c then Proc.Continue
                    else
                        match c with
                        | '/' -> this.To SelfClosingStartTag
                        | '>' -> this.EmitTag Data
                        | '\000' ->
                            this.EmitError()
                            this.CreateAttribute '�'
                            this.To AttributeName
                        | c ->
                            match lowerAsciiLetter c with
                            | -1 ->
                                if c = '"' || c = '\'' || c = '<' || c = '=' then this.EmitError()
                                this.CreateAttribute c
                                this.To AttributeName
                            | cl ->
                                this.CreateAttribute(char cl)
                                this.To AttributeName
            // attribute name
            | AttributeName ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    let c = char ci
                    if isSpace c then this.To AfterAttributeName
                    else
                        match c with
                        | '/' -> this.To SelfClosingStartTag
                        | '=' -> this.To BeforeAttributeValue
                        | '>' -> this.EmitTag Data
                        | '\000' ->
                            this.EmitError()
                            currentAttrName.Append '�' |> ignore
                            Proc.Continue
                        | c ->
                            match lowerAsciiLetter c with
                            | -1 ->
                                if c = '"' || c = '\'' || c = '<' then this.EmitError()
                                currentAttrName.Append c |> ignore
                                Proc.Continue
                            | cl ->
                                currentAttrName.Append(char cl) |> ignore
                                Proc.Continue
            // after attribute name
            | AfterAttributeName ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    let c = char ci
                    if isSpace c then Proc.Continue
                    else
                        match c with
                        | '/' -> this.To SelfClosingStartTag
                        | '=' -> this.To BeforeAttributeValue
                        | '>' -> this.EmitTag Data
                        | '\000' ->
                            this.EmitError()
                            this.CreateAttribute '�'
                            this.To AttributeName
                        | c ->
                            match lowerAsciiLetter c with
                            | -1 ->
                                if c = '"' || c = '\'' || c = '<' then this.EmitError()
                                this.CreateAttribute c
                                this.To AttributeName
                            | cl ->
                                this.CreateAttribute(char cl)
                                this.To AttributeName
            // before attribute value
            | BeforeAttributeValue ->
                match this.Peek input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '\t' | '\n' | '\r' | '\012' | ' ' ->
                        this.DiscardChar input
                        Proc.Continue
                    | '"' ->
                        this.DiscardChar input
                        this.To(AttributeValue DoubleQuoted)
                    | '\'' ->
                        this.DiscardChar input
                        this.To(AttributeValue SingleQuoted)
                    | '>' ->
                        this.DiscardChar input
                        this.EmitError()
                        this.EmitTag Data
                    | _ -> this.To(AttributeValue Unquoted)
            // attribute value (double quoted)
            | AttributeValue DoubleQuoted ->
                match this.PopExceptFrom(input, doubleQuotedSet) with
                | NoInput -> Proc.Suspend
                | FromSet '"' -> this.To AfterAttributeValueQuoted
                | FromSet '&' ->
                    this.ConsumeCharRef()
                    Proc.Continue
                | FromSet '\000' ->
                    this.EmitError()
                    currentAttrValue.Append '�' |> ignore
                    Proc.Continue
                | FromSet c ->
                    currentAttrValue.Append c |> ignore
                    Proc.Continue
                | NotFromSet b ->
                    currentAttrValue.Append b |> ignore
                    Proc.Continue
            // attribute value (single quoted)
            | AttributeValue SingleQuoted ->
                match this.PopExceptFrom(input, singleQuotedSet) with
                | NoInput -> Proc.Suspend
                | FromSet '\'' -> this.To AfterAttributeValueQuoted
                | FromSet '&' ->
                    this.ConsumeCharRef()
                    Proc.Continue
                | FromSet '\000' ->
                    this.EmitError()
                    currentAttrValue.Append '�' |> ignore
                    Proc.Continue
                | FromSet c ->
                    currentAttrValue.Append c |> ignore
                    Proc.Continue
                | NotFromSet b ->
                    currentAttrValue.Append b |> ignore
                    Proc.Continue
            // attribute value (unquoted)
            | AttributeValue Unquoted ->
                match this.PopExceptFrom(input, unquotedSet) with
                | NoInput -> Proc.Suspend
                | FromSet('\t' | '\n' | '\012' | ' ') -> this.To BeforeAttributeName
                | FromSet '&' ->
                    this.ConsumeCharRef()
                    Proc.Continue
                | FromSet '>' -> this.EmitTag Data
                | FromSet '\000' ->
                    this.EmitError()
                    currentAttrValue.Append '�' |> ignore
                    Proc.Continue
                | FromSet c ->
                    if c = '"' || c = '\'' || c = '<' || c = '=' || c = '`' then this.EmitError()
                    currentAttrValue.Append c |> ignore
                    Proc.Continue
                | NotFromSet b ->
                    currentAttrValue.Append b |> ignore
                    Proc.Continue
            // after attribute value (quoted)
            | AfterAttributeValueQuoted ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    let c = char ci
                    if isSpace c then this.To BeforeAttributeName
                    else
                        match c with
                        | '/' -> this.To SelfClosingStartTag
                        | '>' -> this.EmitTag Data
                        | _ ->
                            this.EmitError()
                            this.Reconsume BeforeAttributeName
            // self-closing start tag
            | SelfClosingStartTag ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '>' ->
                        currentTagSelfClosing <- true
                        this.EmitTag Data
                    | _ ->
                        this.EmitError()
                        this.Reconsume BeforeAttributeName
            // comment start
            | CommentStart ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '-' -> this.To CommentStartDash
                    | '\000' ->
                        this.EmitError()
                        currentComment.Append '�' |> ignore
                        this.To Comment
                    | '>' ->
                        this.EmitError()
                        this.EmitCurrentComment()
                        this.To Data
                    | c ->
                        currentComment.Append c |> ignore
                        this.To Comment
            // comment start dash
            | CommentStartDash ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '-' -> this.To CommentEnd
                    | '\000' ->
                        this.EmitError()
                        currentComment.Append("-�") |> ignore
                        this.To Comment
                    | '>' ->
                        this.EmitError()
                        this.EmitCurrentComment()
                        this.To Data
                    | c ->
                        currentComment.Append('-').Append c |> ignore
                        this.To Comment
            // comment
            | Comment ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '<' ->
                        currentComment.Append '<' |> ignore
                        this.To CommentLessThanSign
                    | '-' -> this.To CommentEndDash
                    | '\000' ->
                        this.EmitError()
                        currentComment.Append '�' |> ignore
                        Proc.Continue
                    | c ->
                        currentComment.Append c |> ignore
                        Proc.Continue
            | CommentLessThanSign ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '!' ->
                        currentComment.Append '!' |> ignore
                        this.To CommentLessThanSignBang
                    | '<' ->
                        currentComment.Append '<' |> ignore
                        Proc.Continue
                    | _ -> this.Reconsume Comment
            | CommentLessThanSignBang ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '-' -> this.To CommentLessThanSignBangDash
                    | _ -> this.Reconsume Comment
            | CommentLessThanSignBangDash ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '-' -> this.To CommentLessThanSignBangDashDash
                    | _ -> this.Reconsume CommentEndDash
            | CommentLessThanSignBangDashDash ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '>' -> this.Reconsume CommentEnd
                    | _ ->
                        this.EmitError()
                        this.Reconsume CommentEnd
            | CommentEndDash ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '-' -> this.To CommentEnd
                    | '\000' ->
                        this.EmitError()
                        currentComment.Append("-�") |> ignore
                        this.To Comment
                    | c ->
                        currentComment.Append('-').Append c |> ignore
                        this.To Comment
            | CommentEnd ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '>' ->
                        this.EmitCurrentComment()
                        this.To Data
                    | '!' -> this.To CommentEndBang
                    | '-' ->
                        currentComment.Append '-' |> ignore
                        Proc.Continue
                    | _ ->
                        currentComment.Append "--" |> ignore
                        this.Reconsume Comment
            | CommentEndBang ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '-' ->
                        currentComment.Append "--!" |> ignore
                        this.To CommentEndDash
                    | '>' ->
                        this.EmitError()
                        this.EmitCurrentComment()
                        this.To Data
                    | '\000' ->
                        this.EmitError()
                        currentComment.Append("--!�") |> ignore
                        this.To Comment
                    | c ->
                        currentComment.Append("--!").Append c |> ignore
                        this.To Comment
            // doctype
            | Doctype ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    let c = char ci
                    if isSpace c then this.To BeforeDoctypeName
                    elif c = '>' then this.Reconsume BeforeDoctypeName
                    else
                        this.EmitError()
                        this.Reconsume BeforeDoctypeName
            | BeforeDoctypeName ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    let c = char ci
                    if isSpace c then Proc.Continue
                    else
                        match c with
                        | '\000' ->
                            this.EmitError()
                            this.To DoctypeName
                        | '>' ->
                            this.EmitError()
                            sink.ProcessDoctype()
                            this.To Data
                        | _ -> this.To DoctypeName
            | DoctypeName ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    let c = char ci
                    if isSpace c then
                        tempBuf.Clear() |> ignore
                        this.To AfterDoctypeName
                    else
                        match c with
                        | '>' ->
                            sink.ProcessDoctype()
                            this.To Data
                        | '\000' ->
                            this.EmitError()
                            Proc.Continue
                        | _ -> Proc.Continue
            | AfterDoctypeName ->
                if this.Eat(input, "public", true) then
                    this.To(AfterDoctypeKeyword PublicId)
                elif this.Eat(input, "system", true) then
                    this.To(AfterDoctypeKeyword SystemId)
                else
                    match this.GetChar input with
                    | -1 -> Proc.Suspend
                    | ci ->
                        let c = char ci
                        if isSpace c then Proc.Continue
                        elif c = '>' then
                            sink.ProcessDoctype()
                            this.To Data
                        else
                            this.EmitError()
                            this.Reconsume BogusDoctype
            | AfterDoctypeKeyword kind ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    let c = char ci
                    if isSpace c then this.To(BeforeDoctypeIdentifier kind)
                    else
                        match c with
                        | '"' ->
                            this.EmitError()
                            this.To(DoctypeIdentifierDoubleQuoted kind)
                        | '\'' ->
                            this.EmitError()
                            this.To(DoctypeIdentifierSingleQuoted kind)
                        | '>' ->
                            this.EmitError()
                            sink.ProcessDoctype()
                            this.To Data
                        | _ ->
                            this.EmitError()
                            this.Reconsume BogusDoctype
            | BeforeDoctypeIdentifier kind ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    let c = char ci
                    if isSpace c then Proc.Continue
                    else
                        match c with
                        | '"' -> this.To(DoctypeIdentifierDoubleQuoted kind)
                        | '\'' -> this.To(DoctypeIdentifierSingleQuoted kind)
                        | '>' ->
                            this.EmitError()
                            sink.ProcessDoctype()
                            this.To Data
                        | _ ->
                            this.EmitError()
                            this.Reconsume BogusDoctype
            | DoctypeIdentifierDoubleQuoted kind ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '"' -> this.To(AfterDoctypeIdentifier kind)
                    | '\000' ->
                        this.EmitError()
                        Proc.Continue
                    | '>' ->
                        this.EmitError()
                        sink.ProcessDoctype()
                        this.To Data
                    | _ -> Proc.Continue
            | DoctypeIdentifierSingleQuoted kind ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '\'' -> this.To(AfterDoctypeIdentifier kind)
                    | '\000' ->
                        this.EmitError()
                        Proc.Continue
                    | '>' ->
                        this.EmitError()
                        sink.ProcessDoctype()
                        this.To Data
                    | _ -> Proc.Continue
            | AfterDoctypeIdentifier PublicId ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    let c = char ci
                    if isSpace c then this.To BetweenDoctypePublicAndSystemIdentifiers
                    else
                        match c with
                        | '>' ->
                            sink.ProcessDoctype()
                            this.To Data
                        | '"' ->
                            this.EmitError()
                            this.To(DoctypeIdentifierDoubleQuoted SystemId)
                        | '\'' ->
                            this.EmitError()
                            this.To(DoctypeIdentifierSingleQuoted SystemId)
                        | _ ->
                            this.EmitError()
                            this.Reconsume BogusDoctype
            | AfterDoctypeIdentifier SystemId ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    let c = char ci
                    if isSpace c then Proc.Continue
                    elif c = '>' then
                        sink.ProcessDoctype()
                        this.To Data
                    else
                        this.EmitError()
                        this.Reconsume BogusDoctype
            | BetweenDoctypePublicAndSystemIdentifiers ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    let c = char ci
                    if isSpace c then Proc.Continue
                    else
                        match c with
                        | '>' ->
                            sink.ProcessDoctype()
                            this.To Data
                        | '"' -> this.To(DoctypeIdentifierDoubleQuoted SystemId)
                        | '\'' -> this.To(DoctypeIdentifierSingleQuoted SystemId)
                        | _ ->
                            this.EmitError()
                            this.Reconsume BogusDoctype
            | BogusDoctype ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '>' ->
                        sink.ProcessDoctype()
                        this.To Data
                    | '\000' ->
                        this.EmitError()
                        Proc.Continue
                    | _ -> Proc.Continue
            // bogus comment
            | BogusComment ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | '>' ->
                        this.EmitCurrentComment()
                        this.To Data
                    | '\000' ->
                        this.EmitError()
                        currentComment.Append '�' |> ignore
                        Proc.Continue
                    | c ->
                        currentComment.Append c |> ignore
                        Proc.Continue
            // markup declaration open
            | MarkupDeclarationOpen ->
                if this.Eat(input, "--", false) then
                    currentComment.Clear() |> ignore
                    this.To CommentStart
                elif this.Eat(input, "doctype", true) then
                    this.To Doctype
                elif sink.AdjustedCurrentNodePresentButNotInHtmlNamespace() && this.Eat(input, "[CDATA[", false) then
                    tempBuf.Clear() |> ignore
                    this.To CdataSection
                else
                    this.EmitError()
                    currentComment.Clear() |> ignore
                    this.To BogusComment
            // cdata section
            | CdataSection ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | ']' -> this.To CdataSectionBracket
                    | '\000' ->
                        this.EmitTempBuf()
                        this.EmitChar '\000'
                        Proc.Continue
                    | c ->
                        tempBuf.Append c |> ignore
                        Proc.Continue
            | CdataSectionBracket ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | ']' -> this.To CdataSectionEnd
                    | _ ->
                        tempBuf.Append ']' |> ignore
                        this.Reconsume CdataSection
            | CdataSectionEnd ->
                match this.GetChar input with
                | -1 -> Proc.Suspend
                | ci ->
                    match char ci with
                    | ']' ->
                        tempBuf.Append ']' |> ignore
                        Proc.Continue
                    | '>' ->
                        this.EmitTempBuf()
                        this.To Data
                    | _ ->
                        tempBuf.Append("]]") |> ignore
                        this.Reconsume CdataSection

    // --- The end of the input ----------------------------------------------------------------

    member private this.EofStep() : Proc =
        let eof () =
            sink.ProcessEof()
            Proc.Suspend
        match state with
        | Data
        | RawData Rcdata
        | RawData Rawtext
        | RawData ScriptData
        | State.Plaintext -> eof ()
        | TagName
        | RawData ScriptDataEscapedKind
        | RawData ScriptDataDoubleEscapedKind
        | BeforeAttributeName
        | AttributeName
        | AfterAttributeName
        | AttributeValue _
        | AfterAttributeValueQuoted
        | SelfClosingStartTag
        | ScriptDataEscapedDash _
        | ScriptDataEscapedDashDash _ ->
            this.EmitError()
            this.To Data
        | BeforeAttributeValue -> this.Reconsume(AttributeValue Unquoted)
        | TagOpen ->
            this.EmitError()
            this.EmitChar '<'
            this.To Data
        | EndTagOpen ->
            this.EmitError()
            this.EmitChar '<'
            this.EmitChar '/'
            this.To Data
        | RawLessThanSign ScriptDataDoubleEscapedKind -> this.To(RawData ScriptDataDoubleEscapedKind)
        | RawLessThanSign kind ->
            this.EmitChar '<'
            this.To(RawData kind)
        | RawEndTagOpen kind ->
            this.EmitChar '<'
            this.EmitChar '/'
            this.To(RawData kind)
        | RawEndTagName kind ->
            this.EmitChar '<'
            this.EmitChar '/'
            this.EmitTempBuf()
            this.To(RawData kind)
        | ScriptDataEscapeStart kind -> this.To(RawData(if kind = DoubleEscaped then ScriptDataDoubleEscapedKind else ScriptDataEscapedKind))
        | ScriptDataEscapeStartDash -> this.To(RawData ScriptData)
        | ScriptDataDoubleEscapeEnd -> this.To(RawData ScriptDataDoubleEscapedKind)
        | CommentStart
        | CommentStartDash
        | Comment
        | CommentEndDash
        | CommentEnd
        | CommentEndBang ->
            this.EmitError()
            this.EmitCurrentComment()
            this.To Data
        | CommentLessThanSign
        | CommentLessThanSignBang -> this.Reconsume Comment
        | CommentLessThanSignBangDash -> this.Reconsume CommentEndDash
        | CommentLessThanSignBangDashDash -> this.Reconsume CommentEnd
        | Doctype
        | BeforeDoctypeName ->
            this.EmitError()
            sink.ProcessDoctype()
            this.To Data
        | DoctypeName
        | AfterDoctypeName
        | AfterDoctypeKeyword _
        | BeforeDoctypeIdentifier _
        | DoctypeIdentifierDoubleQuoted _
        | DoctypeIdentifierSingleQuoted _
        | AfterDoctypeIdentifier _
        | BetweenDoctypePublicAndSystemIdentifiers ->
            this.EmitError()
            sink.ProcessDoctype()
            this.To Data
        | BogusDoctype ->
            sink.ProcessDoctype()
            this.To Data
        | BogusComment ->
            this.EmitCurrentComment()
            this.To Data
        | MarkupDeclarationOpen ->
            this.EmitError()
            this.To BogusComment
        | CdataSection ->
            this.EmitTempBuf()
            this.EmitError()
            this.To Data
        | CdataSectionBracket ->
            tempBuf.Append ']' |> ignore
            this.To CdataSection
        | CdataSectionEnd ->
            tempBuf.Append "]]" |> ignore
            this.To CdataSection

    /// Indicates that the input has ended.
    member this.End() =
        // Handle the end of the input in the character reference tokenizer first, as it might
        // un-consume what the state machine has still to read.
        let input = Input ""
        match charRef with
        | null -> ()
        | tok ->
            charRef <- null
            tok.EndOfFile(this, input)
            this.ProcessCharRef(tok.Result)
        atEof <- true
        this.Run input |> ignore
        while this.EofStep() = Proc.Continue do
            ()
        sink.End()

/// `char_ref::Status`
and CharRefStatus =
    | Stuck = 0
    | Progress = 1
    | Done = 2

/// Tokenizes one character reference (`&amp;`, `&#65;`), as the tokenizer's data, RCDATA and
/// attribute value states hit an `&`.
and [<Sealed; AllowNullLiteral>] CharRefTokenizer(isConsumedInAttribute: bool) =
    let mutable state = Begin
    let mutable result: string | null = null
    let mutable num = 0u
    let mutable numTooBig = false
    let mutable seenDigit = false
    let mutable hexMarker = '\000'
    let mutable nameBuf: StringBuilder | null = null
    let mutable nameMatch = struct (0, 0)
    let mutable hasNameMatch = false
    let mutable nameLen = 0

    /// The characters the reference stands for: empty if it isn't one.
    member _.Result: string = nonNull result

    member private _.FinishNone() =
        result <- ""
        CharRefStatus.Done

    member private _.FinishOne(codePoint: int) =
        result <- Char.ConvertFromUtf32 codePoint
        CharRefStatus.Done

    member this.Step(tokenizer: Tokenizer, input: Input) : CharRefStatus =
        if not (isNull result) then
            CharRefStatus.Done
        else
            match state with
            | Begin -> this.DoBegin(tokenizer, input)
            | Octothorpe -> this.DoOctothorpe(tokenizer, input)
            | Numeric(``base``) -> this.DoNumeric(tokenizer, input, ``base``)
            | NumericSemicolon -> this.DoNumericSemicolon(tokenizer, input)
            | Named -> this.DoNamed(tokenizer, input)
            | BogusName -> this.DoBogusName(tokenizer, input)

    member private this.DoBegin(tokenizer: Tokenizer, input: Input) =
        match tokenizer.Peek input with
        | -1 -> CharRefStatus.Stuck
        | ci when isAsciiAlphanumeric (char ci) ->
            state <- Named
            nameBuf <- StringBuilder()
            CharRefStatus.Progress
        | ci when ci = int '#' ->
            tokenizer.DiscardChar input
            state <- Octothorpe
            CharRefStatus.Progress
        | _ -> this.FinishNone()

    member private this.DoOctothorpe(tokenizer: Tokenizer, input: Input) =
        match tokenizer.Peek input with
        | -1 -> CharRefStatus.Stuck
        | ci when ci = int 'x' || ci = int 'X' ->
            tokenizer.DiscardChar input
            hexMarker <- char ci
            state <- Numeric 16u
            CharRefStatus.Progress
        | _ ->
            hexMarker <- '\000'
            state <- Numeric 10u
            CharRefStatus.Progress

    member private this.DoNumeric(tokenizer: Tokenizer, input: Input, ``base``: uint32) =
        match tokenizer.Peek input with
        | -1 -> CharRefStatus.Stuck
        | ci ->
            let c = char ci
            let digit =
                if c >= '0' && c <= '9' then int c - int '0'
                elif ``base`` = 16u && c >= 'a' && c <= 'f' then int c - int 'a' + 10
                elif ``base`` = 16u && c >= 'A' && c <= 'F' then int c - int 'A' + 10
                else -1
            if digit >= 0 then
                tokenizer.DiscardChar input
                num <- num * ``base``
                // We might overflow, and the character is definitely invalid. We still parse digits
                // and semicolon, but don't use the result.
                if num > 0x10FFFFu then numTooBig <- true
                num <- num + uint32 digit
                seenDigit <- true
                CharRefStatus.Progress
            elif not seenDigit then
                this.UnconsumeNumeric(tokenizer, input)
            else
                state <- NumericSemicolon
                CharRefStatus.Progress

    member private this.DoNumericSemicolon(tokenizer: Tokenizer, input: Input) =
        match tokenizer.Peek input with
        | -1 -> CharRefStatus.Stuck
        | ci ->
            if ci = int ';' then tokenizer.DiscardChar input else tokenizer.EmitError()
            this.FinishNumeric tokenizer

    member private this.UnconsumeNumeric(tokenizer: Tokenizer, input: Input) =
        let unconsume = if hexMarker <> '\000' then "#" + string hexMarker else "#"
        input.PushFront unconsume
        tokenizer.EmitError()
        this.FinishNone()

    member private this.FinishNumeric(tokenizer: Tokenizer) =
        let n = num
        let struct (c, error) =
            if n > 0x10FFFFu || numTooBig then struct (0xFFFD, true)
            elif n = 0u || (n >= 0xD800u && n <= 0xDFFFu) then struct (0xFFFD, true)
            elif n >= 0x80u && n <= 0x9Fu then
                match c1Replacements[int n - 0x80] with
                | -1 -> struct (int n, true)
                | replacement -> struct (replacement, true)
            elif (n >= 0x01u && n <= 0x08u) || n = 0x0Bu || (n >= 0x0Du && n <= 0x1Fu) || n = 0x7Fu || (n >= 0xFDD0u && n <= 0xFDEFu) then
                struct (int n, true)
            elif (n &&& 0xFFFEu) = 0xFFFEu then struct (int n, true)
            else struct (int n, false)
        if error then tokenizer.EmitError()
        this.FinishOne c

    member private this.DoNamed(tokenizer: Tokenizer, input: Input) =
        match tokenizer.Peek input with
        | -1 -> CharRefStatus.Stuck
        | ci ->
            let c = char ci
            tokenizer.DiscardChar input
            let buf = nonNull nameBuf
            buf.Append c |> ignore
            match NamedEntities.table.TryGetValue(buf.ToString()) with
            // We have either a full match or a prefix of one.
            | true, (struct (first, _) as m) ->
                if first <> 0 then
                    // We have a full match, but there might be a longer one to come.
                    nameMatch <- m
                    hasNameMatch <- true
                    nameLen <- buf.Length
                CharRefStatus.Progress
            // Can't continue the match.
            | _ -> this.FinishNamed(tokenizer, input, ci)

    member private this.UnconsumeName(input: Input) =
        input.PushFront((nonNull nameBuf).ToString())
        nameBuf <- null

    member private this.FinishNamed(tokenizer: Tokenizer, input: Input, endChar: int) =
        if not hasNameMatch then
            if endChar >= 0 && isAsciiAlphanumeric (char endChar) then
                // Keep looking for a semicolon, to determine whether we emit a parse error.
                state <- BogusName
                CharRefStatus.Progress
            else
                // Check length because &; is not a parse error.
                if endChar = int ';' && (nonNull nameBuf).Length > 1 then tokenizer.EmitError()
                this.UnconsumeName input
                this.FinishNone()
        else
            let struct (c1, c2) = nameMatch
            // We have a complete match, but we may have consumed additional characters into
            // name_buf: usually at least one, several in cases like &notit.
            let buf = nonNull nameBuf
            let lastMatched = buf[nameLen - 1]
            // There might not be a next character after the match, if we had a full match and
            // then hit the end of the input.
            let nextAfter = if nameLen = buf.Length then -1 else int buf[nameLen]
            // In an attribute, a match that doesn't end in a semicolon and is followed by "=" or an
            // ASCII alphanumeric is kept as text, for historical reasons.
            let unconsumeAll =
                if lastMatched = ';' then false
                elif isConsumedInAttribute && nextAfter = int '=' then true
                elif isConsumedInAttribute && nextAfter >= 0 && isAsciiAlphanumeric (char nextAfter) then true
                else
                    tokenizer.EmitError()
                    false
            if unconsumeAll then
                this.UnconsumeName input
                this.FinishNone()
            else
                input.PushFront(buf.ToString(nameLen, buf.Length - nameLen))
                tokenizer.ClearIgnoreLf()
                result <- Char.ConvertFromUtf32 c1 + (if c2 = 0 then "" else Char.ConvertFromUtf32 c2)
                CharRefStatus.Done

    member private this.DoBogusName(tokenizer: Tokenizer, input: Input) =
        match tokenizer.Peek input with
        | -1 -> CharRefStatus.Stuck
        | ci ->
            let c = char ci
            tokenizer.DiscardChar input
            (nonNull nameBuf).Append c |> ignore
            if isAsciiAlphanumeric c then
                CharRefStatus.Progress
            else
                if c = ';' then tokenizer.EmitError()
                this.UnconsumeName input
                this.FinishNone()

    member this.EndOfFile(tokenizer: Tokenizer, input: Input) =
        while isNull result do
            match state with
            | Begin -> this.FinishNone() |> ignore
            | Numeric _ when not seenDigit -> this.UnconsumeNumeric(tokenizer, input) |> ignore
            | Numeric _
            | NumericSemicolon ->
                tokenizer.EmitError()
                this.FinishNumeric tokenizer |> ignore
            | Named -> this.FinishNamed(tokenizer, input, -1) |> ignore
            | BogusName ->
                this.UnconsumeName input
                this.FinishNone() |> ignore
            | Octothorpe ->
                input.PushFront "#"
                tokenizer.EmitError()
                this.FinishNone() |> ignore
