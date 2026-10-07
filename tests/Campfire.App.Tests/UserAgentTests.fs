// Port of the tests of rust/crates/campfire/src/concerns/user_agent.rs
module Campfire.App.Tests.UserAgentTests

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open Xunit
open Campfire.App
open Campfire.App.UserAgent
open Campfire.Ruby
open Campfire.Tests

let vectors () : JsonElement = (Repo.vector "campfire_user_agents").RootElement

let str (element: JsonElement) : string =
    match element.GetString() with
    | null -> failwith "not a string"
    | text -> text

/// What a value of the vectors is, to compare: a string, a bool or null.
let expectedValue (element: JsonElement) : string option =
    match element.ValueKind with
    | JsonValueKind.String -> Some(str element)
    | JsonValueKind.True -> Some "true"
    | JsonValueKind.False -> Some "false"
    | JsonValueKind.Null -> None
    | other -> failwith $"unexpected {other}"

/// Compares a Ruby value (`{"error": ...}` when it raised) with ours.
let check (failures: List<string>) (context: string) (expected: JsonElement) (actual: Rb<string option>) : unit =
    let raised = expected.ValueKind = JsonValueKind.Object && expected.TryGetProperty("error") |> fst
    let ok =
        match raised, actual with
        | true, Error Raised -> true
        | false, Ok value -> value = expectedValue expected
        | _ -> false
    if not ok then failures.Add $"{context}: expected {expected}, got {actual}"

let text (value: string option) : string option = value

let flag (value: bool) : string option = Some(if value then "true" else "false")

let userAgentOf (case: JsonElement) : string | null =
    match case.GetProperty("ua").ValueKind with
    | JsonValueKind.Null -> null
    | _ -> str (case.GetProperty "ua")

[<Fact>]
let ``matches the gem`` () =
    let failures = List<string>()
    for case in (vectors ()).GetProperty("user_agents").EnumerateArray() do
        let ua = match userAgentOf case with null -> "" | ua -> ua
        let agent = parse ua
        let label = case.GetProperty("ua").ToString()
        check failures $"{label} browser" (case.GetProperty "browser") (Agent.tryBrowser agent |> Result.map text)
        check failures $"{label} version" (case.GetProperty "version") (Agent.tryVersion agent |> Result.map (fun v -> text (v |> Option.map (fun v -> v.String))))
        check failures $"{label} platform" (case.GetProperty "platform") (Agent.tryPlatform agent |> Result.map text)
        check failures $"{label} os" (case.GetProperty "os") (Agent.tryOs agent |> Result.map text)
        check failures $"{label} bot" (case.GetProperty "bot") (Ok(flag (Agent.isBot agent)))
        check failures $"{label} mobile" (case.GetProperty "mobile") (Agent.tryMobile agent |> Result.map flag)
    Assert.True(failures.Count = 0, $"{failures.Count} mismatches:\n{String.Join('\n', failures)}")

[<Fact>]
let ``versions match the gem`` () =
    let failures = List<string>()
    for case in (vectors ()).GetProperty("versions").EnumerateArray() do
        let version = Version.create (str (case.GetProperty "string"))
        let toA =
            Version.toA version
            |> List.map (fun segment ->
                match segment with
                | Int i -> $"i:{i}"
                | Str s -> $"s:{s}")
        let expectedToA = [ for item in case.GetProperty("to_a").EnumerateArray() -> str item ]
        if Version.isNil version <> case.GetProperty("nil").GetBoolean() || toA <> expectedToA then
            failures.Add $"{case}: nil={Version.isNil version} to_a={toA}"
    for case in (vectors ()).GetProperty("comparisons").EnumerateArray() do
        let a = Version.create (str (case.GetProperty "a"))
        let b = Version.create (str (case.GetProperty "b"))
        let cmp = Version.rubyCmp a b
        if cmp <> case.GetProperty("cmp").GetInt32()
           || Version.lessThan a b <> case.GetProperty("lt").GetBoolean()
           || (a.String = b.String) <> case.GetProperty("eq").GetBoolean() then
            failures.Add $"{case}: cmp={cmp} lt={Version.lessThan a b} eq={a.String = b.String}"
    Assert.True(failures.Count = 0, $"{failures.Count} mismatches:\n{String.Join('\n', failures)}")

[<Fact>]
let ``blank user agents parse as the default`` () =
    let agent = parse "  "
    Assert.Equal("Mozilla", Agent.browser agent)
    Assert.Equal("4.0", (Agent.version agent).String)

/// Every User-Agent in the vectors: the corpus in reference-tools/campfire/user_agents.rb.
let private userAgents () : string option list =
    [ for case in (vectors ()).GetProperty("user_agents").EnumerateArray() ->
          match userAgentOf case with
          | null -> None
          | ua -> Some ua ]

/// The straightforward code the fast paths replaced, kept to check them against.
module Straightforward =
    let private toRunes (text: string) : int[] = [| for rune in text.EnumerateRunes() -> rune.Value |]

    let private ofRunes (runes: seq<int>) : string =
        let out = StringBuilder()
        for rune in runes do
            out.Append(Char.ConvertFromUtf32 rune) |> ignore
        out.ToString()

    let private isSpace (c: int) : bool = c < 128 && isRubySpace (char c)

    /// `matchProduct` over code points (Rust's `Vec<char>`), the way the char-by-char matcher does it.
    let private matchProduct (s: int[]) : (int * (string * string * string[] option)) option =
        let isProductChar (c: int) = c <> int '/' && not (isSpace c)
        let quotes = s |> Array.takeWhile (fun c -> c = int '\'' || c = int '"') |> Array.length
        let start =
            if quotes < s.Length && isProductChar s[quotes] then Some quotes
            elif quotes > 0 then Some(quotes - 1)
            else None
        match start with
        | None -> None
        | Some start ->
            let mutable i = start + 1
            while i < s.Length && isProductChar s[i] do
                i <- i + 1
            let product = ofRunes s[start .. i - 1]
            if i < s.Length && s[i] = int '/' then i <- i + 1
            let versionStart = i
            while i < s.Length && not (isSpace s[i]) && s[i] <> int ',' do
                i <- i + 1
            let version = ofRunes s[versionStart .. i - 1]
            let mutable comment = None
            if i + 1 < s.Length && isSpace s[i] && s[i + 1] = int '(' then
                match Array.tryFindIndex (fun c -> c = int ')') s[i + 2 ..] with
                | Some close ->
                    comment <- Some(ofRunes s[i + 2 .. i + 2 + close - 1])
                    i <- i + 2 + close + 1
                | None -> ()
            elif ofRunes (Array.truncate 10 s[i..]) = ",gzip(gfe)" then
                i <- i + 10
            Some(i, (product, version, comment |> Option.map (fun comment -> rubySplit comment "; ")))

    /// `parse`'s product loop over chars, collecting the stripped rest after each product.
    let products (userAgent: string) : (string * string * string[] option) list =
        let mutable rest = toRunes (if Ruby.strip userAgent = "" then "Mozilla/4.0 (compatible)" else userAgent)
        let products = List<string * string * string[] option>()
        let mutable going = true
        while going do
            match matchProduct rest with
            | Some(length, product) ->
                products.Add product
                rest <- toRunes (Ruby.strip (ofRunes rest[length..]))
            | None -> going <- false
        List.ofSeq products

    let sameIgnoringCase (a: string) (b: string) : bool =
        Campfire.Views.Helpers.Application.toLowercase a = Campfire.Views.Helpers.Application.toLowercase b

    let containsIgnoringCase (haystack: string) (needle: string) : bool =
        (Campfire.Views.Helpers.Application.toLowercase haystack).Contains(needle, StringComparison.Ordinal)

    let webkitCommentVersion (comment: string) : string option =
        let runes = toRunes comment
        let name = ofRunes (Array.truncate 11 runes)
        if Array.length (toRunes name) <> 11 || Campfire.Views.Helpers.Application.toLowercase name <> "applewebkit" then
            None
        else
            match comment.Substring(name.Length) with
            | rest when rest.StartsWith '/' ->
                let tail = rest.Substring 1
                let digits = tail |> Seq.takeWhile (fun c -> Char.IsAsciiDigit c || c = '.') |> Seq.length
                if digits > 0 then Some(tail.Substring(0, digits)) else None
            | _ -> None

/// Each User-Agent, and each of its prefixes (the truncated and unterminated shapes).
[<Fact>]
let ``scans products like the char by char matcher`` () =
    for userAgent in userAgents () do
        let userAgent = defaultArg userAgent ""
        // Prefixes at code point boundaries, as Rust's `char_indices` gives them.
        let boundaries = [ for i in 0 .. userAgent.Length - 1 do if not (Char.IsLowSurrogate userAgent[i]) then i ]
        let prefixes = [ for i in boundaries -> userAgent.Substring(0, i) ] @ [ userAgent ]
        for prefix in prefixes do
            let fast = Agent.productFields (parse prefix)
            let slow = Straightforward.products prefix
            Assert.True((fast = slow), $"{prefix}")

/// Characters whose lowercase is ASCII though they aren't, or is longer than they are.
let private specialCases = [ "K"; "k"; "K"; "İ"; "i̇"; "I"; "ß"; "SS"; "Σ"; "σ"; "ς"; "ÀPPLEWEBKIT" ]

/// Every product name the test agents parse to, and the special cases.
let private productNames () : Set<string> =
    let names = [ for userAgent in userAgents () do for (name, _, _) in Agent.productFields (parse (defaultArg userAgent "")) -> name ]
    Set.ofList (names @ specialCases)

/// Every comment and whole test agent, and the product names.
let private texts () : Set<string> =
    let mutable texts = productNames ()
    for userAgent in userAgents () |> List.choose id do
        for (_, _, comment) in Agent.productFields (parse userAgent) do
            for c in defaultArg comment [||] do
                texts <- Set.add c texts
        texts <- Set.add userAgent texts
    texts

[<Fact>]
let ``compares ignoring case like lowercasing both`` () =
    // What `detect_product` looks for; Gecko's `version` also looks for the first product.
    let lookedFor =
        [ "Chrome-Lighthouse"; "Iron"; "PaleMoon"; "Firefox"; "Camino"; "Iceweasel"; "Seamonkey"; "MicroMessenger" ]
        @ [ "CriOs"; "chrome"; "iTunes"; "Version"; "OPR"; "NSPlayer"; "Mobile"; "applewebkit" ]
    let names = productNames ()
    let lookedFor = lookedFor @ List.ofSeq names
    for name in names do
        for other in lookedFor do
            Assert.True(
                (sameIgnoringCase name other = Straightforward.sameIgnoringCase name other),
                $"{name} {other}"
            )

[<Fact>]
let ``searches ignoring case like lowercasing the haystack`` () =
    for haystack in texts () do
        for needle in [ "bot"; "micromessenger"; "facebookexternalhit"; "twitterbot"; "k" ] do
            let expected = Straightforward.containsIgnoringCase haystack needle
            Assert.True((containsIgnoringCase haystack needle = expected), $"{haystack} {needle}")

/// Each comment, and each of its suffixes.
[<Fact>]
let ``reads webkit comment versions like lowercasing the name`` () =
    for text in texts () do
        let suffixes = [ for i in 0 .. text.Length - 1 do if not (Char.IsLowSurrogate text[i]) then text.Substring i ]
        for comment in suffixes do
            Assert.True((webkitCommentVersion comment = Straightforward.webkitCommentVersion comment), $"{comment}")
