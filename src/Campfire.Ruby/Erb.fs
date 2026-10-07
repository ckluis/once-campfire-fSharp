// Port of rust/crates/ruby/src/erb.rs
/// `ERB::Util.html_escape` (and `h`): `& < > " '` become `&amp; &lt; &gt; &quot; &#39;`.
module Campfire.Ruby.Erb

open System
open System.Buffers
open System.IO
open System.Text

let private replacement (c: char) : string | null =
    match c with
    | '&' -> "&amp;"
    | '<' -> "&lt;"
    | '>' -> "&gt;"
    | '"' -> "&quot;"
    | '\'' -> "&#39;"
    | _ -> null

/// Writes the text between the escaped characters a run at a time.
let writeHtmlEscaped (dest: TextWriter) (s: string) : unit =
    let mutable last = 0
    for index in 0 .. s.Length - 1 do
        match replacement s[index] with
        | null -> ()
        | r ->
            dest.Write(s.AsSpan(last, index - last))
            dest.Write r
            last <- index + 1
    dest.Write(s.AsSpan last)

let pushHtmlEscaped (out: StringBuilder) (s: string) : unit =
    let mutable last = 0
    for index in 0 .. s.Length - 1 do
        match replacement s[index] with
        | null -> ()
        | r ->
            out.Append(s, last, index - last).Append r |> ignore
            last <- index + 1
    out.Append(s, last, s.Length - last) |> ignore

let htmlEscape (s: string) : string =
    let out = StringBuilder(s.Length)
    pushHtmlEscaped out s
    out.ToString()

let private escapable = SearchValues.Create "&<>\"'"

let private amp = "&amp;"B
let private lt = "&lt;"B
let private gt = "&gt;"B
let private quot = "&quot;"B
let private apos = "&#39;"B

let private replacementUtf8 (c: char) : byte[] =
    match c with
    | '&' -> amp
    | '<' -> lt
    | '>' -> gt
    | '"' -> quot
    | _ -> apos

/// Writes `s` as UTF-8 onto `dest`: the text between the escaped characters a run at a time
/// (the five are ASCII, so a run never splits a surrogate pair), then the replacement.
/// `ERB::Util.html_escape`, for the templates that render straight into a pooled UTF-8 buffer.
let writeHtmlEscapedUtf8 (dest: IBufferWriter<byte>) (s: ReadOnlySpan<char>) : unit =
    let mutable rest = s
    let mutable going = true
    while going do
        let index = rest.IndexOfAny escapable
        let run = if index < 0 then rest else rest.Slice(0, index)
        if not run.IsEmpty then
            let count = Encoding.UTF8.GetByteCount run
            let span = dest.GetSpan count
            let written = Encoding.UTF8.GetBytes(run, span)
            dest.Advance written
        if index < 0 then
            going <- false
        else
            let replacement = replacementUtf8 rest[index]
            let span = dest.GetSpan replacement.Length
            replacement.AsSpan().CopyTo span
            dest.Advance replacement.Length
            rest <- rest.Slice(index + 1)
