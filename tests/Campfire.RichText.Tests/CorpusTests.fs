// Port of rust/crates/richtext/tests/corpus.rs
//
// Differential test against the Rails pipeline: corpus/expected.json is produced by
// reference-tools/richtext/run.sh in the campfire-reference image. Every output is also checked
// against security properties that don't depend on the oracle.
module Campfire.RichText.Tests.CorpusTests

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open Xunit
open Campfire.RichText
open Campfire.RichText.Unicode
open Campfire.RichText.Tests.Support
open Campfire.RichText.Tests.CorpusResolver
open Campfire.Tests

let private str (e: JsonElement) : string = e.GetString() |> nonNull

/// RICHTEXT_CORPUS points at a larger, uncommitted corpus (see reference-tools/richtext/run.sh)
let private corpusPath, private usesCommittedCorpus =
    match Environment.GetEnvironmentVariable "RICHTEXT_CORPUS" with
    | null
    | "" -> Repo.path "tests/Campfire.RichText.Tests/corpus/expected.json", true
    | path -> path, false

let private load () : JsonDocument =
    if not (File.Exists corpusPath) then failwith "run reference-tools/richtext/run.sh to generate the corpus"
    JsonDocument.Parse(File.ReadAllText corpusPath)

/// A DOM normalization for reporting near misses: parse, drop whitespace-only text, sort attributes.
let private normalizedDom (html: string) : string =
    let dom = Dom()
    match dom.ParseFragment html with
    | Error _ -> $"unparseable: {html}"
    | Ok root ->
        let out = StringBuilder()
        let rec walk (node: NodeId) =
            for child in dom.Children node do
                match dom.Text child with
                | ValueSome text ->
                    let collapsed = text.Split([| ' '; '\t'; '\n'; '\r'; '\u000c'; '\u000b' |], StringSplitOptions.RemoveEmptyEntries) |> String.concat " "
                    if collapsed.Length > 0 then out.Append collapsed |> ignore
                | ValueNone ->
                    match dom.LocalName child with
                    | ValueSome name ->
                        let attrs = dom.Attrs child |> List.sort
                        out.Append($"<{name} {attrs}>") |> ignore
                        walk child
                        out.Append($"</{name}>") |> ignore
                    | ValueNone -> ()
        walk root
        out.ToString()

// --- Deliberate differences ----------------------------------------------------------------------

type private ScanState =
    | Text
    | Tag
    | Value

/// Rails' presentation as the port renders it on purpose (see "Known differences" in README.md):
/// `<` and `>` are escaped in attribute values, and the links rails_autolink inserted inside an
/// attribute value (its stored XSS) are left as the text they replaced; `name` attributes are dropped.
let private withPortDivergences (rails: string) : string =
    let insertedLink = "<a target=\"_blank\" href=\""
    let nameAttr = " name=\""
    let out = StringBuilder(rails.Length)
    let mutable state = Text
    let mutable pos = 0
    let startsWith (literal: string) = pos + literal.Length <= rails.Length && String.CompareOrdinal(rails, pos, literal, 0, literal.Length) = 0
    while pos < rails.Length do
        let c = rails[pos]
        if state = Value && startsWith insertedLink then
            let textStart = rails.IndexOf("\">", pos, StringComparison.Ordinal) + 2
            let textEnd = rails.IndexOf("</a>", textStart, StringComparison.Ordinal)
            out.Append(rails.Substring(textStart, textEnd - textStart).Replace(">", "&gt;")) |> ignore
            pos <- textEnd + 4
        elif state = Tag && startsWith nameAttr then
            let valueEnd = rails.IndexOf('"', pos + nameAttr.Length)
            pos <- valueEnd + 1
        else
            match state, c with
            | Text, '<' -> state <- Tag
            | Tag, '>' -> state <- Text
            | Tag, '"' -> state <- Value
            | Value, '"' -> state <- Tag
            | _ -> ()
            match state, c with
            | Value, '<' -> out.Append "&lt;" |> ignore
            | Value, '>' -> out.Append "&gt;" |> ignore
            | _ -> out.Append c |> ignore
            pos <- pos + 1
    out.ToString()

[<Fact>]
let ``port_divergences_apply_to_attribute_values_only`` () =
    Assert.Equal(
        "<p title=\"a&gt;b http://x.test/\">c > <a target=\"_blank\" href=\"http://y.test/\">y</a></p>",
        withPortDivergences
            "<p title=\"a>b <a target=\"_blank\" href=\"http://x.test/\">http://x.test/</a>\">c > <a target=\"_blank\" href=\"http://y.test/\">y</a></p>"
    )
    Assert.Equal("<a title=\"name=\">n</a>", withPortDivergences "<a name=\"x y\" title=\"name=\">n</a>")

// --- Security assertions -------------------------------------------------------------------------

let private dangerousElements =
    set
        [ "script"; "style"; "iframe"; "frame"; "frameset"; "object"; "embed"; "applet"; "base"; "meta"; "link"; "form"; "input"; "button"
          "textarea"; "select"; "svg"; "math"; "template"; "noscript"; "xmp"; "plaintext"; "noembed" ]

let private urlAttributes =
    set [ "href"; "src"; "action"; "formaction"; "poster"; "cite"; "background"; "xlink:href"; "srcset"; "data" ]

let private isControl (c: char) = Char.IsControl c

let private dangerousUrl (value: string) : bool =
    // What a browser would see: control characters and whitespace ignored, case folded
    let cleaned = Chars.toLowercase (String(value |> Seq.filter (fun c -> not (isControl c) && not (Chars.isWhitespace c)) |> Seq.toArray))
    if cleaned.StartsWith("javascript:", StringComparison.Ordinal) || cleaned.StartsWith("vbscript:", StringComparison.Ordinal) || cleaned.StartsWith("livescript:", StringComparison.Ordinal) then
        true
    elif cleaned.StartsWith("data:", StringComparison.Ordinal) then
        // A data: URL is only dangerous as markup or script; an unparseable media type is text/plain
        let rest = cleaned.Substring 5
        let mediatype = (((rest.Split ',').[0]).Split ';').[0]
        let token (s: string) = s.Length > 0 && s |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || "!#$%&'*+-.^_`|~".IndexOf c >= 0)
        let valid =
            match mediatype.IndexOf '/' with
            | -1 -> false
            | slash -> token (mediatype.Substring(0, slash)) && token (mediatype.Substring(slash + 1))
        valid && [ "html"; "xml"; "svg"; "script"; "ecmascript" ] |> List.exists (fun kind -> mediatype.Contains(kind, StringComparison.Ordinal))
    else
        false

/// Parses rendered output the way a browser would and returns the security violations in it.
let private securityViolations (html: string) (allowStyle: bool) : string list =
    let dom = Dom()
    match dom.ParseFragment html with
    | Error _ -> [ "unparseable output" ]
    | Ok root ->
        let violations = ResizeArray<string>()
        for node in dom.Descendants root do
            match dom.LocalName node with
            | ValueNone -> ()
            | ValueSome name ->
                if dangerousElements.Contains name then violations.Add $"<{name}> element"
                // Nothing auto_link inserts may escape its own sanitizer's allowlist
                let allowed = SafeList.autoLink
                if not (allowed.AllowsTag name) then violations.Add $"<{name}> not in the allowlist"
                for (attr, value) in dom.Attrs node do
                    if not (allowed.AllowsAttribute attr) && attr <> "target" && not (attr = "style" && allowStyle) then
                        violations.Add $"{attr} on <{name}> not in the allowlist"
                    let lower = Chars.toLowercase attr
                    if lower.StartsWith("on", StringComparison.Ordinal) then violations.Add $"{attr} attribute on <{name}>"
                    if urlAttributes.Contains lower && dangerousUrl value then violations.Add $"{attr}=\"{value}\" on <{name}>"
                    if lower = "style" && not allowStyle then violations.Add $"style on <{name}>"
                    if lower.StartsWith("data-", StringComparison.Ordinal) && lower <> "data-language" then violations.Add $"{attr} on <{name}>"
        List.ofSeq violations

// --- The comparison ------------------------------------------------------------------------------

type private Tally() =
    member val Exact = 0 with get, set
    member val DomEqual = 0 with get, set
    member val Mismatched = ResizeArray<string>()

    member this.Record(label: string, expected: string, actual: string) =
        if expected = actual then
            this.Exact <- this.Exact + 1
        elif normalizedDom expected = normalizedDom actual then
            this.DomEqual <- this.DomEqual + 1
            this.Mismatched.Add $"{label} (DOM-equal)\n  expected: {expected}\n  actual:   {actual}"
        else
            this.Mismatched.Add $"{label}\n  expected: {expected}\n  actual:   {actual}"

/// `outcome_str`: Error for a recorded exception, else the recorded string (None for nil).
let private outcome (v: JsonElement) : Result<string option, string> =
    match v.TryGetProperty "error" with
    | true, e -> Error(str e)
    | _ ->
        match (v.GetProperty "ok").ValueKind with
        | JsonValueKind.String -> Ok(Some(str (v.GetProperty "ok")))
        | _ -> Ok None

let private showOption (o: string option) : string =
    match o with
    | Some s -> $"Some({s})"
    | None -> "None"

let private tallyOf (tallies: IDictionary<string, Tally>) (kind: string) : Tally =
    match tallies.TryGetValue kind with
    | true, t -> t
    | _ ->
        let t = Tally()
        tallies[kind] <- t
        t

[<Fact>]
let ``corpus_matches_rails`` () =
    use corpus = load ()
    let json = corpus.RootElement
    let resolver = resolverOf json
    let tallies = SortedDictionary<string, Tally>()
    let security = ResizeArray<string>()
    let cases = json.GetProperty("cases").EnumerateArray() |> Seq.toArray

    for case in cases do
        let name = str (case.GetProperty "name")
        let body = str (case.GetProperty "body")
        let ctx =
            render
                resolver
                (match (case.TryGetProperty "host") with
                 | true, h when h.ValueKind = JsonValueKind.String -> Some(str h)
                 | _ -> None)

        // presentation
        let expected =
            match outcome (case.GetProperty "presentation") with
            | Ok html -> Presentation.Html(withPortDivergences (defaultArg html ""))
            | Error _ -> Presentation.Unrenderable
        let actual = ActionText.presentMessage body ctx
        let label = $"[presentation] {name}"
        // Deliberate: a missing attachable Rails can't find a partial for (a deleted user's
        // mention) renders ☒ instead of raising and blanking the message.
        let missingPartial =
            match case.TryGetProperty "presentation_raised_message" with
            | true, m when m.ValueKind = JsonValueKind.String -> (str m).Contains("to_missing_attachable_partial_path", StringComparison.Ordinal)
            | _ -> false
        match expected, actual with
        | Presentation.Html _, Presentation.Html a when missingPartial ->
            Assert.True(a.Contains '☒', $"{label}: {a}")
            (tallyOf tallies "presentation").Exact <- (tallyOf tallies "presentation").Exact + 1
        | Presentation.Html e, Presentation.Html a -> (tallyOf tallies "presentation").Record(label, e, a)
        | _ -> (tallyOf tallies "presentation").Record(label, $"{expected}", $"{actual}")
        match actual with
        | Presentation.Html html ->
            for v in securityViolations html false do
                security.Add $"{name}: {v}"
        | _ -> ()

        // plain text
        let actual = ActionText.toPlainText body ctx
        let label = $"[plain_text] {name}"
        match outcome (case.GetProperty "plain_text"), actual with
        | Ok e, Ok a -> (tallyOf tallies "plain_text").Record(label, defaultArg e "", a)
        | Error _, Error _ -> (tallyOf tallies "plain_text").Exact <- (tallyOf tallies "plain_text").Exact + 1
        | e, a -> (tallyOf tallies "plain_text").Record(label, $"{e}", $"{a}")

        // editable value
        let actual = ActionText.editableValue body ctx
        let label = $"[editable] {name}"
        // Deliberate: missing attachables leave the editor, where Rails raises.
        let missingInEditor =
            match (case.GetProperty "editable").TryGetProperty "message" with
            | true, m when m.ValueKind = JsonValueKind.String -> (str m).Contains("MissingAttachable", StringComparison.Ordinal)
            | _ -> false
        match outcome (case.GetProperty "editable"), actual with
        | Error _, Ok _ when missingInEditor -> (tallyOf tallies "editable").Exact <- (tallyOf tallies "editable").Exact + 1
        | Ok e, Ok a -> (tallyOf tallies "editable").Record(label, showOption e, showOption a)
        | Error _, Error _ -> (tallyOf tallies "editable").Exact <- (tallyOf tallies "editable").Exact + 1
        | e, a -> (tallyOf tallies "editable").Record(label, $"{e}", $"{a}")

        // mentioned users
        let actual = ActionText.mentionedUsers body ctx |> Result.map (List.map (fun u -> u.Id))
        let expected =
            match (case.GetProperty "mentioned").TryGetProperty "ok" with
            | true, ids -> Ok [ for i in ids.EnumerateArray() -> i.GetInt64() ]
            | _ -> Error()
        let label = $"[mentioned] {name}"
        match expected, actual with
        | Ok e, Ok a -> (tallyOf tallies "mentioned").Record(label, $"{e}", $"{a}")
        | Error _, Error _ -> (tallyOf tallies "mentioned").Exact <- (tallyOf tallies "mentioned").Exact + 1
        | _ -> (tallyOf tallies "mentioned").Record(label, $"{expected}", $"{actual}")

    // opengraph URL checks
    let webUrls = json.GetProperty("web_urls").EnumerateArray() |> Seq.toArray
    for url in webUrls do
        let value = str (url.GetProperty "value")
        let host = str (url.GetProperty "host")
        let actual = Attachables.webUrl (ValueSome value) host
        let label = $"[web_url] {value}"
        match outcome (url.GetProperty "result"), actual with
        | Ok e, Ok a -> (tallyOf tallies "web_url").Record(label, showOption e, showOption a)
        | Error _, Error _ -> (tallyOf tallies "web_url").Exact <- (tallyOf tallies "web_url").Exact + 1
        | e, a -> (tallyOf tallies "web_url").Record(label, $"{e}", $"{a}")

    let report = StringBuilder()
    let mutable failed = false
    for KeyValue(kind, tally) in tallies do
        let total = tally.Exact + tally.Mismatched.Count
        report.AppendLine $"{kind}: {total} cases, {tally.Exact} byte-identical, {tally.DomEqual} DOM-equal only, {tally.Mismatched.Count - tally.DomEqual} different" |> ignore
        for m in tally.Mismatched do
            report.AppendLine $"  MISMATCH {m}" |> ignore
            failed <- true
    report.AppendLine $"security: {cases.Length} outputs checked, {security.Count} violations" |> ignore
    for v in security do
        report.AppendLine $"  VIOLATION {v}" |> ignore

    // Every case of the committed corpus runs through every check: a skipped one fails here.
    if usesCommittedCorpus then
        Assert.Equal(658, cases.Length)
        Assert.Equal(54, webUrls.Length)
        for kind in [ "presentation"; "plain_text"; "editable"; "mentioned" ] do
            Assert.Equal(658, (tallyOf tallies kind).Exact + (tallyOf tallies kind).Mismatched.Count)
        Assert.Equal(54, (tallyOf tallies "web_url").Exact + (tallyOf tallies "web_url").Mismatched.Count)
    Assert.True(security.Count = 0, $"security assertions failed\n{report}")
    Assert.False(failed, $"differences from the Rails pipeline\n{report}")

/// The oracle's own outputs must pass the security assertions too: two implementations can agree
/// on something unsafe. (They do: rails_autolink breaks out of attribute values, which is why the
/// port diverges there, so the assertions run on the output as the port means to render it.)
[<Fact>]
let ``rails_outputs_pass_security_assertions`` () =
    use corpus = load ()
    let violations = ResizeArray<string>()
    let mutable checkedOutputs = 0
    for case in corpus.RootElement.GetProperty("cases").EnumerateArray() do
        match outcome (case.GetProperty "presentation") with
        | Ok(Some html) ->
            checkedOutputs <- checkedOutputs + 1
            for v in securityViolations (withPortDivergences html) false do
                let name = str (case.GetProperty "name")
                violations.Add $"{name}: {v}"
        | _ -> ()
    Assert.True(checkedOutputs > 0)
    Assert.True(violations.Count = 0, String.Join("\n", violations))

/// The security gate has to catch what it's for, or passing it means nothing.
[<Fact>]
let ``security_assertions_catch_planted_defects`` () =
    for (html, what) in
        [ "<script>alert(1)</script>", "script element"
          "<p onclick=\"x()\">p</p>", "event handler"
          "<a href=\"java\tscript:alert(1)\">x</a>", "javascript URL"
          "<a href=\" JAVASCRIPT:alert(1)\">x</a>", "javascript URL"
          "<img src=\"data:text/html,<script>\">", "data URL"
          "<span style=\"color: red\">x</span>", "style attribute"
          "<svg><a xlink:href=\"javascript:1\">x</a></svg>", "svg"
          "<span data-controller=\"x\">x</span>", "data attribute"
          "<p title=\"a\" _blank\"=\"\">x</p>", "attribute outside the allowlist"
          "<details>x</details>", "element outside the allowlist" ] do
        Assert.True(not (List.isEmpty (securityViolations html false)), $"missed {what} in {html}")
    Assert.True(List.isEmpty (securityViolations "<p><a href=\"https://example.com\">x</a><img src=\"/a.png\"></p>" false))
