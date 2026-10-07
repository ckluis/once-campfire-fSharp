// Port of rust/crates/ruby/src/rack.rs
/// What Rack (3.2) does with request headers, where Ruby's string behaviour shows through.
module Campfire.Ruby.Rack

open System
open Campfire.Ruby.Integer

/// `http_range =~ /bytes=([^;]+)/`: after the first `bytes=` that something other than `;`
/// follows, up to the next `;`.
let private rangeSpec (header: string) : string option =
    let mutable from = 0
    let mutable found = None
    let mutable searching = true
    while searching do
        let i = header.IndexOf("bytes=", from, StringComparison.Ordinal)
        if i < 0 then
            searching <- false
        else
            let rest = header.Substring(i + 6)
            let semi = rest.IndexOf ';'
            let spec = if semi < 0 then rest else rest.Substring(0, semi)
            if spec.Length > 0 then
                found <- Some spec
                searching <- false
            else
                from <- i + 6
    found

/// `/,[ \t]*/`
let private splitComma (s: string) : struct (int * int) voption =
    let i = s.IndexOf ','
    if i < 0 then
        ValueNone
    else
        let mutable e = i + 1
        while e < s.Length && (s[e] = ' ' || s[e] = '\t') do
            e <- e + 1
        ValueSome(struct (i, e))

/// `String#split` with a separator finder: trailing empty fields are dropped.
let private rubySplit (s: string) (find: string -> struct (int * int) voption) : string list =
    let fields = ResizeArray<string>()
    let mutable rest = s
    let mutable go = true
    while go do
        match find rest with
        | ValueSome(struct (start, ``end``)) ->
            fields.Add(rest.Substring(0, start))
            rest <- rest.Substring ``end``
        | ValueNone -> go <- false
    fields.Add rest
    while fields.Count > 0 && fields[fields.Count - 1] = "" do
        fields.RemoveAt(fields.Count - 1)
    List.ofSeq fields

let private findDash (s: string) : struct (int * int) voption =
    let i = s.IndexOf '-'
    if i < 0 then ValueNone else ValueSome(struct (i, i + 1))

let private countOf (c: char) (s: string) =
    let mutable n = 0
    for x in s do
        if x = c then n <- n + 1
    n

/// `Rack::Utils.get_byte_ranges(http_range, size)`: `None` means "serve everything" and an empty
/// list means 416. The ranges are inclusive and within `size`.
let byteRanges (header: string option) (size: uint64) : (uint64 * uint64) list option =
    if size = 0UL then
        None
    else
        match header |> Option.bind rangeSpec with
        | None -> None
        | Some spec when countOf ',' spec >= 100 -> None
        | Some spec ->
            let size = Int128.CreateChecked size
            let one = Int128.One
            // None is the early `return None` of the Rust loop.
            let rec go (specs: string list) (acc: (Int128 * Int128) list) : (Int128 * Int128) list option =
                match specs with
                | [] -> Some(List.rev acc)
                | rangeSpec :: rest ->
                    if not (rangeSpec.Contains '-') then
                        None
                    else
                        // Split on "-" first, so neither end can be negative.
                        let parts = rubySplit rangeSpec findDash
                        let r0 = List.tryItem 0 parts
                        let r1 = List.tryItem 1 parts
                        let bounds =
                            match r0 with
                            | None
                            | Some "" ->
                                r1 |> Option.map (fun r1 -> (Int128.Max(size - toI128 r1, Int128.Zero), size - one))
                            | Some r0 ->
                                let r0 = toI128 r0
                                match r1 with
                                | None -> Some(r0, size - one)
                                | Some r1 ->
                                    let r1 = toI128 r1
                                    if r1 < r0 then None else Some(r0, Int128.Min(r1, size - one))
                        match bounds with
                        | None -> None
                        | Some(a, b) -> go rest (if a <= b then (a, b) :: acc else acc)
            match go (rubySplit spec splitComma) [] with
            | None -> None
            | Some ranges ->
                let total = ranges |> List.sumBy (fun (a, b) -> b - a + one)
                if total > size then Some []
                else Some(ranges |> List.map (fun (a, b) -> (uint64 a, uint64 b)))
