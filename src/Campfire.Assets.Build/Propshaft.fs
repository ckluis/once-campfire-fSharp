// Port of rust/crates/assets/build/propshaft.rs
/// A port of Propshaft 1.2.1's load path, digesting and compilers (the gem's lib/propshaft/*),
/// run at build time so the digested paths and compiled bytes match `assets:precompile`.
module Campfire.Assets.Build.Propshaft

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

type Asset = { LogicalPath: string; Source: string; Content: byte[] }

type Kind =
    | Css
    | Js
    | Other

// Ruby's \s is [ \t\n\v\f\r]; spelled out so the Latin-1 decoded input can't match more.
[<Literal>]
let private Ws = @"[ \t\n\x0B\x0C\r]"

// Propshaft reads assets as ASCII-8BIT and matches bytes; decoding each byte to the char with the
// same code point lets a string regex engine see exactly those bytes.
let private latin1Decode (bytes: byte[]) : string = Encoding.Latin1.GetString bytes

let private latin1Encode (s: string) : byte[] = Encoding.Latin1.GetBytes s

let private latin1ToUtf8 (s: string) : string = Encoding.UTF8.GetString(latin1Encode s)

let private utf8ToLatin1 (s: string) : string = latin1Decode (Encoding.UTF8.GetBytes s)

/// Path components, which is how Rust's `PathBuf` orders and compares paths.
let private components (path: string) : string[] = path.Split('/', StringSplitOptions.RemoveEmptyEntries)

/// `PathBuf`'s `Ord`: component by component, so "a/b" sorts before "a-b" although '-' < '/'.
let comparePaths (a: string) (b: string) : int =
    let a = components a
    let b = components b
    let rec go i =
        if i >= a.Length || i >= b.Length then compare a.Length b.Length
        else
            match String.CompareOrdinal(a[i], b[i]) with
            | 0 -> go (i + 1)
            | c -> c
    go 0

/// The last component of a path.
let fileName (path: string) : string =
    match path.LastIndexOf '/' with
    | -1 -> path
    | slash -> path.Substring(slash + 1)

/// Ruby's File.extname.
let extname (path: string) : string =
    let slash = path.LastIndexOf '/'
    let b = if slash < 0 then path else path.Substring(slash + 1)
    let trimmed = b.TrimStart '.'
    match trimmed.LastIndexOf '.' with
    | -1 -> ""
    | dot -> trimmed.Substring dot

/// Pathname#dirname for relative logical paths.
let private dirname (path: string) : string =
    match path.LastIndexOf '/' with
    | -1 -> "."
    | slash -> path.Substring(0, slash)

/// Pathname#+ for relative paths: leading "." and ".." of the right side are resolved against
/// the left side; anything after the first ordinary component is kept as written.
let private plus (left: string) (right: string) : string =
    let prefix = ResizeArray(components left)
    let suffix = ResizeArray(components right)
    let kept = ResizeArray<string>()
    let mutable searching = true
    while searching do
        while suffix.Count > 0 && suffix[0] = "." do
            suffix.RemoveAt 0
        if prefix.Count = 0 then
            searching <- false
        else
            let last = prefix[prefix.Count - 1]
            prefix.RemoveAt(prefix.Count - 1)
            if last <> "." then
                if last = ".." || not (suffix.Count > 0 && suffix[0] = "..") then
                    kept.Add last
                    searching <- false
                else
                    suffix.RemoveAt 0
    prefix.AddRange kept
    prefix.AddRange suffix
    if prefix.Count = 0 then "." else String.Join('/', prefix)

/// Pathname#cleanpath (non-conservative) for relative paths.
let private cleanpath (path: string) : string =
    let out = ResizeArray<string>()
    for c in path.Split '/' do
        match c with
        | ""
        | "." -> ()
        | ".." when out.Count > 0 && out[out.Count - 1] <> ".." -> out.RemoveAt(out.Count - 1)
        | other -> out.Add other
    if out.Count = 0 then "." else String.Join('/', out)

/// CssAssetUrls#resolve_path (JsAssetUrls has the same one).
let private resolvePath (directory: string) (filename: string) : string =
    if filename.StartsWith "../" then cleanpath (plus directory filename)
    elif filename.StartsWith "/" then filename.Substring 1
    else plus directory (if filename.StartsWith "./" then filename.Substring 2 else filename)

/// The start of the extension that `sub(/\.(\w+(\.map)?)$/)` replaces in Asset#digested_path.
let private digestableExtensionStart (path: string) : int option =
    let isWord (s: string) = s.Length > 0 && s |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_')
    seq { 0 .. path.Length - 1 }
    |> Seq.filter (fun i -> path[i] = '.')
    |> Seq.tryFind (fun dot ->
        let rest = path.Substring(dot + 1)
        isWord rest || (rest.EndsWith ".map" && isWord (rest.Substring(0, rest.Length - 4))))

let private allFilesFromTree (path: string) : string list =
    let rec walk (dir: string) =
        [ for entry in Directory.EnumerateFileSystemEntries dir do
              if Directory.Exists entry then yield! walk entry else yield entry ]
    walk path

/// Propshaft::LoadPath#dedup: drop paths nested in (string-prefixed by) an earlier sorted path,
/// keeping the original order. Pathname sorts with "/" below every other character.
let private dedup (paths: string list) : string list =
    let key (p: string) = p.Replace('/', '\000')
    let sorted = paths |> List.sortWith (fun a b -> String.CompareOrdinal(key a, key b))
    let deduped = ResizeArray<string>()
    for path in sorted do
        if deduped.Count = 0 || not (path.StartsWith(deduped[deduped.Count - 1], StringComparison.Ordinal)) then
            deduped.Add path
    let seen = HashSet<string>()
    paths |> List.filter (fun p -> deduped.Contains p && seen.Add p)

/// `gsub` over a regex, calling `replace` for each match.
let private gsub (pattern: Regex) (input: string) (replace: Match -> string) : string =
    pattern.Replace(input, MatchEvaluator replace)

let private hex (bytes: byte[]) : string = Convert.ToHexStringLower bytes

type LoadPath(paths: string list, version: string, prefix: string) =
    let assets = ResizeArray<Asset>()
    let byLogicalPath = Dictionary<string, int>(StringComparer.Ordinal)

    do
        for path in dedup paths do
            if Directory.Exists path then
                let root = path.TrimEnd '/'
                for file in allFilesFromTree path |> List.sortWith comparePaths do
                    if not ((fileName file).StartsWith '.') then
                        let logicalPath = file.Substring(root.Length + 1)
                        if not (byLogicalPath.ContainsKey logicalPath) then
                            byLogicalPath[logicalPath] <- assets.Count
                            assets.Add { LogicalPath = logicalPath; Source = file; Content = File.ReadAllBytes file }

    let quotedUrl (head: string) (excluded: string) =
        Regex(
            $"""{head}\({Ws}*["']?(?!(?:{excluded}))([^"' \t\n\x0B\x0C\r?#)]+)([#?][^"')]+)?{Ws}*["']?\)""",
            RegexOptions.CultureInvariant
        )

    // Propshaft::Compiler::CssAssetUrls::ASSET_URL_PATTERN
    let cssAssetUrls = quotedUrl "url" @"\#|%23|data:|http:|https:|//"
    // Propshaft::Compiler::JsAssetUrls::ASSET_URL_PATTERN
    let jsAssetUrls = quotedUrl "RAILS_ASSET_URL" @"\#|%23|data|http|//"
    // Propshaft::Compiler::SourceMappingUrls::SOURCE_MAPPING_PATTERN, with Ruby's \Z
    let sourceMappingUrls =
        Regex($@"(//|/\*)# sourceMappingURL=(.+\.map)({Ws}*?\*/)?{Ws}*?(?=\n?\z)", RegexOptions.CultureInvariant)
    let urlPrefixInSourceMap = Regex($"^(.+/)?{Regex.Escape prefix}/", RegexOptions.Multiline ||| RegexOptions.CultureInvariant)
    let alreadyDigested = Regex(@"-([0-9a-zA-Z_-]{7,128})\.digested", RegexOptions.CultureInvariant)

    // Each is a pure function of the asset, and the digest of one asset is asked for once per
    // reference to it, so they're remembered.
    let decoded = Dictionary<int, string>()
    let digests = Dictionary<int, string>()

    let content (index: int) =
        match decoded.TryGetValue index with
        | true, s -> s
        | _ ->
            let s = latin1Decode assets[index].Content
            decoded[index] <- s
            s

    let urlPrefix = prefix.TrimEnd '/'

    member _.Assets: IReadOnlyList<Asset> = assets

    member _.Find(logicalPath: string) : int option =
        match byLogicalPath.TryGetValue logicalPath with
        | true, i -> Some i
        | _ -> None

    /// Propshaft::Asset#content_type, reduced to the two types that have compilers.
    member _.Kind(index: int) : Kind =
        match extname assets[index].LogicalPath with
        | ".css" -> Css
        | ".js" -> Js
        | _ -> Other

    member this.Digest(index: int) : string =
        match digests.TryGetValue index with
        | true, d -> d
        | _ ->
            use hasher = IncrementalHash.CreateHash HashAlgorithmName.SHA1
            hasher.AppendData assets[index].Content
            for referenced in this.ReferencedBy index do
                hasher.AppendData assets[referenced].Content
            hasher.AppendData(Encoding.UTF8.GetBytes version)
            let d = (hex (hasher.GetHashAndReset())).Substring(0, 8)
            digests[index] <- d
            d

    /// Propshaft::Asset#digested_path
    member this.DigestedPath(index: int) : string =
        let logicalPath = assets[index].LogicalPath
        if alreadyDigested.IsMatch logicalPath then
            logicalPath
        else
            match digestableExtensionStart logicalPath with
            | Some dot -> $"{logicalPath.Substring(0, dot)}-{this.Digest index}{logicalPath.Substring dot}"
            | None -> logicalPath

    /// Propshaft::Compilers#compile: None when no compiler is registered for the type.
    member this.CompiledContent(index: int) : byte[] option =
        let assetUrls =
            match this.Kind index with
            | Css -> Some cssAssetUrls
            | Js -> Some jsAssetUrls
            | Other -> None
        assetUrls
        |> Option.map (fun assetUrls ->
            let input = this.CompileAssetUrls(index, assetUrls, content index)
            latin1Encode (this.CompileSourceMappingUrls(index, input)))

    member private this.ReferencedBy(index: int) : int list =
        let pattern =
            match this.Kind index with
            | Css -> Some cssAssetUrls
            | Js -> Some jsAssetUrls
            | Other -> None
        match pattern with
        | None -> []
        | Some pattern ->
            let references = ResizeArray<int>()
            this.CollectReferences(index, pattern, references)
            List.ofSeq references

    // CssAssetUrls#referenced_by / JsAssetUrls#referenced_by: referenced assets are scanned with
    // the same pattern whatever their own type is.
    member private this.CollectReferences(index: int, pattern: Regex, references: ResizeArray<int>) : unit =
        let directory = dirname assets[index].LogicalPath
        for m in pattern.Matches(content index) do
            let url = latin1ToUtf8 m.Groups[1].Value
            match this.Find(resolvePath directory url) with
            | Some referenced when not (references.Contains referenced) ->
                references.Add referenced
                this.CollectReferences(referenced, pattern, references)
            | _ -> ()

    member private this.CompileAssetUrls(index: int, pattern: Regex, input: string) : string =
        let directory = dirname assets[index].LogicalPath
        let isCss = this.Kind index = Css
        gsub pattern input (fun m ->
            let url = latin1ToUtf8 m.Groups[1].Value
            let fingerprint = if m.Groups[2].Success then m.Groups[2].Value else ""
            let replacement =
                match this.Find(resolvePath directory url) with
                | Some found -> $"\"{urlPrefix}/{this.DigestedPath found}{latin1ToUtf8 fingerprint}\""
                | None -> $"\"{url}\""
            utf8ToLatin1 (if isCss then $"url({replacement})" else replacement))

    member private this.CompileSourceMappingUrls(index: int, input: string) : string =
        let logicalPath = assets[index].LogicalPath
        gsub sourceMappingUrls input (fun m ->
            let commentStart = m.Groups[1].Value
            let commentEnd = if m.Groups[3].Success then m.Groups[3].Value else ""
            let url = urlPrefixInSourceMap.Replace(latin1ToUtf8 m.Groups[2].Value, "")
            let directory = dirname logicalPath
            let resolved = if directory = "." then url else plus directory url
            match this.Find resolved with
            | Some found -> $"{commentStart}# sourceMappingURL={urlPrefix}/{utf8ToLatin1 (this.DigestedPath found)}{commentEnd}"
            | None -> $"{commentStart}{commentEnd}")
