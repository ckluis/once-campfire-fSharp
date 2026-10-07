// Port of rust/crates/richtext/src/ruby.rs
//
// Ruby and Active Support string behaviors the pipeline depends on, beyond `Campfire.Ruby`'s.
module Campfire.RichText.RubyExt

open System
open System.Text
open Campfire.RailsCompat
open Campfire.RichText.Unicode

/// Active Support's `String#blank?`: empty or only Unicode whitespace (`/\A[[:space:]]*\z/`).
let isBlank (s: string) : bool =
    let mutable blank = true
    let mutable i = 0
    while blank && i < s.Length do
        if not (Chars.isWhitespace s[i]) then blank <- false
        i <- i + 1
    blank

/// `Object#present?` for an optional string.
let presence (s: string voption) : string voption =
    match s with
    | ValueSome v when not (isBlank v) -> s
    | _ -> ValueNone

/// `String#chomp("")`: removes every trailing `\n` or `\r\n`, but not a lone `\r`.
let chompNewlines (s: string) : string =
    let mutable stop = s.Length
    let mutable fin = false
    while not fin do
        if stop >= 2 && s[stop - 2] = '\r' && s[stop - 1] = '\n' then stop <- stop - 2
        elif stop >= 1 && s[stop - 1] = '\n' then stop <- stop - 1
        else fin <- true
    if stop = s.Length then s else s.Substring(0, stop)

/// `String#chomp` with no argument: removes one trailing `\r\n`, `\n` or `\r`.
let chomp (s: string) : string =
    if s.EndsWith("\r\n", StringComparison.Ordinal) then s.Substring(0, s.Length - 2)
    elif s.EndsWith("\n", StringComparison.Ordinal) || s.EndsWith("\r", StringComparison.Ordinal) then s.Substring(0, s.Length - 1)
    else s

let private codePointCount (s: string) : int =
    let mutable n = 0
    let mutable i = 0
    while i < s.Length do
        i <- i + (if Char.IsHighSurrogate s[i] && i + 1 < s.Length && Char.IsLowSurrogate s[i + 1] then 2 else 1)
        n <- n + 1
    n

/// The first `n` code points of `s`.
let private takeCodePoints (s: string) (n: int) : string =
    let mutable taken = 0
    let mutable i = 0
    while taken < n && i < s.Length do
        i <- i + (if Char.IsHighSurrogate s[i] && i + 1 < s.Length && Char.IsLowSurrogate s[i + 1] then 2 else 1)
        taken <- taken + 1
    s.Substring(0, i)

/// Action View's `truncate(text, length:, omission:)` with the default separator, before escaping.
let truncate (text: string) (length: int) (omission: string) : string =
    let chars = codePointCount text
    if chars <= length then
        text
    else
        let keep = Math.Max(0, length - codePointCount omission)
        takeCodePoints text keep + omission

/// `JSON.parse` removes `/* */` and `//` comments, which serde_json would reject.
let private stripJsonComments (s: string) : string option =
    let out = StringBuilder(s.Length)
    let mutable i = 0
    let mutable inString = false
    let mutable failed = false
    while not failed && i < s.Length do
        let c = s[i]
        i <- i + 1
        if inString then
            out.Append c |> ignore
            if c = '\\' then
                if i < s.Length then
                    out.Append s[i] |> ignore
                    i <- i + 1
                else
                    failed <- true
            elif c = '"' then
                inString <- false
        elif c = '"' then
            inString <- true
            out.Append c |> ignore
        elif c = '/' && i < s.Length && s[i] = '*' then
            i <- i + 1
            let mutable closed = false
            while not closed && i < s.Length do
                if s[i] = '*' && i + 1 < s.Length && s[i + 1] = '/' then
                    i <- i + 2
                    closed <- true
                else
                    i <- i + 1
            if not closed then failed <- true else out.Append ' ' |> ignore
        elif c = '/' && i < s.Length && s[i] = '/' then
            while i < s.Length && s[i] <> '\n' do
                i <- i + 1
            // The line feed ends the comment and is consumed with it.
            if i < s.Length then i <- i + 1
            out.Append ' ' |> ignore
        else
            out.Append c |> ignore
    if failed then None else Some(out.ToString())

/// Ruby's `JSON.parse`, which (unlike serde_json) also skips `/* */` and `//` comments.
let jsonParse (s: string) : Value option =
    match stripJsonComments s with
    | None -> None
    | Some stripped -> Json.parse (Encoding.UTF8.GetBytes stripped)

let stringInspect (s: string) : string =
    let out = StringBuilder("\"")
    for i in 0 .. s.Length - 1 do
        let c = s[i]
        match c with
        | '"' -> out.Append "\\\"" |> ignore
        | '\\' -> out.Append "\\\\" |> ignore
        | '\n' -> out.Append "\\n" |> ignore
        | '\t' -> out.Append "\\t" |> ignore
        | '\r' -> out.Append "\\r" |> ignore
        | '\u000c' -> out.Append "\\f" |> ignore
        | '\u000b' -> out.Append "\\v" |> ignore
        | '\u0008' -> out.Append "\\b" |> ignore
        | '\u0007' -> out.Append "\\a" |> ignore
        | '\u001b' -> out.Append "\\e" |> ignore
        // What would start an interpolation in a double-quoted literal: `#{`, `#$`, `#@`
        | '#' when i + 1 < s.Length && (s[i + 1] = '{' || s[i + 1] = '$' || s[i + 1] = '@') -> out.Append "\\#" |> ignore
        | c when c < ' ' || c = '\u007f' -> out.Append("\\x").Append((int c).ToString("X2")) |> ignore
        | c -> out.Append c |> ignore
    out.Append('"').ToString()

/// `Object#inspect` for parsed JSON values, in Ruby 3.4's format (`{"a" => 1}`).
let rec jsonValueInspect (v: Value) : string =
    match v with
    | Value.Null -> "nil"
    | Value.Bool b -> if b then "true" else "false"
    | Value.Int i -> string i
    | Value.UInt u -> string u
    | Value.Float f -> Campfire.Ruby.Float.floatToS f
    | Value.String s -> stringInspect s
    | Value.Array items -> "[" + String.Join(", ", items |> List.map jsonValueInspect) + "]"
    | Value.Object entries ->
        if List.isEmpty entries then
            "{}"
        else
            "{" + String.Join(", ", entries |> List.map (fun (k, value) -> stringInspect k + " => " + jsonValueInspect value)) + "}"

/// `Object#to_s` of a parsed JSON value, as Nokogiri's `create_element` applies to attribute values.
let jsonValueToS (v: Value) : string =
    match v with
    | Value.String s -> s
    | Value.Null -> ""
    | other -> jsonValueInspect other
