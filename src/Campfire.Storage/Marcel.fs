// Port of rust/crates/storage/src/marcel.rs
/// Marcel 1.1.0 content type identification (`Marcel::MimeType.for`), over tables dumped from the
/// reference bundle (`reference-tools/storage/dump_tables.rb marcel-fsharp` -> `Tables.fs`).
module Campfire.Storage.Marcel

open System
open System.Collections.Generic
open Campfire.Storage.Tables

[<Literal>]
let Binary = "application/octet-stream"

let private lookup (table: (string * 'v)[]) : Dictionary<string, 'v> =
    let dictionary = Dictionary<string, 'v>(table.Length, StringComparer.Ordinal)
    for key, value in table do
        dictionary[key] <- value
    dictionary

let private extensionTypes = lazy (lookup extensions)
let private typeExtensions = lazy (lookup typeExts)
let private typeParentTypes = lazy (lookup typeParents)

/// `str::to_lowercase`. .NET's invariant lowercasing agrees except for U+0130, which Rust turns
/// into "i" and a combining dot (and a capital sigma ending a word, which no table entry holds).
let private toLowercase (s: string) : string =
    let lowered = s.ToLowerInvariant()
    if lowered.IndexOf '\u0130' >= 0 then lowered.Replace("\u0130", "i\u0307") else lowered

/// `Marcel::Magic.by_extension`: case-insensitive, with or without the leading dot.
let byExtension (extension: string) : string option =
    let ext = toLowercase extension
    let ext = if ext.StartsWith('.') then ext.Substring 1 else ext
    match extensionTypes.Value.TryGetValue ext with
    | true, contentType -> Some contentType
    | _ -> None

/// `Marcel::Magic.by_path`: the extension per Ruby's `File.extname`.
let byPath (path: string) : string option = byExtension (Filename.extname path)

/// `Marcel::Magic.new(type).extensions`.
let extensionsOf (contentType: string) : string[] =
    match typeExtensions.Value.TryGetValue contentType with
    | true, exts -> exts
    | _ -> [||]

let private parents (contentType: string) : string[] =
    match typeParentTypes.Value.TryGetValue contentType with
    | true, found -> found
    | _ -> [||]

/// `Marcel::Magic.child?`.
let rec isChild (child: string) (parent: string) : bool =
    child = parent || parents child |> Array.exists (fun p -> isChild p parent)

let private prefixLength =
    lazy
        (let rec reach (matches: Match[]) : int =
            matches
            |> Array.map (fun m ->
                let value = m.Value |> Option.map Array.length |> Option.defaultValue 0
                let own =
                    match m.RangeEnd with
                    | Some stop -> stop + value
                    | None -> m.Offset + value
                max own (reach m.Children))
            |> Array.fold max 0
         magic |> Array.map (fun (_, matches) -> reach matches) |> Array.fold max 0)

/// How many leading bytes [`byMagic`] can look at: identifying the first `magicPrefixLen ()`
/// bytes of a file gives the same answer as identifying all of it.
let magicPrefixLen () : int = prefixLength.Value

/// `IO#read(length)` after skipping `offset` bytes: `nil` at EOF, otherwise up to `length` bytes.
let private read (data: byte[]) (offset: int) (length: int) : ReadOnlyMemory<byte> voption =
    if length = 0 then ValueSome ReadOnlyMemory.Empty
    elif offset >= data.Length then ValueNone
    else ValueSome(ReadOnlyMemory(data, offset, min length (data.Length - offset)))

let rec private matchesAny (data: byte[]) (matches: Match[]) : bool =
    matches
    |> Array.exists (fun m ->
        match m.Value with
        | None -> false
        | Some value ->
            let hit =
                match m.RangeEnd with
                // `io.read(offset.begin); io.read(offset.end - offset.begin + value.bytesize).include?(value)`
                | Some stop ->
                    match read data m.Offset (stop - m.Offset + value.Length) with
                    | ValueSome window -> value.Length = 0 || window.Span.IndexOf(ReadOnlySpan value) >= 0
                    | ValueNone -> false
                | None ->
                    match read data m.Offset value.Length with
                    | ValueSome bytes -> bytes.Span.SequenceEqual(ReadOnlySpan value)
                    | ValueNone -> false
            hit && (m.Children.Length = 0 || matchesAny data m.Children))

/// `Marcel::Magic.by_magic`: the first table entry whose matches hit.
let byMagic (data: byte[]) : string option =
    magic
    |> Array.tryFind (fun (_, matches) -> matchesAny data matches)
    |> Option.map (fun (contentType, _) -> toLowercase contentType)

/// Declared types are downcased, stripped of parameters, and ignored when binary.
let private forDeclaredType (declaredType: string option) : string option =
    declaredType
    |> Option.bind (fun declared ->
        let declared = toLowercase declared
        let mediaType = declared.Split([| ';'; ','; ' '; '\t'; '\n'; '\r'; '\u000b'; '\u000c' |]).[0]
        if mediaType.Contains '/' && mediaType <> Binary then Some mediaType else None)

/// `most_specific_type(*candidates, BINARY)`: later candidates only win when they are children
/// of the current pick.
let private mostSpecificType (candidates: string option list) : string =
    let unique = ResizeArray<string>()
    for candidate in (candidates |> List.choose id) @ [ Binary ] do
        if not (unique.Contains candidate) then unique.Add candidate
    let mutable pick = unique[0]
    for i in 1 .. unique.Count - 1 do
        if isChild unique[i] pick then pick <- unique[i]
    pick

/// `Marcel::MimeType.for(io, name:, declared_type:)`, as `ActiveStorage::Blob#extract_content_type`
/// calls it on upload.
let identify (data: byte[]) (name: string option) (declaredType: string option) : string =
    let filenameType = name |> Option.bind byPath
    mostSpecificType [ byMagic data; forDeclaredType declaredType; filenameType ]

/// `Marcel::MimeType.for(extension:)`.
let forExtension (extension: string) : string = mostSpecificType [ byExtension extension ]
