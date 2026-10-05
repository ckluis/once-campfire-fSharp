// Port of rust/crates/assets/build.rs
/// Digests and compiles the reference's assets the way `bin/rails assets:precompile` does
/// (Propshaft), renders the import map, and writes the results plus reference/public where
/// Campfire.Assets embeds them:
///
///   EmbeddedData.fs       the sorted tables: manifest, stylesheets, and every servable URL's
///                         offset and length in assets.bin
///   assets.bin            the bytes of every digested asset, public file and the manifest
///   importmap-tags.html   what `javascript_importmap_tags` renders
///
///   Campfire.Assets.Build --reference <reference/> --assets <crate dir> --out <dir>
///
/// The output is a pure function of the inputs, except the Last-Modified date, which is
/// SOURCE_DATE_EPOCH when that is set and the time of the run otherwise (as Rust's build.rs does).
module Campfire.Assets.Build.Program

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text
open Campfire.Ruby

// Propshaft's Railtie default; the app doesn't change it.
[<Literal>]
let private Prefix = "/assets"

/// JSON.generate's string escaping (no script_safe, non-ASCII passed through).
let private json (s: string) : string =
    let out = StringBuilder "\""
    for c in s do
        match c with
        | '"' -> out.Append "\\\"" |> ignore
        | '\\' -> out.Append "\\\\" |> ignore
        | '\n' -> out.Append "\\n" |> ignore
        | '\r' -> out.Append "\\r" |> ignore
        | '\t' -> out.Append "\\t" |> ignore
        | '\b' -> out.Append "\\b" |> ignore
        | '\012' -> out.Append "\\f" |> ignore
        | c when c < ' ' -> out.Append("\\u").Append((int c).ToString("x4")) |> ignore
        | c -> out.Append c |> ignore
    out.Append('"').ToString()

/// An F# string literal that is the same string, whatever the file's encoding.
let private fsString (s: string) : string =
    let out = StringBuilder "\""
    for c in s do
        match c with
        | '"' -> out.Append "\\\"" |> ignore
        | '\\' -> out.Append "\\\\" |> ignore
        | c when c < ' ' || c > '~' -> out.Append("\\u").Append((int c).ToString("x4")) |> ignore
        | c -> out.Append c |> ignore
    out.Append('"').ToString()

/// Time#httpdate: "Sat, 26 Sep 2026 12:23:14 GMT".
let private httpdate (epoch: int64) : string =
    DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture)

let private buildTime () : int64 =
    match Environment.GetEnvironmentVariable "SOURCE_DATE_EPOCH" |> Option.ofObj |> Option.bind (fun s -> Int64.TryParse s |> function | true, v -> Some v | _ -> None) with
    | Some epoch -> epoch
    | None -> DateTimeOffset.UtcNow.ToUnixTimeSeconds()

/// `overrides/` first, so the app's own changes to the frontend shadow the reference's files of
/// the same logical path, then vendor/LOAD_PATH (written by script/revendor from
/// `Rails.application.assets.load_path.paths`).
let private loadPathDirs (crateDir: string) (railsRoot: string) : string list =
    let overrides = Path.Combine(crateDir, "overrides")
    let loadPath =
        try
            File.ReadAllText(Path.Combine(crateDir, "vendor/LOAD_PATH"))
        with :? FileNotFoundException ->
            failwith "vendor/LOAD_PATH is missing; run src/Campfire.Assets/script/revendor"
    let exported =
        loadPath.Split '\n'
        |> Array.filter (fun line -> line.Trim() <> "")
        |> Array.map (fun line ->
            match line.IndexOf ':' with
            | -1 -> failwith $"vendor/LOAD_PATH: bad line {line}"
            | i ->
                match line.Substring(0, i), line.Substring(i + 1) with
                | "reference", dir -> Path.Combine(railsRoot, dir)
                | "vendor", dir -> Path.Combine(crateDir, "vendor", dir)
                | _ -> failwith $"vendor/LOAD_PATH: bad line {line}")
        |> List.ofArray
    overrides :: exported

/// config/initializers/assets.rb sets `Rails.application.config.assets.version`.
let private assetsVersion (railsRoot: string) : string =
    let path = Path.Combine(railsRoot, "config/initializers/assets.rb")
    let initializer = if File.Exists path then File.ReadAllText path else ""
    initializer.Split '\n'
    |> Seq.filter (fun line -> not (line.TrimStart().StartsWith '#'))
    |> Seq.tryPick (fun line ->
        match line.IndexOf "config.assets.version" with
        | -1 -> None
        | i ->
            let value = line.Substring(i + "config.assets.version".Length).TrimStart()
            if value.StartsWith '=' then Some(value.Substring(1).Trim().Trim('"', '\'')) else None)
    |> Option.defaultValue "1"

/// Importmap::ImportmapTagsHelper#javascript_importmap_tags for the "application" entry point,
/// with no CSP nonce (the reference configures no content security policy).
let private importmapTags (loadPath: Propshaft.LoadPath) (digested: string[]) (railsRoot: string) : string =
    let resolve (path: string) = loadPath.Find path |> Option.map (fun index -> $"{Prefix}/{digested[index]}")
    let pins = Importmap.expand (Path.Combine(railsRoot, "config/importmap.rb")) railsRoot

    // Missing assets are skipped (Propshaft::MissingAssetError is a rescuable asset error).
    let imports = pins |> List.choose (fun pin -> resolve pin.Path |> Option.map (fun path -> pin.Name, path))
    let importsJson =
        if imports.IsEmpty then
            "{\n  \"imports\": {}\n}"
        else
            let lines = imports |> List.map (fun (name, path) -> $"    {json name}: {json path}")
            "{\n  \"imports\": {\n" + String.concat ",\n" lines + "\n  }\n}"

    let preloads = ResizeArray<string>()
    for pin in pins |> List.filter (fun pin -> pin.Preload) do
        match resolve pin.Path with
        | Some path when not (preloads.Contains path) -> preloads.Add path
        | _ -> ()

    [ $"<script type=\"importmap\" data-turbo-track=\"reload\">{importsJson}</script>"
      preloads |> Seq.map (fun path -> $"<link rel=\"modulepreload\" href=\"{Erb.htmlEscape path}\">") |> String.concat "\n"
      "<script type=\"module\">import \"application\"</script>" ]
    |> String.concat "\n"

let private allFiles (dir: string) : string list =
    let rec walk (dir: string) =
        [ for entry in Directory.EnumerateFileSystemEntries dir do
              if Directory.Exists entry then yield! walk entry else yield entry ]
    if Directory.Exists dir then walk dir |> List.sortWith Propshaft.comparePaths else []

/// Rewrites a file only when its content changes, so an unchanged output keeps its timestamp.
let private writeIfChanged (path: string) (bytes: byte[]) : unit =
    if not (File.Exists path && File.ReadAllBytes path = bytes) then File.WriteAllBytes(path, bytes)

let private run (railsRoot: string) (crateDir: string) (outDir: string) : unit =
    let loadPath = Propshaft.LoadPath(loadPathDirs crateDir railsRoot, assetsVersion railsRoot, Prefix)
    let assets = loadPath.Assets

    // Every body goes into one blob, at an offset.
    let blob = new MemoryStream()
    let add (bytes: byte[]) : struct (int * int) =
        let offset = int blob.Position
        blob.Write bytes
        struct (offset, bytes.Length)

    let digested = Array.init assets.Count loadPath.DigestedPath
    let assetBodies =
        Array.init assets.Count (fun index ->
            add (loadPath.CompiledContent index |> Option.defaultValue assets[index].Content))

    // Propshaft::Processor#write_manifest (the order is the load path's, not readdir's).
    let manifestJson =
        "{"
        + String.Join(
            ",",
            Seq.init assets.Count (fun i -> $"{json assets[i].LogicalPath}:{{\"digested_path\":{json digested[i]},\"integrity\":null}}")
        )
        + "}"

    let byLogical = Array.init assets.Count id |> Array.sortWith (fun a b -> String.CompareOrdinal(assets[a].LogicalPath, assets[b].LogicalPath))

    // Propshaft::Helper#all_stylesheets_paths: every text/css asset's logical path, sorted.
    let stylesheets = byLogical |> Array.filter (fun i -> Propshaft.extname assets[i].LogicalPath = ".css") |> Array.map (fun i -> assets[i].LogicalPath)

    // Everything ActionDispatch::Static can serve: reference/public plus the precompiled
    // public/assets (the digested files and the manifest), sorted by URL path.
    let publicDir = Path.Combine(railsRoot, "public")
    let publicAssets = Path.Combine(publicDir, "assets") + "/"
    // A stray precompile inside the submodule mustn't shadow what we build.
    let publicFiles = allFiles publicDir |> List.filter (fun f -> not (f.StartsWith(publicAssets, StringComparison.Ordinal)))
    let files =
        [ for file in publicFiles -> $"/{file.Substring(publicDir.TrimEnd('/').Length + 1)}", add (File.ReadAllBytes file)
          for i in 0 .. assets.Count - 1 -> $"{Prefix}/{digested[i]}", assetBodies[i]
          yield $"{Prefix}/.manifest.json", add (Encoding.UTF8.GetBytes manifestJson) ]
        // A stable sort, so a public file wins over an asset of the same URL, as in Rust.
        |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
        |> List.fold (fun acc ((url, _) as f) -> match acc with | (last, _) :: _ when last = url -> acc | _ -> f :: acc) []
        |> List.rev

    let code = StringBuilder()
    let line (s: string) = code.Append(s).Append('\n') |> ignore
    line "// <auto-generated> by src/Campfire.Assets.Build (the counterpart of crates/assets/build.rs); do not edit."
    line "module internal Campfire.Assets.EmbeddedData"
    line ""
    line "/// (logical path, digested path), sorted by logical path."
    line "let manifest: (string * string)[] ="
    line "    [|"
    for i in byLogical do
        line $"        ({fsString assets[i].LogicalPath}, {fsString digested[i]})"
    line "    |]"
    line ""
    line "let stylesheets: string[] ="
    line "    [|"
    for s in stylesheets do
        line $"        {fsString s}"
    line "    |]"
    line ""
    line "/// (URL path, offset in assets.bin, length), sorted by URL path."
    line "let files: struct (string * int * int)[] ="
    line "    [|"
    for url, struct (offset, length) in files do
        line $"        struct ({fsString url}, {offset}, {length})"
    line "    |]"
    line ""
    line $"let builtAt = {fsString (httpdate (buildTime ()))}"

    Directory.CreateDirectory outDir |> ignore
    writeIfChanged (Path.Combine(outDir, "assets.bin")) (blob.ToArray())
    writeIfChanged (Path.Combine(outDir, "importmap-tags.html")) (Encoding.UTF8.GetBytes(importmapTags loadPath digested railsRoot))
    writeIfChanged (Path.Combine(outDir, "EmbeddedData.fs")) (UTF8Encoding(false).GetBytes(code.ToString()))

    printfn "Campfire.Assets.Build: %d assets, %d servable files, %d bytes" assets.Count files.Length blob.Length

[<EntryPoint>]
let main argv =
    let rec parse (args: string list) (acc: Map<string, string>) =
        match args with
        | [] -> acc
        | key :: value :: rest when key.StartsWith "--" -> parse rest (acc.Add(key.Substring 2, value))
        | other -> failwith $"usage: Campfire.Assets.Build --reference <dir> --assets <dir> --out <dir> (got {other})"
    let args = parse (List.ofArray argv) Map.empty
    let get key = match args.TryFind key with | Some v -> Path.GetFullPath v | None -> failwith $"missing --{key}"
    let railsRoot = get "reference"
    if not (Directory.Exists railsRoot) then failwith "reference/ submodule is missing"
    run railsRoot (get "assets") (get "out")
    0
