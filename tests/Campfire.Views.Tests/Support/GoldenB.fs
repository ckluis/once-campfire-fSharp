// Port of rust/crates/views/tests/messages_support/mod.rs
/// Shared by the views-B golden tests (`messages_*`, `rooms_*`, `searches_*`): loads a golden from
/// `rust/crates/views/tests/golden/b` (written by `reference-tools/views/b/run.sh`; read here, never written), builds
/// the `ViewContext` the reference request had, and compares our HTML with the reference as the canonical token
/// stream that `reference-tools/views/b/canonical.rb` produces.
module Campfire.Views.Tests.GoldenB

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open Campfire.Tests
open Campfire.Views
open Campfire.Views.Differential.Inputs

let private goldenDir = Repo.path "rust/crates/views/tests/golden/b"

/// Where a difference's full token streams are written (never into `rust/`, which the Rust test wrote them in).
let private diffDir = Repo.path "target/views-b-diff"

let private voidElements = set [ "area"; "base"; "br"; "col"; "embed"; "hr"; "img"; "input"; "keygen"; "link"; "meta"; "source"; "track"; "wbr" ]
let private rawText = set [ "script"; "style"; "textarea"; "title" ]

// ---- the token stream ------------------------------------------------------------------------------------------

let private isWhitespace (c: char) : bool = c = ' ' || c = '\t' || c = '\n' || c = '\r' || c = '\u000C'

let private collapse (text: string) : string =
    let out = StringBuilder(text.Length)
    let mutable inSpace = false
    for c in text do
        if isWhitespace c then
            if not inSpace then out.Append ' ' |> ignore
            inSpace <- true
        else
            out.Append c |> ignore
            inSpace <- false
    out.ToString()

/// Text tokens are `#` and the text; text next to text merges.
let private pushText (out: List<string>) (text: string) : unit =
    let collapsed = collapse text
    if out.Count > 0 && out[out.Count - 1].StartsWith '#' then
        let last = out[out.Count - 1]
        out[out.Count - 1] <- "#" + collapse (last.Substring 1 + collapsed)
    else
        out.Add("#" + collapsed)

/// Rust's `str::find(char::is_whitespace)` stops at Unicode whitespace; the tags are our own output and ASCII.
let private decode (text: string) : string =
    let out = StringBuilder(text.Length)
    let mutable rest = text
    let mutable fin = false
    while not fin do
        match rest.IndexOf '&' with
        | -1 ->
            out.Append rest |> ignore
            fin <- true
        | amp ->
            out.Append(rest, 0, amp) |> ignore
            rest <- rest.Substring amp
            let window = rest.Substring(0, min rest.Length 12)
            match window.IndexOf ';' with
            | -1 ->
                out.Append '&' |> ignore
                rest <- rest.Substring 1
            | semi ->
                let entity = rest.Substring(1, semi - 1)
                let decoded =
                    match entity with
                    | "amp" -> Some "&"
                    | "lt" -> Some "<"
                    | "gt" -> Some ">"
                    | "quot" -> Some "\""
                    | "apos" -> Some "'"
                    | "nbsp" -> Some " "
                    | _ when entity.StartsWith "#x" || entity.StartsWith "#X" ->
                        match Int32.TryParse(entity.Substring 2, Globalization.NumberStyles.AllowHexSpecifier, null) with
                        | true, code when Rune.IsValid code -> Some(Rune(code).ToString())
                        | _ -> None
                    | _ when entity.StartsWith "#" ->
                        match Int32.TryParse(entity.Substring 1) with
                        | true, code when Rune.IsValid code -> Some(Rune(code).ToString())
                        | _ -> None
                    | _ -> None
                match decoded with
                | Some c ->
                    out.Append c |> ignore
                    rest <- rest.Substring(semi + 1)
                | None ->
                    out.Append '&' |> ignore
                    rest <- rest.Substring 1
    out.ToString()

let private isTagBoundary (c: char) : bool = Char.IsWhiteSpace c || c = '>' || c = '/'

/// A start tag from `start`: the canonical token (empty for a forgery token), the tag's name and where it ends.
let private startTag (html: string) (start: int) : string * string * int =
    let mutable i = start + 1
    let mutable nameEnd = i
    while not (isTagBoundary html[nameEnd]) do
        nameEnd <- nameEnd + 1
    let name = html.Substring(i, nameEnd - i).ToLowerInvariant()
    i <- nameEnd
    let attrs = List<string * string>()
    let mutable fin = false
    while not fin do
        while i < html.Length && (Char.IsWhiteSpace html[i] || html[i] = '/') do
            i <- i + 1
        if i >= html.Length || html[i] = '>' then
            i <- i + 1
            fin <- true
        else
            let mutable keyEnd = i
            while not (Char.IsWhiteSpace html[keyEnd] || html[keyEnd] = '=' || html[keyEnd] = '>') do
                keyEnd <- keyEnd + 1
            let key = html.Substring(i, keyEnd - i).ToLowerInvariant()
            i <- keyEnd
            while Char.IsWhiteSpace html[i] do
                i <- i + 1
            let value =
                if html[i] = '=' then
                    i <- i + 1
                    while Char.IsWhiteSpace html[i] do
                        i <- i + 1
                    if html[i] = '"' || html[i] = '\'' then
                        let quote = html[i]
                        let finish = html.IndexOf(quote, i + 1)
                        let value = decode (html.Substring(i + 1, finish - i - 1))
                        i <- finish + 1
                        value
                    else
                        let mutable finish = i
                        while not (Char.IsWhiteSpace html[finish] || html[finish] = '>') do
                            finish <- finish + 1
                        let value = decode (html.Substring(i, finish - i))
                        i <- finish
                        value
                else
                    ""
            if not (attrs.Exists(fun (k, _) -> k = key)) then attrs.Add((key, value))
    // Rails renders forgery tokens; this app doesn't (forgery protection is by `Sec-Fetch-Site`).
    let named (value: string) = attrs.Exists(fun (k, v) -> k = "name" && v = value)
    if (name = "input" && named "authenticity_token") || (name = "meta" && (named "csrf-token" || named "csrf-param")) then
        "", name, i
    else
        let escape (v: string) = v.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;")
        let rendered = attrs |> Seq.map (fun (k, v) -> " " + k + "=\"" + escape v + "\"") |> String.concat ""
        $"<{name}{rendered}>", name, i

/// Tokenizes well-formed HTML (our own output) into the canonical stream.
let tokens (html: string) : List<string> =
    let out = List<string>()
    let mutable i = 0
    let text = StringBuilder()
    // Open elements, so that an end tag closes any elements left open inside it, as the HTML parser does (Rails'
    // blockless `form_with` leaves its form open).
    let openElements = List<string>()
    let flush () =
        if text.Length > 0 then
            pushText out (decode (text.ToString()))
            text.Clear() |> ignore
    while i < html.Length do
        let mutable handled = false
        if html[i] = '<' then
            if String.CompareOrdinal(html, i, "<!--", 0, 4) = 0 then
                flush ()
                i <- (match html.IndexOf("-->", i, StringComparison.Ordinal) with
                      | -1 -> html.Length
                      | e -> e + 3)
                handled <- true
            elif String.CompareOrdinal(html, i, "<!", 0, 2) = 0 then
                flush ()
                i <- (match html.IndexOf('>', i) with
                      | -1 -> html.Length
                      | e -> e + 1)
                handled <- true
            elif String.CompareOrdinal(html, i, "</", 0, 2) = 0 then
                flush ()
                let finish = html.IndexOf('>', i)
                let inner = html.Substring(i + 2, finish - i - 2)
                let name = (inner.Split([| ' '; '\t'; '\n'; '\r'; '\u000C' |], StringSplitOptions.RemoveEmptyEntries) |> Array.tryHead |> Option.defaultValue "").ToLowerInvariant()
                let depth = openElements.FindLastIndex(fun n -> n = name)
                if depth >= 0 then
                    for k in openElements.Count - 1 .. -1 .. depth do
                        out.Add $"</{openElements[k]}>"
                    openElements.RemoveRange(depth, openElements.Count - depth)
                i <- finish + 1
                handled <- true
            elif i + 1 < html.Length && Char.IsAsciiLetter html[i + 1] then
                flush ()
                let token, name, finish = startTag html i
                if token <> "" then out.Add token
                i <- finish
                if not (voidElements.Contains name) then openElements.Add name
                if rawText.Contains name then
                    let close = $"</{name}"
                    let stop =
                        match html.IndexOf(close, i, StringComparison.OrdinalIgnoreCase) with
                        | -1 -> html.Length
                        | e -> e
                    let raw = html.Substring(i, stop - i)
                    if raw <> "" then pushText out (if name = "script" || name = "style" then raw else decode raw)
                    i <- stop
                handled <- true
        if not handled then
            let next =
                match html.IndexOf('<', i) with
                | -1 -> html.Length
                | e -> i + max (e - i) 1
            text.Append(html, i, next - i) |> ignore
            i <- next
    flush ()
    for k in openElements.Count - 1 .. -1 .. 0 do
        out.Add $"</{openElements[k]}>"
    out.RemoveAll(fun t -> t = "#") |> ignore
    out

// ---- regions of a page -----------------------------------------------------------------------------------------

let private tagName (token: string) : string =
    let trimmed = token.TrimStart('<')
    let trimmed = if trimmed.StartsWith '/' then trimmed.TrimStart('/') else trimmed
    let cut = trimmed.IndexOfAny [| ' '; '>' |]
    if cut < 0 then trimmed else trimmed.Substring(0, cut)

let private elementChildren (tokens: List<string>) (isStart: string -> bool) : List<string> =
    match tokens.FindIndex(fun t -> isStart t) with
    | -1 -> List<string>()
    | start ->
        let name = tagName tokens[start]
        let mutable depth = 0
        let mutable result: List<string> | null = null
        let mutable offset = 0
        while isNull result && start + 1 + offset < tokens.Count do
            let token = tokens[start + 1 + offset]
            if token.StartsWith '<' && not (token.StartsWith "</") && tagName token = name && not (voidElements.Contains name) then
                depth <- depth + 1
            elif token = $"</{name}>" then
                if depth = 0 then result <- tokens.GetRange(start + 1, offset) else depth <- depth - 1
            offset <- offset + 1
        match result with
        | null -> tokens.GetRange(start + 1, tokens.Count - start - 1)
        | found -> found

let private trimWhitespace (tokens: List<string>) : List<string> =
    let tokens = List<string>(tokens)
    while tokens.Count > 0 && tokens[0] = "# " do
        tokens.RemoveAt 0
    while tokens.Count > 0 && tokens[tokens.Count - 1] = "# " do
        tokens.RemoveAt(tokens.Count - 1)
    tokens

/// The page's `yield :head`: everything in head after the importmap's module script.
let private headBlock (tokens: List<string>) : List<string> =
    let head = elementChildren tokens (fun t -> t = "<head>")
    let start =
        match head.FindLastIndex(fun t -> t.StartsWith "<script type=\"module\"") with
        | -1 -> head.Count
        | i -> i + 3
    head.GetRange(min start head.Count, head.Count - min start head.Count)

let private regionNames = [ "title"; "head"; "nav"; "main"; "footer"; "sidebar" ]

let private regionsNamed (tokens: List<string>) (names: string list) : (string * List<string>) list =
    let hasMain = tokens.Exists(fun t -> t.StartsWith "<main id=\"main-content\"")
    if not hasMain then
        // A turbo-frame request's minimal layout: compare the body.
        [ "body", trimWhitespace (elementChildren tokens (fun t -> t.StartsWith "<body")) ]
    else
        names
        |> List.map (fun name ->
            let region =
                match name with
                | "title" -> elementChildren tokens (fun t -> t = "<title>")
                | "head" -> headBlock tokens
                | "nav" -> elementChildren tokens (fun t -> t.StartsWith "<nav id=\"nav\"")
                | "main" ->
                    let main = elementChildren tokens (fun t -> t.StartsWith "<main id=\"main-content\"")
                    match main.FindIndex(fun t -> t.StartsWith "<footer id=\"footer\"") with
                    | -1 -> main
                    | footer ->
                        main.RemoveRange(footer, main.Count - footer)
                        main
                | "footer" -> elementChildren tokens (fun t -> t.StartsWith "<footer id=\"footer\"")
                | "sidebar" -> elementChildren tokens (fun t -> t.StartsWith "<aside id=\"sidebar\"")
                | other -> failwith other
            name, trimWhitespace region)

// ---- the reference's expected stream ------------------------------------------------------------------------------

/// A message's "Copy link" button carries the message's path, which the browser makes absolute, rather than an
/// absolute URL built from the request's host (README, Known differences).
let private withRelativeCopyLink (token: string) : string =
    let absolute = "data-copy-to-clipboard-content-value=\"http"
    match token.IndexOf(absolute, StringComparison.Ordinal) with
    | -1 -> token
    | _ when not (token.Contains "title=\"Copy link\"") -> token
    | start ->
        let valueStart = start + "data-copy-to-clipboard-content-value=\"".Length
        let valueEnd = valueStart + token.Substring(valueStart).IndexOf '"'
        let url = token.Substring(valueStart, valueEnd - valueStart)
        let path = url.Substring(url.IndexOf("://", StringComparison.Ordinal) + 3)
        let path = path.Substring(path.IndexOf '/')
        token.Substring(0, start) + $"data-copy-to-clipboard-url-value=\"{path}\"" + token.Substring(valueEnd + 1)

/// The reference's token stream without the CSRF tags and fields Rails renders (this app has none), with the text
/// around a dropped tag merged as the tokenizer would have merged it.
let private withoutForgeryTokens (expected: string list) : List<string> =
    let isToken (t: string) =
        (t.StartsWith "<input " && t.Contains "name=\"authenticity_token\"")
        || (t.StartsWith "<meta " && (t.Contains "name=\"csrf-token\"" || t.Contains "name=\"csrf-param\""))
    let out = List<string>()
    for token in expected |> List.filter (isToken >> not) |> List.map withRelativeCopyLink do
        if token.StartsWith '#' then pushText out (token.Substring 1) else out.Add token
    out

let private assertSame (label: string) (expected: List<string>) (actual: List<string>) : unit =
    if not (Seq.forall2 (=) expected actual) || expected.Count <> actual.Count then
        let common = min expected.Count actual.Count
        let index =
            match Seq.zip expected actual |> Seq.tryFindIndex (fun (e, a) -> e <> a) with
            | Some i -> i
            | None -> common
        let from = max (index - 4) 0
        let show (tokens: List<string>) =
            let first = min from tokens.Count
            let last = min (index + 6) tokens.Count
            String.Join("\n    ", tokens.GetRange(first, last - first))
        let file = label.Replace(' ', '_').Replace('[', '_').Replace(']', '_')
        Directory.CreateDirectory diffDir |> ignore
        File.WriteAllText(Path.Combine(diffDir, $"{file}.expected"), String.Join("\n", expected))
        File.WriteAllText(Path.Combine(diffDir, $"{file}.actual"), String.Join("\n", actual))
        failwith $"{label}: DOM differs at token {index} (expected {expected.Count} tokens, got {actual.Count})\n  expected:\n    {show expected}\n  actual:\n    {show actual}\n  full streams in target/views-b-diff/{file}.*"

// ---- a golden ----------------------------------------------------------------------------------------------------

type Golden =
    { Name: string
      Kind: string
      Json: JsonElement
      /// Overrides the context's `base_url` (the rest of the context is the golden's).
      BaseUrl: string option }

let golden (name: string) : Golden =
    let path = Path.Combine(goldenDir, $"{name}.json")
    let json = JsonDocument.Parse(File.ReadAllText path).RootElement
    { Name = name
      Kind = str (get json "kind")
      Json = json
      BaseUrl = None }

let private expectedTokens (g: Golden) : List<string> =
    withoutForgeryTokens [ for t in (get g.Json "expected").EnumerateArray() -> nonNull (t.GetString()) ]

type Golden with
    member g.Input: JsonElement = get g.Json "input"

    member g.InputAt(key: string) : JsonElement = get g.Input key

    /// The `ViewContext` the reference request had.
    member g.Context: ViewContext =
        let context = get g.Json "context"
        let assets =
            [ for p in (get context "assets").EnumerateObject() -> p.Name, nonNull (p.Value.GetString()) ] |> dict
        let assetPath (logical: string) =
            match assets.TryGetValue logical with
            | true, path -> path
            | _ -> failwith $"{g.Name}: unknown asset {logical}"
        let user = get context "current_user"
        let p = get context "platform"
        let flag key = bool (get p key)
        { CurrentUser =
            (if user.ValueKind = JsonValueKind.Object then
                 Some
                     { Id = int64Of (get user "id")
                       Name = str (get user "name")
                       Administrator = bool (get user "administrator")
                       Bot = bool (get user "bot")
                       AvatarUrl = str (get user "avatar_url") }
             else
                 None)
          Account =
            { Name = str (get (get context "account") "name")
              LogoUrl = str (get (get context "account") "logo_url")
              HasLogo = bool (get (get context "account") "has_logo") }
          FlashNotice = None
          FlashAlert = None
          Platform =
            { Ios = flag "ios"
              Android = flag "android"
              Mac = flag "mac"
              Windows = flag "windows"
              Chrome = flag "chrome"
              Firefox = flag "firefox"
              Safari = flag "safari"
              Edge = flag "edge"
              Mobile = flag "mobile"
              Desktop = flag "desktop"
              AppleMessages = flag "apple_messages"
              Browser = str (get p "browser")
              OperatingSystem = str (get p "operating_system") }
          VapidPublicKey = None
          AssetPath = assetPath
          // Marks where the layout ends and the page's head block starts (see `headBlock`).
          ImportmapTags = "<script type=\"module\">import \"application\"</script>"
          StylesheetTags = ""
          CustomStyles = None
          CableUrl = "/cable"
          BaseUrl = defaultArg g.BaseUrl (str (get context "base_url"))
          RequestUrl = ""
          Referrer = None
          LastRoomVisitedId =
            (match get context "last_room_visited_id" with
             | v when v.ValueKind = JsonValueKind.Number -> Some(v.GetInt64())
             | _ -> None)
          AppVersion = "0" }

    /// Asserts DOM parity for a template rendered without a layout against a reference page: the page's main content
    /// (or a frame layout's body) is compared with the whole render.
    member g.AssertContent(actualHtml: string) : unit =
        let expected = expectedTokens g
        let expected =
            if g.Kind = "page" then
                match regionsNamed expected [ "main" ] with
                | (_, tokens) :: _ -> tokens
                | [] -> List<string>()
            else
                expected
        assertSame g.Name (trimWhitespace expected) (trimWhitespace (tokens actualHtml))

    /// Asserts DOM parity. Pages compare the regions the page fills (title, the head block, nav, main content, footer,
    /// sidebar); fragments compare everything.
    member g.AssertDom(actualHtml: string) : unit =
        let expected = expectedTokens g
        let actual = tokens actualHtml
        if g.Kind = "page" then
            let regions = regionsNamed expected regionNames
            if regions.IsEmpty then failwith $"{g.Name}: no regions in reference"
            let actualRegions = regionsNamed actual (regions |> List.map fst)
            for (name, expectedRegion), (_, actualRegion) in List.zip regions actualRegions do
                assertSame $"{g.Name} [{name}]" expectedRegion actualRegion
        else
            assertSame g.Name (trimWhitespace expected) (trimWhitespace actual)
