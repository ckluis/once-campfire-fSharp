// Port of rust/crates/ruby/src/erb.rs
/// `ERB::Util.html_escape` (and `h`): `& < > " '` become `&amp; &lt; &gt; &quot; &#39;`.
module Campfire.Ruby.Erb

open System
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
