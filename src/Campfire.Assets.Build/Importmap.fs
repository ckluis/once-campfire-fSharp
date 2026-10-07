// Port of rust/crates/assets/build/importmap.rs
/// A port of importmap-rails 2.2.2's Importmap::Map (lib/importmap/map.rb) for the subset of the
/// config/importmap.rb DSL the reference uses: `pin` and `pin_all_from` with `to:`, `under:` and
/// `preload:`.
module Campfire.Assets.Build.Importmap

open System
open System.IO

type Pin = { Name: string; Path: string; Preload: bool }

type private Entry =
    | PinEntry of name: string * ``to``: string option * preload: bool
    | Dir of dir: string * under: string option * ``to``: string option * preload: bool

type private Value =
    | Str of string
    | Bool of bool

/// The parsing state over the rest of a line, which Rust keeps as a `&str` it advances.
type private Args(line: string) =
    let mutable rest = line

    member _.Rest = rest

    member _.String() : string option =
        if rest.Length > 0 && (rest[0] = '"' || rest[0] = '\'') then
            let quote = rest[0]
            match rest.IndexOf(quote, 1) with
            | -1 -> None
            | end' ->
                let value = rest.Substring(1, end' - 1)
                rest <- rest.Substring(end' + 1).TrimStart()
                Some value
        else
            None

    member this.Option() : (string * Value) option =
        if rest.Length = 0 || rest[0] = '#' then
            None
        else
            if not (rest.StartsWith ',') then failwith $"importmap.rb: can't parse {rest}"
            let afterComma = rest.Substring(1).TrimStart()
            let colon = afterComma.IndexOf ':'
            if colon < 0 then failwith "importmap.rb: expected key: value"
            let key = afterComma.Substring(0, colon)
            rest <- afterComma.Substring(colon + 1).TrimStart()
            let value =
                match this.String() with
                | Some s -> Str s
                | None ->
                    if rest.StartsWith "true" then
                        rest <- rest.Substring(4).TrimStart()
                        Bool true
                    elif rest.StartsWith "false" then
                        rest <- rest.Substring(5).TrimStart()
                        Bool false
                    else
                        failwith $"importmap.rb: unsupported value {rest}"
            Some(key.Trim(), value)

let private parseLine (line: string) : Entry option =
    let line = line.Trim()
    if line = "" || line.StartsWith '#' then
        None
    else
        let split = line |> Seq.tryFindIndex Char.IsWhiteSpace
        let command, rest =
            match split with
            | Some i -> line.Substring(0, i), line.Substring(i + 1)
            | None -> line, ""
        let args = Args(rest.TrimStart())
        let first =
            match args.String() with
            | Some s -> s
            | None -> failwith $"importmap.rb: expected a string in {line}"
        let mutable ``to`` = None
        let mutable under = None
        let mutable preload = true
        let mutable more = true
        while more do
            match args.Option() with
            | None -> more <- false
            | Some("to", Str v) -> ``to`` <- Some v
            | Some("under", Str v) -> under <- Some v
            | Some("preload", Bool v) -> preload <- v
            | Some(key, _) -> failwith $"importmap.rb: unsupported option {key} in {line}"
        match command with
        | "pin" -> Some(PinEntry(first, ``to``, preload))
        | "pin_all_from" -> Some(Dir(first, under, ``to``, preload))
        | other -> failwith $"importmap.rb: unsupported statement {other}"

/// A later entry for an existing name replaces it in place (a Ruby Hash keeps its position).
let private insert (packages: ResizeArray<Pin>) (pin: Pin) : unit =
    match packages.FindIndex(fun p -> p.Name = pin.Name) with
    | -1 -> packages.Add pin
    | i -> packages[i] <- pin

/// `[under, filename.chomp(extname).remove(/(?:\/|^)index$/).presence].compact.join("/")`
let private moduleNameFrom (filename: string) (under: string option) : string =
    let ext = Propshaft.extname filename
    let stem = if ext <> "" && filename.EndsWith(ext, StringComparison.Ordinal) then filename.Substring(0, filename.Length - ext.Length) else filename
    let stem =
        if stem = "index" then ""
        elif stem.EndsWith "/index" then stem.Substring(0, stem.Length - 6)
        else stem
    [ under; (if stem = "" then None else Some stem) ] |> List.choose id |> String.concat "/"

/// `Dir[path.join("**/*.js{,m}")]`: Dir globs skip dotfiles and dot-directories.
let private javascriptFilesInTree (dir: string) : string list =
    let rec walk (dir: string) =
        [ for path in Directory.EnumerateFileSystemEntries dir do
              let name = Propshaft.fileName path
              if not (name.StartsWith '.') then
                  if Directory.Exists path then yield! walk path
                  elif name.EndsWith ".js" || name.EndsWith ".jsm" then yield path ]
    walk dir

/// Importmap::Map#expanded_packages_and_directories: pins in insertion order, then every
/// directory expanded; a later entry for an existing name keeps its position (a Ruby Hash).
let expand (importmapRb: string) (railsRoot: string) : Pin list =
    let source = File.ReadAllText importmapRb
    let packages = ResizeArray<Pin>()
    let directories = ResizeArray<string * string option * string option * bool>()

    for entry in source.Split '\n' |> Seq.choose parseLine do
        match entry with
        | PinEntry(name, ``to``, preload) ->
            let path = ``to`` |> Option.defaultValue $"{name}.js"
            insert packages { Name = name; Path = path; Preload = preload }
        | Dir(dir, under, ``to``, preload) ->
            directories.RemoveAll(fun (d, _, _, _) -> d = dir) |> ignore
            directories.Add((dir, under, ``to``, preload))

    for dir, under, ``to``, preload in directories do
        let root = Path.Combine(railsRoot, dir)
        if Directory.Exists root then
            for file in javascriptFilesInTree root |> List.sortWith Propshaft.comparePaths do
                let filename = file.Substring(root.TrimEnd('/').Length + 1)
                let name = moduleNameFrom filename under
                let path =
                    [ ``to`` |> Option.orElse under; Some filename ]
                    |> List.choose id
                    |> List.filter (fun s -> s <> "")
                    |> String.concat "/"
                insert packages { Name = name; Path = path; Preload = preload }

    List.ofSeq packages
