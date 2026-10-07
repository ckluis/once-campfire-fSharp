// Port of rust/crates/campfire/src/concerns/user_agent.rs
//
// A port of the useragent gem (0.16.11), which Rails' `allow_browser` and platform_agent use to
// read the User-Agent header: `UserAgent.parse` splits the header into products, and the first
// of `UserAgent::Browsers::ALL` whose `extend?` accepts them decides how `browser`, `version`,
// `platform`, `os`, `bot?` and `mobile?` are answered.
//
// Where the gem raises (a `NoMethodError` on nil, say), the `try*` functions return `Error Raised`;
// the plain ones fall back to nil. Ruby's `\d` and `\s` are ASCII-only and so are the hand-written
// matchers here. `^` and `$` are treated as string anchors: a header value cannot contain a newline.
//
// Rust scans bytes where this scans UTF-16 code units. Every delimiter the matchers name is ASCII and
// every unit of a non-ASCII character (a surrogate half included) is one their classes treat as "any
// other character", so each scan stops at the same place.
module Campfire.App.UserAgent

open System
open Campfire.Ruby
open Campfire.Views.Helpers

/// The gem raised (NoMethodError/ArgumentError) instead of answering.
type Raised = Raised

type Rb<'T> = Result<'T, Raised>

[<Literal>]
let private DefaultUserAgent = "Mozilla/4.0 (compatible)"

/// `\s` in a Ruby regexp.
let internal isRubySpace (c: char) : bool =
    c = ' ' || c = '\t' || c = '\n' || c = '\011' || c = '\012' || c = '\r'

/// `a.downcase == b.downcase`. Two ASCII strings can compare byte by byte, ignoring ASCII case:
/// lowercasing them changes only A-Z. Anything else is lowercased first, because a non-ASCII
/// character can lowercase to an ASCII one (U+212A KELVIN SIGN to "k").
let internal sameIgnoringCase (a: string) (b: string) : bool =
    if System.Text.Ascii.IsValid a && System.Text.Ascii.IsValid b then
        String.Equals(a, b, StringComparison.OrdinalIgnoreCase)
    else
        Application.toLowercase a = Application.toLowercase b

/// `haystack.downcase.include?(needle)` for a non-empty, lowercase ASCII `needle`, with the same
/// ASCII fast path as `sameIgnoringCase`.
let internal containsIgnoringCase (haystack: string) (needle: string) : bool =
    if System.Text.Ascii.IsValid haystack then
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase)
    else
        (Application.toLowercase haystack).Contains(needle, StringComparison.Ordinal)

/// ActiveSupport's `present?` for strings.
let isPresent (value: string) : bool = not (Seq.forall Char.IsWhiteSpace value)

// ---------------------------------------------------------------------------------------------
// UserAgent::Version

type Segment =
    /// A run of digits, leading zeros stripped ("0" for zero), compared as an unbounded integer.
    | Int of string
    | Str of string

module private Segment =
    let int (digits: string) : Segment =
        let trimmed = digits.TrimStart '0'
        Int(if trimmed = "" then "0" else trimmed)

    let asU64 (segment: Segment) : uint64 option =
        match segment with
        | Int digits ->
            match UInt64.TryParse digits with
            | true, n -> Some n
            | _ -> None
        | Str _ -> None

/// `UserAgent::Version`. Equality is string equality; ordering is the gem's `<=>`, which is not a
/// total order (a non-numeric version sorts below everything it isn't equal to, from either side).
type Version =
    { String: string
      Blank: bool
      Comparable: bool }

/// `str.scan(/\d+|[A-Za-z][0-9A-Za-z-]*$/)`.
let private scanSequences (text: string) : Segment list =
    let sequences = ResizeArray<Segment>()
    let mutable i = 0
    while i < text.Length do
        if Char.IsAsciiDigit text[i] then
            let start = i
            while i < text.Length && Char.IsAsciiDigit text[i] do
                i <- i + 1
            sequences.Add(Segment.int (text.Substring(start, i - start)))
        elif Char.IsAsciiLetter text[i] then
            let mutable e = i + 1
            while e < text.Length && (Char.IsAsciiLetterOrDigit text[e] || text[e] = '-') do
                e <- e + 1
            if e = text.Length then
                sequences.Add(Str(text.Substring i))
                i <- e
            else
                i <- i + 1
        else
            i <- i + 1
    List.ofSeq sequences

module Version =
    let create (text: string) : Version =
        let blank = Seq.forall isRubySpace text
        let digits = text |> Seq.takeWhile Char.IsAsciiDigit |> Seq.length
        let comparable = not blank && digits > 0 && (digits = text.Length || text.Substring(digits).StartsWith '.')
        { String = text; Blank = blank; Comparable = comparable }

    /// `Version.new(nil)`.
    let empty: Version = create ""

    /// `Version#nil?`: the string is empty or whitespace.
    let isNil (version: Version) : bool = version.Blank

    /// `version.to_s.present?` (ActiveSupport's Unicode-whitespace `blank?`).
    let isPresent (version: Version) : bool = isPresent version.String

    /// `Version#to_a`, split out when asked for rather than in `create`: a parse makes a version for
    /// every product, and only comparisons read the segments.
    let toA (version: Version) : Segment list =
        if version.Blank then []
        elif version.Comparable then scanSequences version.String
        else [ Str version.String ]

    /// `Version#<=>` against another version: only the first six segments count. -1, 0 or 1.
    let rubyCmp (version: Version) (other: Version) : int =
        if version.Comparable then
            let ours = toA version |> Array.ofList
            let theirs = toA other |> Array.ofList
            let zero = Int "0"
            let mutable result = 0
            let mutable i = 0
            while result = 0 && i < 6 do
                let a = if i < ours.Length then ours[i] else zero
                let b = if i < theirs.Length then theirs[i] else zero
                match a, b with
                | Str _, Int _ -> result <- -1
                | Int _, Str _ -> result <- 1
                | _ when a = b -> ()
                | Int x, Int y -> result <- (if x.Length <> y.Length then compare x.Length y.Length else sign (String.CompareOrdinal(x, y)))
                | Str x, Str y -> result <- sign (String.CompareOrdinal(x, y))
                i <- i + 1
            result
        elif version.String = other.String then
            0
        else
            -1

    /// `a < b`, as the gem's `<=>` answers it.
    let lessThan (a: Version) (b: Version) : bool = rubyCmp a b < 0

// ---------------------------------------------------------------------------------------------
// UserAgent (one product) and parsing

type internal Product =
    { Product: string
      Version: Version
      Comment: string[] option }

module internal Product =
    let commentAt (product: Product) (index: int) : string option =
        product.Comment |> Option.bind (fun comment -> if index < comment.Length then Some comment[index] else None)

    let joinedComment (product: Product) : string option =
        product.Comment |> Option.map (fun comment -> String.Join("; ", comment))

/// `String#split(separator)`: trailing empty fields are dropped.
let internal rubySplit (text: string) (separator: string) : string[] =
    let parts = ResizeArray<string>(text.Split(separator, StringSplitOptions.None))
    while parts.Count > 0 && parts[parts.Count - 1] = "" do
        parts.RemoveAt(parts.Count - 1)
    parts.ToArray()

/// Length of the run of characters at `from` satisfying `f`.
let private run (text: string) (from: int) (f: char -> bool) : int =
    let mutable n = 0
    while from + n < text.Length && f text[from + n] do
        n <- n + 1
    n

/// `UserAgent::MATCHER` applied at the start of `s`:
/// `^['"]*([^/\s]+)/?([^\s,]*)(\s\(([^\)]*)\)|,gzip\(gfe\))?`. Returns the match length.
let internal matchProduct (s: string) : (int * Product) option =
    let isProductChar (c: char) = c <> '/' && not (isRubySpace c)
    let quotes = run s 0 (fun c -> c = '\'' || c = '"')
    let start =
        if quotes < s.Length && isProductChar s[quotes] then Some quotes
        elif quotes > 0 then Some(quotes - 1) // backtrack: the last quote is the product
        else None
    match start with
    | None -> None
    | Some start ->
        let mutable i = start + 1
        i <- i + run s i isProductChar
        let product = s.Substring(start, i - start)
        if i < s.Length && s[i] = '/' then i <- i + 1
        let versionStart = i
        i <- i + run s i (fun c -> not (isRubySpace c) && c <> ',')
        let version = s.Substring(versionStart, i - versionStart)
        let mutable comment: string option = None
        if i < s.Length && isRubySpace s[i] && i + 1 < s.Length && s[i + 1] = '(' then
            match s.IndexOf(')', i + 2) with
            | -1 -> ()
            | close ->
                comment <- Some(s.Substring(i + 2, close - (i + 2)))
                i <- close + 1
        elif String.CompareOrdinal(s, i, ",gzip(gfe)", 0, 10) = 0 then
            i <- i + 10
        Some(
            i,
            { Product = product
              Version = Version.create version
              Comment = comment |> Option.map (fun comment -> rubySplit comment "; ") }
        )

// ---------------------------------------------------------------------------------------------
// OperatingSystems and hand-written regexps

let private isVersionChar (c: char) : bool = Char.IsAsciiDigit c || c = '.'

/// `/(?:Intel|PPC) Mac OS X\s*([0-9_\.]+)?/`: `Some capture` when it matches.
let private macOsXVersion (os: string) : string option option =
    seq { 0 .. os.Length - 1 }
    |> Seq.tryPick (fun i ->
        let rest = os.Substring i
        let after =
            if rest.StartsWith("Intel Mac OS X", StringComparison.Ordinal) then Some(rest.Substring 14)
            elif rest.StartsWith("PPC Mac OS X", StringComparison.Ordinal) then Some(rest.Substring 12)
            else None
        after
        |> Option.map (fun after ->
            let after = after.Substring(run after 0 isRubySpace)
            let digits = run after 0 (fun c -> Char.IsAsciiDigit c || c = '_' || c = '.')
            if digits > 0 then Some(after.Substring(0, digits)) else None))

/// `IOS_VERSION_REGEX = /CPU (?:iPhone |iPod )?OS ([\d_]+) like Mac OS X/`.
let private iosVersion (os: string) : string option =
    seq { 0 .. os.Length - 1 }
    |> Seq.tryPick (fun i ->
        let rest = os.Substring i
        if not (rest.StartsWith("CPU ", StringComparison.Ordinal)) then
            None
        else
            let rest = rest.Substring 4
            [ (if rest.StartsWith("iPhone ", StringComparison.Ordinal) then Some(rest.Substring 7) else None)
              (if rest.StartsWith("iPod ", StringComparison.Ordinal) then Some(rest.Substring 5) else None)
              Some rest ]
            |> List.choose id
            |> List.tryPick (fun rest ->
                if not (rest.StartsWith("OS ", StringComparison.Ordinal)) then
                    None
                else
                    let rest = rest.Substring 3
                    let digits = run rest 0 (fun c -> Char.IsAsciiDigit c || c = '_')
                    if digits > 0 && rest.Substring(digits).StartsWith(" like Mac OS X", StringComparison.Ordinal) then
                        Some(rest.Substring(0, digits))
                    else
                        None))

/// `/CrOS\s([^\s]+)\s(\d+(\.\d+)*)/`: the second capture.
let private chromeOsVersion (os: string) : string option =
    seq { 0 .. os.Length - 1 }
    |> Seq.tryPick (fun i ->
        if String.CompareOrdinal(os, i, "CrOS", 0, 4) <> 0 then
            None
        else
            let offset = i + 4
            if offset >= os.Length || not (isRubySpace os[offset]) then
                None
            else
                let word = run os (offset + 1) (fun c -> not (isRubySpace c))
                if word = 0 then
                    None
                else
                    let at = offset + 1 + word
                    if at >= os.Length || not (isRubySpace os[at]) then
                        None
                    else
                        let start = at + 1
                        let mutable e = start + run os start Char.IsAsciiDigit
                        if e = start then
                            None
                        else
                            while e + 1 < os.Length && os[e] = '.' && Char.IsAsciiDigit os[e + 1] do
                                e <- e + 1 + run os (e + 1) Char.IsAsciiDigit
                            Some(os.Substring(start, e - start)))

/// `UserAgent::OperatingSystems.normalize_os`.
let private normalizeOs (os: string) : string =
    let windows =
        match os with
        | "Windows NT 10.0" -> Some "Windows 10"
        | "Windows NT 6.3" -> Some "Windows 8.1"
        | "Windows NT 6.2" -> Some "Windows 8"
        | "Windows NT 6.1" -> Some "Windows 7"
        | "Windows NT 6.0" -> Some "Windows Vista"
        | "Windows NT 5.2" -> Some "Windows XP x64 Edition"
        | "Windows NT 5.1" -> Some "Windows XP"
        | "Windows NT 5.01" -> Some "Windows 2000, Service Pack 1 (SP1)"
        | "Windows NT 5.0" -> Some "Windows 2000"
        | "Windows NT 4.0" -> Some "Windows NT 4.0"
        | "Windows 98" -> Some "Windows 98"
        | "Windows 95" -> Some "Windows 95"
        | "Windows CE" -> Some "Windows CE"
        | _ -> None
    match windows with
    | Some windows -> windows
    | None ->
        match macOsXVersion os with
        | Some(Some version) -> "OS X " + version.Replace('_', '.')
        | Some None -> "OS X"
        | None ->
            match iosVersion os with
            | Some version -> "iOS " + version.Replace('_', '.')
            | None ->
                match chromeOsVersion os with
                | Some version -> "ChromeOS " + version
                | None -> os

/// `/Windows NT [\d\.]+|Windows Phone (OS )?[\d\.]+/`: the matched text.
let private windowsOs (s: string) : string option =
    seq { 0 .. s.Length - 1 }
    |> Seq.tryPick (fun i ->
        let rest = s.Substring i
        let tail =
            if rest.StartsWith("Windows NT ", StringComparison.Ordinal) then
                Some(rest.Substring 11)
            elif rest.StartsWith("Windows Phone ", StringComparison.Ordinal) then
                let after = rest.Substring 14
                if after.StartsWith("OS ", StringComparison.Ordinal) && run after 3 isVersionChar > 0 then Some(after.Substring 3) else Some after
            else
                None
        tail
        |> Option.bind (fun tail ->
            let digits = run tail 0 isVersionChar
            if digits > 0 then Some(rest.Substring(0, rest.Length - tail.Length + digits)) else None))

/// `joined_comment =~ /Trident.+rv:/`.
let private tridentRv (s: string) : bool =
    let mutable from = 0
    let mutable found = false
    while not found && from <= s.Length - 7 do
        match s.IndexOf("Trident", from, StringComparison.Ordinal) with
        | -1 -> from <- s.Length
        | i ->
            let rest = s.Substring(i + 7)
            let line = match rest.IndexOf '\n' with -1 -> rest | n -> rest.Substring(0, n)
            // `.+` needs at least one character before `rv:`.
            let mutable at = 0
            while not found && at <= line.Length - 3 do
                match line.IndexOf("rv:", at, StringComparison.Ordinal) with
                | -1 -> at <- line.Length
                | j ->
                    if j >= 1 then found <- true
                    at <- j + 1
            from <- i + 1
    found

/// `joined_comment[/(MSIE\s|rv:)([\d\.]+)/, 2]`.
let private ieVersion (s: string) : string option =
    seq { 0 .. s.Length - 1 }
    |> Seq.tryPick (fun i ->
        let rest = s.Substring i
        let tail =
            if rest.StartsWith("MSIE", StringComparison.Ordinal) && rest.Length > 4 && isRubySpace rest[4] then Some(rest.Substring 5)
            elif rest.StartsWith("rv:", StringComparison.Ordinal) then Some(rest.Substring 3)
            else None
        tail
        |> Option.bind (fun tail ->
            let digits = run tail 0 isVersionChar
            if digits > 0 then Some(tail.Substring(0, digits)) else None))

/// The capture of `/<prefix>([class]+)/` at its leftmost match.
let private captureAfter (s: string) (prefix: string) (isClass: char -> bool) : string option =
    let mutable from = 0
    let mutable found = None
    while found.IsNone && from <= s.Length - prefix.Length do
        match s.IndexOf(prefix, from, StringComparison.Ordinal) with
        | -1 -> from <- s.Length
        | i ->
            let tail = s.Substring(i + prefix.Length)
            let len = run tail 0 isClass
            if len > 0 then found <- Some(tail.Substring(0, len))
            from <- i + 1
    found

/// `WEBKIT_VERSION_REGEXP = /\A(?<webkit>AppleWebKit)\/(?<version>[\d\.]+)/i`: the version.
let internal webkitCommentVersion (comment: string) : string option =
    // Rust takes the first 11 characters (scalar values); the name is ASCII, so 11 UTF-16 units that are
    // ASCII letters say the same.
    if comment.Length < 11 || not (sameIgnoringCase (comment.Substring(0, 11)) "applewebkit") then
        None
    else
        let rest = comment.Substring 11
        if not (rest.StartsWith '/') then
            None
        else
            let tail = rest.Substring 1
            let digits = run tail 0 isVersionChar
            if digits > 0 then Some(tail.Substring(0, digits)) else None

/// `Webkit::BuildVersions`: Safari versions before Safari 3 reported only the WebKit build.
let private webkitBuildVersion (build: string) : string option =
    match build with
    | "85.7" -> Some "1.0"
    | "85.8.5"
    | "85.8.2" -> Some "1.0.3"
    | "124" -> Some "1.2"
    | "125.2" -> Some "1.2.2"
    | "125.4" -> Some "1.2.3"
    | "125.5.5"
    | "125.5.6"
    | "125.5.7" -> Some "1.2.4"
    | "312.1.1"
    | "312.1" -> Some "1.3"
    | "312.5"
    | "312.5.1"
    | "312.5.2" -> Some "1.3.1"
    | "312.8"
    | "312.8.1" -> Some "1.3.2"
    | "412"
    | "412.6"
    | "412.6.2" -> Some "2.0"
    | "412.7" -> Some "2.0.1"
    | "416.11"
    | "416.12" -> Some "2.0.2"
    | "417.9"
    | "418" -> Some "2.0.3"
    | "418.8"
    | "418.9"
    | "418.9.1"
    | "419" -> Some "2.0.4"
    | "425.13" -> Some "2.2"
    | "534.52.7" -> Some "5.1.2"
    | _ -> None

// ---------------------------------------------------------------------------------------------
// Browsers

type private Kind =
    | Base
    | Edge
    | InternetExplorer
    | Opera
    | WechatBrowser
    | Vivaldi
    | Chrome
    | ITunes
    | PlayStation
    | PodcastAddict
    | Webkit
    | Gecko
    | WindowsMediaPlayer
    | AppleCoreMedia
    | Libavformat

module private Kind =
    /// `UserAgent::Browsers::ALL`, in detection order.
    let all: Kind list =
        [ Edge; InternetExplorer; Opera; WechatBrowser; Vivaldi; Chrome; ITunes; PlayStation; PodcastAddict; Webkit; Gecko; WindowsMediaPlayer; AppleCoreMedia; Libavformat ]

    /// Each browser class's `self.extend?(agent)`.
    let extends (products: Product[]) (kind: Kind) : bool =
        let first = Array.tryHead products
        let last = Array.tryLast products
        let firstVersion = first |> Option.map (fun p -> p.Version.String)
        let any (name: string) = products |> Array.exists (fun p -> p.Product = name)
        match kind with
        | Base -> true
        | Edge -> last |> Option.exists (fun p -> p.Product = "Edge")
        | InternetExplorer ->
            first
            |> Option.exists (fun p ->
                p.Comment.IsSome
                && ((Product.commentAt p 1 |> Option.exists (fun c -> c.Contains("MSIE", StringComparison.Ordinal)))
                    || (Product.joinedComment p |> Option.exists tridentRv)))
        | Opera -> first |> Option.exists (fun p -> p.Product = "Opera") || last |> Option.exists (fun p -> p.Product = "OPR")
        | WechatBrowser -> products |> Array.exists (fun p -> containsIgnoringCase p.Product "micromessenger")
        | Vivaldi -> any "Vivaldi"
        | Chrome -> any "Chrome" || any "CriOS"
        | ITunes -> any "iTunes"
        | PlayStation ->
            first
            |> Option.bind (fun p -> p.Comment)
            |> Option.bind Array.tryHead
            |> Option.exists (fun c ->
                c.Contains("PLAYSTATION 3", StringComparison.Ordinal)
                || c.Contains("PlayStation Vita", StringComparison.Ordinal)
                || c.Contains("PlayStation 4", StringComparison.Ordinal))
        | PodcastAddict -> products.Length >= 3 && products[0].Product = "Podcast" && products[1].Product = "Addict" && products[2].Product = "-"
        | Webkit ->
            products
            |> Array.exists (fun p ->
                sameIgnoringCase p.Product "applewebkit"
                || (p.Comment |> Option.defaultValue [||] |> Array.exists (fun c -> (webkitCommentVersion c).IsSome)))
        | Gecko -> first |> Option.exists (fun p -> p.Product = "Mozilla")
        | WindowsMediaPlayer ->
            products
            |> Array.exists (fun p ->
                (p.Product = "NSPlayer" || p.Product = "Windows-Media-Player" || p.Product = "WMFSDK")
                && not (firstVersion = Some "4.1.0.3856" || firstVersion = Some "7.10.0.3059" || firstVersion = Some "7.0.0.1956"))
        | AppleCoreMedia -> any "AppleCoreMedia"
        | Libavformat -> products |> Array.exists (fun p -> p.Product = "Lavf" || (p.Product = "NSPlayer" && firstVersion = Some "4.1.0.3856"))

/// A parsed User-Agent: `UserAgent::Browsers::Base` extended with the detected browser class.
type Agent =
    private
        { Kind: Kind
          Products: Product[] }

/// `UserAgent.parse`: blank strings parse as "Mozilla/4.0 (compatible)".
let parse (userAgent: string) : Agent =
    let mutable rest = if (Ruby.strip userAgent) = "" then DefaultUserAgent else userAgent
    let products = ResizeArray<Product>()
    let mutable going = true
    while going do
        match matchProduct rest with
        | Some(length, product) ->
            products.Add product
            rest <- Ruby.strip (rest.Substring length)
        | None -> going <- false
    let products = products.ToArray()
    let kind = Kind.all |> List.tryFind (Kind.extends products) |> Option.defaultValue Base
    { Kind = kind; Products = products }

module Agent =
    let private first (agent: Agent) : Product option = Array.tryHead agent.Products

    let private last (agent: Agent) : Product option = Array.tryLast agent.Products

    /// `detect_product`: case-insensitive product name lookup (also what `respond_to?` and
    /// `method_missing` use).
    let private detectProduct (agent: Agent) (name: string) : Product option =
        agent.Products |> Array.tryFind (fun p -> sameIgnoringCase p.Product name)

    /// `application`: most classes use the first product; the WebKit-based ones the first product
    /// with a non-empty comment.
    let private application (agent: Agent) : Product option =
        match agent.Kind with
        | Chrome
        | Vivaldi
        | Webkit
        | ITunes
        | AppleCoreMedia -> agent.Products |> Array.tryFind (fun p -> p.Comment |> Option.exists (fun c -> c.Length > 0))
        | _ -> first agent

    let private applicationComment (agent: Agent) : string[] option = application agent |> Option.bind (fun a -> a.Comment)

    let private baseVersion (agent: Agent) : Version option = application agent |> Option.map (fun a -> a.Version)

    /// `Webkit#webkit.version`: the AppleWebKit product's version, or one from a comment.
    let private webkit (agent: Agent) : Version option =
        match agent.Products |> Array.tryFind (fun p -> sameIgnoringCase p.Product "applewebkit") with
        | Some product -> Some product.Version
        | None ->
            agent.Products
            |> Array.collect (fun p -> p.Comment |> Option.defaultValue [||])
            |> Array.tryPick webkitCommentVersion
            |> Option.map Version.create

    let private commentFirst (comment: string[] option) : string option = comment |> Option.bind Array.tryHead

    let private playstationOs (agent: Agent) : string option =
        applicationComment agent |> Option.map (fun comment -> String.Join(" ", comment))

    let private playstationPlatform (agent: Agent) : string option =
        playstationOs agent
        |> Option.bind (fun os ->
            if os.Contains("PLAYSTATION 3", StringComparison.Ordinal) then Some "PlayStation 3"
            elif os.Contains("PlayStation 4", StringComparison.Ordinal) then Some "PlayStation 4"
            elif os.Contains("PlayStation Vita", StringComparison.Ordinal) then Some "PlayStation Vita"
            else None)

    let private playstationBrowser (agent: Agent) : string option =
        match applicationComment agent |> commentFirst with
        | None -> None
        | Some firstComment ->
            if firstComment.Contains("PLAYSTATION 3", StringComparison.Ordinal) then Some "PS3 Internet Browser"
            elif last agent |> Option.exists (fun p -> p.Product = "Silk") then Some "Silk"
            elif firstComment.Contains("PlayStation 4", StringComparison.Ordinal) then Some "PS4 Internet Browser"
            else None

    /// `Webkit#os`.
    let private webkitOs (agent: Agent) : string option =
        match applicationComment agent with
        | None -> None
        | Some comment ->
            let at i = if i < comment.Length then Some comment[i] else None
            if at 0 |> Option.exists (fun c -> c.Contains("Windows NT", StringComparison.Ordinal)) then
                at 0 |> Option.map normalizeOs
            elif (at 2).IsNone || at 1 |> Option.exists (fun c -> c.Contains("Android", StringComparison.Ordinal)) then
                at 1 |> Option.map normalizeOs
            else
                match comment |> Array.tryFind (fun c -> (iosVersion c).IsSome) with
                | Some ios -> Some(normalizeOs ios)
                | None -> at 2 |> Option.map normalizeOs

    /// `Webkit#platform`.
    let private webkitPlatform (agent: Agent) : string option =
        match applicationComment agent with
        | None -> None
        | Some comment ->
            let firstComment = Array.tryHead comment
            if firstComment |> Option.exists (fun c -> c.Contains("Windows", StringComparison.Ordinal)) then Some "Windows"
            elif firstComment = Some "BB10" then Some "BlackBerry"
            elif comment |> Array.exists (fun c -> c.Contains("Android", StringComparison.Ordinal)) then Some "Android"
            else firstComment

    let private webkitBrowser (agent: Agent) : string =
        if webkitOs agent |> Option.exists (fun os -> os.Contains("Android", StringComparison.Ordinal)) then "Android"
        elif webkitPlatform agent = Some "BlackBerry" then "BlackBerry"
        else "Safari"

    let private geckoBrowser (agent: Agent) : string =
        [ "PaleMoon"; "Firefox"; "Camino"; "Iceweasel"; "Seamonkey" ]
        |> List.tryFind (fun name -> (detectProduct agent name).IsSome)
        |> Option.defaultWith (fun () -> first agent |> Option.map (fun p -> p.Product) |> Option.defaultValue "")

    /// `browser`.
    let tryBrowser (agent: Agent) : Rb<string option> =
        match agent.Kind with
        | Base -> Ok(application agent |> Option.map (fun a -> a.Product))
        | Edge -> Ok(Some "Edge")
        | InternetExplorer -> Ok(Some "Internet Explorer")
        | Opera -> Ok(Some "Opera")
        | WechatBrowser -> Ok(Some "Wechat Browser")
        | Vivaldi -> Ok(Some "Vivaldi")
        | Chrome -> Ok(Some(if (detectProduct agent "Iron").IsSome then "Iron" else "Chrome"))
        | ITunes -> Ok(Some "iTunes")
        | PlayStation -> Ok(playstationBrowser agent)
        | PodcastAddict -> Ok(Some "Podcast Addict")
        | Webkit -> Ok(Some(webkitBrowser agent))
        | Gecko -> Ok(Some(geckoBrowser agent))
        | WindowsMediaPlayer -> Ok(Some "Windows Media Player")
        | AppleCoreMedia -> Ok(Some "AppleCoreMedia")
        | Libavformat -> Ok(Some "libavformat")

    /// `browser`; nil (possible for unparseable strings and a bare PlayStation Vita) is "".
    let browser (agent: Agent) : string =
        match tryBrowser agent with
        | Ok(Some browser) -> browser
        | _ -> ""

    let private operaMini (agent: Agent) : bool =
        // `/Opera Mini/ === application` matches against UserAgent#to_str; only the comment can hold a space.
        first agent |> Option.bind Product.joinedComment |> Option.exists (fun c -> c.Contains("Opera Mini", StringComparison.Ordinal))

    let private operaVersion (agent: Agent) : Version option =
        if operaMini agent then
            // `rescue Version.new` covers a comment without an "Opera Mini/<version>".
            let comment =
                applicationComment agent
                |> Option.defaultValue [||]
                |> Array.tryFind (fun c -> c.Contains("Opera Mini", StringComparison.Ordinal))
            let version = comment |> Option.bind (fun c -> captureAfter c "Opera Mini/" isVersionChar)
            Some(Version.create (defaultArg version ""))
        else
            match detectProduct agent "Version" with
            | Some product -> Some product.Version
            | None ->
                match detectProduct agent "OPR" with
                | Some product -> Some product.Version
                | None -> baseVersion agent

    let private playstationVersion (agent: Agent) : Version option =
        match playstationOs agent with
        | None -> None
        | Some os ->
            let after (marker: string) =
                let parts = rubySplit os marker
                Version.create (if parts.Length = 0 then "" else parts[parts.Length - 1])
            if playstationBrowser agent = Some "Silk" then
                last agent |> Option.map (fun p -> p.Version)
            else
                match playstationPlatform agent with
                | Some "PlayStation 3" -> Some(after "PLAYSTATION 3 ")
                | Some "PlayStation 4" -> Some(after "PlayStation 4 ")
                | Some "PlayStation Vita" -> Some(after "PlayStation Vita ")
                | _ -> None

    let private webkitVersion (agent: Agent) : Version =
        match detectProduct agent "Version" with
        | Some product -> product.Version
        | None ->
            let ios =
                match webkitOs agent |> Option.bind (fun os -> captureAfter os "iOS " isVersionChar) with
                | Some ios when webkitBrowser agent = "Safari" -> Some(Version.create (ios.Replace('_', '.')))
                | _ -> None
            match ios with
            | Some version -> version
            | None ->
                let build = webkit agent |> Option.map (fun w -> w.String) |> Option.defaultValue ""
                Version.create (defaultArg (webkitBuildVersion build) "")

    /// `version`.
    let tryVersion (agent: Agent) : Rb<Version option> =
        let raised (product: Product option) : Rb<Version> =
            match product with
            | Some product -> Ok product.Version
            | None -> Error Raised
        match agent.Kind with
        | Base
        | WindowsMediaPlayer
        | AppleCoreMedia -> Ok(baseVersion agent)
        | Edge
        | Vivaldi -> Ok(last agent |> Option.map (fun p -> p.Version))
        | InternetExplorer ->
            let joined = application agent |> Option.bind Product.joinedComment |> Option.defaultValue ""
            Ok(Some(Version.create (defaultArg (ieVersion joined) "")))
        | Opera -> Ok(operaVersion agent)
        | WechatBrowser -> raised (detectProduct agent "MicroMessenger") |> Result.map Some
        | Chrome ->
            let product = detectProduct agent "CriOs" |> Option.orElse (detectProduct agent "chrome")
            raised product |> Result.map Some
        | ITunes -> raised (detectProduct agent "iTunes") |> Result.map Some
        | PlayStation -> Ok(playstationVersion agent)
        | PodcastAddict -> Ok None
        | Webkit -> Ok(Some(webkitVersion agent))
        | Gecko ->
            match raised (detectProduct agent (geckoBrowser agent)) with
            | Error e -> Error e
            | Ok version -> Ok(if Version.isNil version then baseVersion agent else Some version)
        | Libavformat -> Ok(if (detectProduct agent "NSPlayer").IsSome then None else baseVersion agent)

    /// `version`; nil is an empty version.
    let version (agent: Agent) : Version =
        match tryVersion agent with
        | Ok(Some version) -> version
        | _ -> Version.empty

    /// `PodcastAddict#os`; the outer `Error` is the gem raising on a comment-less Dalvik/Mozilla.
    let private podcastAddictOs (agent: Agent) : Rb<string option> =
        if agent.Products.Length <= 3 then
            Ok None
        else
            let device = agent.Products[3]
            if device.Product <> "Dalvik" && device.Product <> "Mozilla" then
                Ok None
            else
                match device.Comment with
                | None -> Error Raised
                | Some comment ->
                    Ok(
                        if comment.Length > 3 then Some comment[2]
                        elif comment.Length = 3 then Some "Android"
                        else None
                    )

    /// `platform`.
    let tryPlatform (agent: Agent) : Rb<string option> =
        let comment = applicationComment agent
        let firstComment = commentFirst comment
        let any (needle: string) =
            comment |> Option.exists (fun c -> c |> Array.exists (fun c -> c.Contains(needle, StringComparison.Ordinal)))
        let firstContains (needle: string) = firstComment |> Option.exists (fun c -> c.Contains(needle, StringComparison.Ordinal))
        match agent.Kind with
        | Base
        | Libavformat -> Ok None
        | Edge
        | InternetExplorer
        | WindowsMediaPlayer -> Ok(Some "Windows")
        | Opera
        | AppleCoreMedia ->
            if comment.IsNone then Ok None
            elif firstContains "Windows" then Ok(Some "Windows")
            else Ok firstComment
        | WechatBrowser ->
            if comment.IsNone then Ok None
            elif firstContains "iPhone" then Ok(Some "iPhone")
            elif any "Android" then Ok(Some "Android")
            else Ok firstComment
        | Chrome
        | Vivaldi ->
            if comment.IsNone then Ok None
            elif firstContains "Windows" then Ok(Some "Windows")
            elif any "CrOS" then Ok(Some "ChromeOS")
            elif any "Android" then Ok(Some "Android")
            else Ok firstComment
        | Webkit
        | ITunes -> Ok(webkitPlatform agent)
        | PlayStation -> Ok(playstationPlatform agent)
        | PodcastAddict ->
            match podcastAddictOs agent with
            | Error e -> Error e
            | Ok None -> Error Raised
            | Ok(Some os) -> Ok(if os.Contains("Android", StringComparison.Ordinal) then Some "Android" else None)
        | Gecko ->
            if comment.IsNone then
                Ok None
            else
                match firstComment with
                | Some "compatible"
                | Some "Mobile" -> Ok None
                | Some c when c.StartsWith("Windows ", StringComparison.Ordinal) -> Ok(Some "Windows")
                | other -> Ok other

    let platform (agent: Agent) : string option =
        match tryPlatform agent with
        | Ok platform -> platform
        | Error _ -> None

    /// `os` shared by Chrome, Vivaldi, WechatBrowser and AppleCoreMedia.
    let private chromeOs (comment: string[]) : string option =
        let at i = if i < comment.Length then Some comment[i] else None
        let pick =
            if at 0 |> Option.exists (fun c -> c.Contains("Windows NT", StringComparison.Ordinal)) then at 0
            elif (at 2).IsNone || at 1 |> Option.exists (fun c -> c.Contains("Android", StringComparison.Ordinal)) then at 1
            else at 2
        pick |> Option.map normalizeOs

    let private geckoOs (agent: Agent) : string option =
        match applicationComment agent with
        | None -> None
        | Some comment ->
            let firstComment = Array.tryHead comment
            let at i = if i < comment.Length then Some comment[i] else None
            let startsWith (prefix: string) = firstComment |> Option.exists (fun c -> c.StartsWith(prefix, StringComparison.Ordinal))
            let index =
                if at 1 = Some "U" then Some 2
                elif startsWith "Windows " || startsWith "Android" then Some 0
                elif firstComment = Some "Mobile" then None
                else Some 1
            index |> Option.bind at |> Option.map normalizeOs

    let private itunesFullOs (agent: Agent) : string option =
        match applicationComment agent with
        | Some comment when comment.Length > 1 ->
            let fullOs = comment[1]
            let n = fullOs.Length
            // "(Build 7601" lost its ")" to the comment's own.
            let reopened =
                n >= 11
                && fullOs.Substring(n - 11, 7) = "(Build "
                && Seq.forall Char.IsAsciiDigit (fullOs.Substring(n - 4))
            Some(if reopened then fullOs + ")" else fullOs)
        | _ -> None

    let private itunesOs (agent: Agent) : string option =
        let windows = applicationComment agent |> commentFirst |> Option.exists (fun c -> c.Contains("Windows", StringComparison.Ordinal))
        if not windows then
            webkitOs agent
        else
            let fullOs = defaultArg (itunesFullOs agent) ""
            let contains (needle: string) = fullOs.Contains(needle, StringComparison.Ordinal)
            Some(
                if contains "Windows 8.1" then "Windows 8.1"
                elif contains "Windows 8" then "Windows 8"
                elif contains "Windows 7" then "Windows 7"
                elif contains "Windows Vista" then "Windows Vista"
                elif contains "Windows XP" then "Windows XP"
                else "Windows"
            )

    let private windowsMediaPlayerMajor (agent: Agent) : Rb<uint64> =
        // `version.to_a[0]` compared with an Integer: nil raises NoMethodError, a String ArgumentError.
        match baseVersion agent with
        | None -> Error Raised
        | Some version ->
            match Version.toA version with
            | Int digits :: _ -> Ok(match UInt64.TryParse digits with | true, n -> n | _ -> UInt64.MaxValue)
            | _ -> Error Raised

    let private windowsMediaPlayerOs (agent: Agent) : Rb<string> =
        match windowsMediaPlayerMajor agent with
        | Error e -> Error e
        | Ok major ->
            let segments = baseVersion agent |> Option.defaultValue Version.empty |> Version.toA |> Array.ofList
            let part i = if i < segments.Length then Segment.asU64 segments[i] else None
            Ok(
                if major <= 4UL then
                    match part 3 with
                    | Some 3564UL
                    | Some 3925UL -> "Windows 98"
                    | Some 3857UL -> "Windows 9x"
                    | Some 3936UL -> "Windows XP"
                    | Some 3938UL -> "Windows 2000"
                    | _ -> "Windows"
                elif major = 7UL then
                    match part 3 with
                    | Some 3055UL -> "Windows 98"
                    | _ -> "Windows"
                elif major = 8UL then
                    "Windows XP"
                elif major = 9UL || major = 10UL then
                    match part 3 with
                    | Some 2980UL -> "Windows 98/2000"
                    | Some 3268UL
                    | Some 3367UL
                    | Some 3270UL -> "Windows 2000"
                    | Some 3802UL
                    | Some 4503UL -> "Windows XP"
                    | _ -> "Windows"
                elif major = 11UL || major = 12UL then
                    match part 2 with
                    | Some 9841UL
                    | Some 9858UL
                    | Some 9860UL
                    | Some 9879UL -> "Windows 10"
                    | Some 9651UL -> "Windows Phone 8.1"
                    | Some 9600UL -> "Windows 8.1"
                    | Some 9200UL -> "Windows 8"
                    | Some 7600UL
                    | Some 7601UL -> "Windows 7"
                    | Some n when n >= 6000UL && n <= 6002UL -> "Windows Vista"
                    | Some 5721UL -> "Windows XP"
                    | _ -> "Windows"
                else
                    "Windows"
            )

    /// `os`.
    let tryOs (agent: Agent) : Rb<string option> =
        match agent.Kind with
        | Base
        | Libavformat -> Ok None
        | Edge ->
            let matched =
                agent.Products
                |> Array.collect (fun p -> p.Comment |> Option.defaultValue [||])
                |> Array.tryPick windowsOs
            Ok(Some(normalizeOs (defaultArg matched "")))
        | InternetExplorer ->
            let joined = application agent |> Option.bind Product.joinedComment |> Option.defaultValue ""
            Ok(Some(normalizeOs (defaultArg (windowsOs joined) "")))
        | Opera ->
            match applicationComment agent with
            | None -> Ok None
            | Some comment ->
                match Array.tryHead comment with
                | Some firstComment when firstComment.Contains("Windows", StringComparison.Ordinal) -> Ok(Some(normalizeOs firstComment))
                | _ -> Ok(if comment.Length > 1 then Some comment[1] else None)
        | WechatBrowser
        | Chrome
        | Vivaldi
        | AppleCoreMedia -> Ok(applicationComment agent |> Option.bind chromeOs)
        | Webkit -> Ok(webkitOs agent)
        | ITunes -> Ok(itunesOs agent)
        | PlayStation -> Ok(playstationOs agent)
        | PodcastAddict -> podcastAddictOs agent
        | Gecko -> Ok(geckoOs agent)
        | WindowsMediaPlayer -> windowsMediaPlayerOs agent |> Result.map Some

    /// `bot?`.
    let isBot (agent: Agent) : bool =
        match application agent with
        | None -> true
        | Some application ->
            (agent.Products
             |> Array.exists (fun p -> p.Comment |> Option.defaultValue [||] |> Array.exists (fun c -> containsIgnoringCase c "bot")))
            || (detectProduct agent "Chrome-Lighthouse").IsSome
            || application.Product.Contains("bot", StringComparison.Ordinal)

    /// `mobile?` (unused by the app; kept for the gem's vectors).
    let tryMobile (agent: Agent) : Rb<bool> =
        match agent.Kind with
        | Opera -> Ok(operaMini agent)
        | PlayStation -> Ok(playstationPlatform agent = Some "PlayStation Vita")
        | PodcastAddict -> Ok true
        | WindowsMediaPlayer ->
            windowsMediaPlayerOs agent |> Result.map (fun os -> os = "Windows Phone 8" || os = "Windows Phone 8.1")
        | _ ->
            if (detectProduct agent "Mobile").IsSome
               || agent.Products |> Array.exists (fun p -> p.Comment |> Option.defaultValue [||] |> Array.exists (fun c -> c = "Mobile")) then
                Ok true
            else
                match tryOs agent with
                | Error e -> Error e
                | Ok os ->
                    Ok(
                        (os |> Option.exists (fun os -> os.Contains("Android", StringComparison.Ordinal)))
                        || (applicationComment agent
                            |> Option.exists (fun c -> c |> Array.exists (fun c -> c.StartsWith("IEMobile", StringComparison.Ordinal))))
                    )

    /// The products as `(name, version, comment)`, for tests.
    let internal productFields (agent: Agent) : (string * string * string[] option) list =
        [ for p in agent.Products -> p.Product, p.Version.String, p.Comment ]
