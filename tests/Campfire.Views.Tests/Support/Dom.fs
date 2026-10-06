// Port of rust/crates/views/tests/support/dom.rs
/// DOM normalization for parity tests: two documents are the same page when their token streams
/// match after sorting attributes, merging adjacent text, collapsing whitespace runs to one space,
/// and dropping the forgery tokens Rails renders and this app doesn't. Whitespace-only text is kept
/// (as a single space) because it can affect inline layout.
///
/// The tokenizer is the one Campfire.RichText ports from html5ever, as the Rust test uses html5ever's.
module Campfire.Views.Tests.Dom

open System
open System.Collections.Generic
open System.Text
open Campfire.RichText.Html5ever

/// Rust's `{:?}` of a string, as far as attribute values need: quoted, with the escapes it writes.
let private debugString (value: string) : string =
    let out = StringBuilder("\"")
    for c in value do
        match c with
        | '"' -> out.Append "\\\"" |> ignore
        | '\\' -> out.Append "\\\\" |> ignore
        | '\n' -> out.Append "\\n" |> ignore
        | '\r' -> out.Append "\\r" |> ignore
        | '\t' -> out.Append "\\t" |> ignore
        | '\000' -> out.Append "\\0" |> ignore
        | c -> out.Append c |> ignore
    out.Append('"').ToString()

let private collapseWhitespace (text: string) : string =
    let out = StringBuilder()
    let mutable inSpace = false
    for c in text do
        if c = ' ' || c = '\t' || c = '\n' || c = '\u000C' || c = '\r' then
            if not inSpace then out.Append ' ' |> ignore
            inSpace <- true
        else
            out.Append c |> ignore
            inSpace <- false
    out.ToString()

/// The CSRF meta tags and hidden token fields Rails renders. This app protects against forgery by
/// `Sec-Fetch-Site` instead, so its pages have none (all are void elements: no end tag to drop).
let private isForgeryToken (tag: string) (attrs: (string * string) list) : bool =
    let named (name: string) = attrs |> List.exists (fun (k, v) -> k = "name" && v = name)
    (tag = "input" && named "authenticity_token") || (tag = "meta" && (named "csrf-token" || named "csrf-param"))

type private Sink() =
    inherit TokenSink()
    let lines = List<string>()
    let text = StringBuilder()

    member _.Lines = lines

    member _.FlushText() =
        if text.Length > 0 then
            let collapsed = collapseWhitespace (text.ToString())
            text.Clear() |> ignore
            lines.Add $"\"{collapsed}\""

    override this.ProcessTag(tag: Tag) =
        let name = tag.Name
        let attrs = [ for a in tag.Attrs -> QualName.qualified a.Name, a.Value ]
        // Dropped before flushing, so the text on either side merges as if it weren't there.
        if tag.Kind = StartTag && isForgeryToken name attrs then
            Continue
        else
            this.FlushText()
            match tag.Kind with
            | StartTag ->
                let sorted = attrs |> List.sortWith (fun (a, av) (b, bv) -> match String.CompareOrdinal(a, b) with 0 -> String.CompareOrdinal(av, bv) | c -> c)
                let rendered = sorted |> List.map (fun (k, v) -> $" {k}={debugString v}") |> String.concat ""
                lines.Add $"<{name}{rendered}>"
            | EndTag -> lines.Add $"</{name}>"
            Continue

    override _.ProcessComment(_) = ()
    override _.ProcessChars(s) = text.Append s |> ignore
    override _.ProcessNull() = text.Append '\000' |> ignore
    override _.ProcessEof() = ()
    override this.ProcessDoctype() =
        this.FlushText()
        lines.Add "<!DOCTYPE>"
    override _.ProcessParseError() = ()
    override _.End() = ()
    override _.AdjustedCurrentNodePresentButNotInHtmlNamespace() = false

/// The normalized token lines of an HTML document or fragment.
let normalizeHtml (html: string) : string list =
    let sink = Sink()
    let tokenizer = Tokenizer(sink, Data, -1)
    tokenizer.Feed(Input html)
    tokenizer.End()
    sink.FlushText()
    List.ofSeq sink.Lines

/// A readable report of the first differences between two normalized documents, or `None`.
let diff (expected: string list) (actual: string list) : string option =
    if expected = actual then
        None
    else
        let e, a = Array.ofList expected, Array.ofList actual
        let first =
            Seq.zip e a |> Seq.tryFindIndex (fun (x, y) -> x <> y) |> Option.defaultValue (min e.Length a.Length)
        let from = max 0 (first - 4)
        let report = StringBuilder()
        report.AppendLine $"first difference at token {first} (expected {e.Length} tokens, got {a.Length})" |> ignore
        for index in from .. first + 5 do
            let x = if index < e.Length then e[index] else "<end>"
            let y = if index < a.Length then a[index] else "<end>"
            let marker = if x = y then " " else "!"
            report.AppendLine $"{marker} rails: {x}" |> ignore
            report.AppendLine $"{marker} fsharp:  {y}" |> ignore
        Some(report.ToString())
