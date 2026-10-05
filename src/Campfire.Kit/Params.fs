// Port of rust/crates/kit/src/params.rs
//
// Rails request parameters.
//
// This is a port of `ActionDispatch::ParamBuilder` (Rails 8.2, itself derived from
// `Rack::QueryParser`) plus the pair splitting of `ActionDispatch::QueryParser.each_pair` and
// `Rack::QueryParser#parse_query_pairs`, the deep munging applied to JSON bodies
// (`Request::Utils::NoNilParamEncoder`), and the strong-parameters subset Campfire uses
// (`require`, `permit`, `fetch`).
//
// `Params.parseNested` exposes the query-string parser as JSON for differential tests against
// `ActionDispatch::ParamBuilder.from_query_string` in the reference container.
//
// Values are built once while a request is parsed and read afterwards: nothing mutates a nested
// hash or array after `fromPairs` returns, so `ParamMap.Clone` copies one level and shares the rest
// (Rust clones deeply).
namespace Campfire.Kit

open System
open System.Buffers
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Text.Unicode
open Campfire.RailsCompat
open Campfire.Kit

/// A JSON number: an i64, a u64 past it, or an f64, as serde_json's are.
[<Struct>]
type JsonNumber =
    | NInt of i: int64
    | NUInt of u: uint64
    | NFloat of f: float

module JsonNumber =
    /// serde_json's `Number::to_string`: integers in decimal, floats as ryu writes them (`1.0`,
    /// `0.0001`, `1e21`, `1.5e-7`).
    let toString (n: JsonNumber) : string =
        match n with
        | NInt i -> i.ToString(CultureInfo.InvariantCulture)
        | NUInt u -> u.ToString(CultureInfo.InvariantCulture)
        | NFloat f ->
            if f = 0.0 then
                (if Double.IsNegative f then "-0.0" else "0.0")
            else
                // The shortest round-trip digits and the power of ten of the last one.
                let shortest = Math.Abs(f).ToString("R", CultureInfo.InvariantCulture)
                let mantissa, exponent =
                    match shortest.IndexOf 'E' with
                    | -1 -> shortest, 0
                    | e -> shortest.Substring(0, e), Int32.Parse(shortest.Substring(e + 1), CultureInfo.InvariantCulture)
                let dot = mantissa.IndexOf '.'
                let integerLength = if dot < 0 then mantissa.Length else dot
                let raw = mantissa.Replace(".", "")
                let trimmedLeading = raw.TrimStart '0'
                let leadingZeros = raw.Length - trimmedLeading.Length
                let digits = trimmedLeading.TrimEnd '0'
                // value = 0.DIGITS * 10^point
                let point = integerLength - leadingZeros + exponent
                let k = point - digits.Length
                let length = digits.Length
                let kk = length + k
                let body =
                    if 0 <= k && kk <= 16 then
                        digits + String('0', k) + ".0"
                    elif 0 < kk && kk <= 16 then
                        digits.Substring(0, kk) + "." + digits.Substring kk
                    elif -5 < kk && kk <= 0 then
                        "0." + String('0', -kk) + digits
                    else
                        let first = digits.Substring(0, 1)
                        let rest = if length > 1 then "." + digits.Substring 1 else ""
                        $"{first}{rest}e{kk - 1}"
                (if f < 0.0 then "-" else "") + body

/// A file part of a multipart body (`ActionDispatch::Http::UploadedFile`), spooled to a temp
/// file that's deleted when the request is done (`Rack::TempfileReaper`).
[<Sealed>]
type UploadedFile(originalFilename: string, contentType: string | null, headers: string, size: int64, path: string) =
    let mutable deleted = false

    member _.OriginalFilename = originalFilename
    member _.ContentType = contentType

    /// The raw part headers (`UploadedFile#headers`).
    member _.Headers = headers
    member _.Size = size
    member _.Path = path

    member _.Read() : byte[] = File.ReadAllBytes path

    /// Spool `bytes` to a temp file; handy for tests and for bot raw-body attachments.
    static member FromBytes(originalFilename: string, contentType: string | null, bytes: ReadOnlySpan<byte>) : UploadedFile =
        let path = UploadedFile.NewTempPath()
        use file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)
        file.Write bytes
        new UploadedFile(originalFilename, contentType, "", int64 bytes.Length, path)

    /// A new, empty temp file named as Rack names them (`RackMultipart...`).
    static member NewTempPath() : string =
        Path.Combine(Path.GetTempPath(), "RackMultipart" + Guid.NewGuid().ToString("N"))

    /// Delete the spooled file; a second call does nothing.
    member _.Delete() =
        if not deleted then
            deleted <- true
            try
                File.Delete path
            with
            | :? IOException
            | :? UnauthorizedAccessException -> ()

    interface IDisposable with
        member this.Dispose() = this.Delete()

    override this.Equals(other: obj) =
        match other with
        | :? UploadedFile as other -> String.Equals(path, other.Path, StringComparison.Ordinal)
        | _ -> false

    override _.GetHashCode() = path.GetHashCode()

/// A parameter value: what a Rails params hash can hold.
[<CustomEquality; NoComparison>]
type Param =
    | Null
    | Bool of bool
    | Number of JsonNumber
    | Str of string
    | File of UploadedFile
    | Array of ResizeArray<Param>
    | Hash of ParamMap

    override this.Equals(other: obj) =
        match other with
        | :? Param as other ->
            match this, other with
            | Null, Null -> true
            | Bool a, Bool b -> a = b
            | Number a, Number b -> a = b
            | Str a, Str b -> String.Equals(a, b, StringComparison.Ordinal)
            | File a, File b -> a.Equals b
            | Array a, Array b -> a.Count = b.Count && Seq.forall2 (fun (x: Param) y -> x.Equals y) a b
            | Hash a, Hash b -> a.Equals b
            | _ -> false
        | _ -> false

    override this.GetHashCode() =
        match this with
        | Null -> 0
        | Bool b -> hash b
        | Number n -> hash n
        | Str s -> s.GetHashCode()
        | File f -> f.GetHashCode()
        | Array items -> items.Count
        | Hash map -> map.Count

    member this.AsStr: string | null = match this with Str s -> s | _ -> null

    member this.AsHash: ParamMap | null = match this with Hash map -> map | _ -> null

    member this.AsArray: ResizeArray<Param> | null = match this with Array items -> items | _ -> null

    member this.AsFile: UploadedFile | null = match this with File f -> f | _ -> null

    /// `params[:a][:b]` style lookup; ValueNone for anything that isn't a hash.
    member this.Get(key: string) : Param voption =
        match this with
        | Hash map -> map.Get key
        | _ -> ValueNone

    /// The value as Rails would interpolate it (`to_s`): strings as-is, numbers and booleans
    /// formatted, nil as "". Null for the rest.
    member this.ToS() : string | null =
        match this with
        | Null -> ""
        | Bool b -> if b then "true" else "false"
        | Number n -> JsonNumber.toString n
        | Str s -> s
        | _ -> null

    /// ActiveSupport's `blank?`: nil, false, whitespace-only strings, and empty collections.
    member this.IsBlank: bool =
        match this with
        | Null -> true
        | Bool b -> not b
        | Number _
        | File _ -> false
        | Str s -> Seq.forall Char.IsWhiteSpace s
        | Array items -> items.Count = 0
        | Hash map -> map.Count = 0

    member this.IsPresent: bool = not this.IsBlank

    /// The strong-parameters notion of a permitted scalar.
    member this.IsPermittedScalar: bool =
        match this with
        | Array _
        | Hash _ -> false
        | _ -> true

    member this.ToJson() : Value =
        match this with
        | Null -> Value.Null
        | Bool b -> Value.Bool b
        | Number(NInt i) -> Value.Int i
        | Number(NUInt u) -> Value.UInt u
        | Number(NFloat f) -> Value.Float f
        | Str s -> Value.String s
        | File file ->
            Value.Object
                [ "original_filename", Value.String file.OriginalFilename
                  "content_type",
                  (match file.ContentType with
                   | null -> Value.Null
                   | ct -> Value.String ct) ]
        | Array items -> Value.Array [ for item in items -> item.ToJson() ]
        | Hash map -> map.ToJson()

    /// `params.require(:key).permit(...)`'s second half: keep only the permitted keys.
    member this.Permit(filters: Permit list) : ParamMap =
        match this with
        | Hash map -> map.Permit filters
        | _ -> ParamMap()

/// An insertion-ordered string-keyed map, like the Ruby `Hash` behind Rails params. Past a few
/// keys it is indexed too, so params with many keys (a large JSON body, a long query string) cost
/// linear time, not quadratic.
and [<Sealed>] ParamMap() =
    static let indexAfter = 8
    let mutable keys: string[] = Array.zeroCreate 4
    let mutable values: Param[] = Array.zeroCreate 4
    let mutable count = 0
    let mutable index: Dictionary<string, int> | null = null

    member _.Count = count

    member _.IsEmpty = count = 0

    member _.KeyAt(i: int) : string = keys[i]
    member _.ValueAt(i: int) : Param = values[i]

    member private _.Find(key: string) : int =
        match index with
        | null ->
            let mutable found = -1
            let mutable i = 0
            while found < 0 && i < count do
                if String.Equals(keys[i], key, StringComparison.Ordinal) then found <- i
                i <- i + 1
            found
        | index ->
            match index.TryGetValue key with
            | true, i -> i
            | _ -> -1

    member this.Get(key: string) : Param voption =
        match this.Find key with
        | -1 -> ValueNone
        | i -> ValueSome values[i]

    /// The string value of `key`, null when it is missing or not a string.
    member this.Str(key: string) : string | null =
        match this.Find key with
        | -1 -> null
        | i -> values[i].AsStr

    member this.ContainsKey(key: string) : bool = this.Find key >= 0

    /// Ruby's `hash[key] = value`: replaces in place, keeping the original position.
    member this.Insert(key: string, value: Param) : unit =
        match this.Find key with
        | -1 ->
            if count = keys.Length then
                Array.Resize(&keys, count * 2)
                Array.Resize(&values, count * 2)
            keys[count] <- key
            values[count] <- value
            match index with
            | null -> ()
            | index -> index[key] <- count
            count <- count + 1
            if isNull index && count > indexAfter then
                let built = Dictionary<string, int>(count * 2, StringComparer.Ordinal)
                for i in 0 .. count - 1 do
                    built[keys[i]] <- i
                index <- built
        | i -> values[i] <- value

    /// `shift_remove`: the entry goes and the ones after it keep their order.
    member this.Remove(key: string) : Param voption =
        match this.Find key with
        | -1 -> ValueNone
        | at ->
            let removed = values[at]
            for i in at .. count - 2 do
                keys[i] <- keys[i + 1]
                values[i] <- values[i + 1]
            count <- count - 1
            keys[count] <- Unchecked.defaultof<string>
            values[count] <- Param.Null
            match index with
            | null -> ()
            | _ ->
                let rebuilt = Dictionary<string, int>(count * 2, StringComparer.Ordinal)
                for i in 0 .. count - 1 do
                    rebuilt[keys[i]] <- i
                index <- rebuilt
            ValueSome removed

    member this.Keys: string seq = seq { for i in 0 .. count - 1 -> keys[i] }

    member this.Iter: struct (string * Param) seq = seq { for i in 0 .. count - 1 -> struct (keys[i], values[i]) }

    /// Ruby's `Hash#merge!`: later values win, existing keys keep their position.
    member this.Merge(other: ParamMap) : unit =
        for i in 0 .. other.Count - 1 do
            this.Insert(other.KeyAt i, other.ValueAt i)

    /// One level deep: nested values are shared (see the note at the top of this file).
    member this.Clone() : ParamMap =
        let copy = ParamMap()
        copy.Merge this
        copy

    /// `params.require(:key)`: the value when it's present (or `false`), else `ParameterMissing`.
    member this.Require(key: string) : Result<Param, Error> =
        match this.Get key with
        | ValueSome value when value.IsPresent || (match value with Bool false -> true | _ -> false) -> Ok value
        | _ -> Error(ParameterMissing key)

    /// `params.fetch(:key, default)`.
    member this.Fetch(key: string, ``default``: Param) : Param =
        match this.Get key with
        | ValueSome value -> value
        | ValueNone -> ``default``

    /// `ActionController::Parameters#permit`, for the filter shapes Campfire uses.
    member this.Permit(filters: Permit list) : ParamMap =
        let permitted = ParamMap()
        for filter in filters do
            match filter with
            | Permit.Key key ->
                match this.Get key with
                | ValueSome value when value.IsPermittedScalar -> permitted.Insert(key, value)
                | _ -> ()
                // Multi-parameter attributes, e.g. `born_on(1i)`.
                for i in 0 .. count - 1 do
                    if ParamMap.IsMultiParameterKey(keys[i], key) && values[i].IsPermittedScalar then
                        permitted.Insert(keys[i], values[i])
            | Permit.ScalarArray key ->
                match this.Get key with
                | ValueSome(Param.Array items) when items |> Seq.forall (fun item -> item.IsPermittedScalar) ->
                    permitted.Insert(key, Param.Array(ResizeArray items))
                | _ -> ()
            | Permit.AnyHash key ->
                match this.Get key with
                | ValueSome(Param.Hash map) -> permitted.Insert(key, Param.Hash(ParamMap.PermitAny map))
                | _ -> ()
            | Permit.Nested(key, nested) ->
                match this.Get key with
                | ValueSome(Param.Hash map) when ParamMap.IsFieldsForStyle map ->
                    let each = ParamMap()
                    for i in 0 .. map.Count - 1 do
                        match map.ValueAt i with
                        | Param.Hash inner -> each.Insert(map.KeyAt i, Param.Hash(inner.Permit nested))
                        | _ -> ()
                    permitted.Insert(key, Param.Hash each)
                | ValueSome(Param.Hash map) -> permitted.Insert(key, Param.Hash(map.Permit nested))
                | ValueSome(Param.Array items) ->
                    let hashes = ResizeArray<Param>()
                    for item in items do
                        match item with
                        | Param.Hash m -> hashes.Add(Param.Hash(m.Permit nested))
                        | _ -> ()
                    permitted.Insert(key, Param.Array hashes)
                | _ -> ()
        permitted

    member this.ToJson() : Value =
        Value.Object [ for i in 0 .. count - 1 -> keys[i], values[i].ToJson() ]

    /// Equal when the same keys map to equal values in the same order, as Ruby hashes compare.
    override this.Equals(other: obj) =
        match other with
        | :? ParamMap as other ->
            count = other.Count
            && Seq.forall (fun i -> String.Equals(keys[i], other.KeyAt i, StringComparison.Ordinal) && values[i].Equals(other.ValueAt i)) (seq { 0 .. count - 1 })
        | _ -> false

    override this.GetHashCode() = count

    // /\A#{key}\(\d+[if]?\)\z/
    static member private IsMultiParameterKey(candidate: string, key: string) : bool =
        if
            candidate.Length > key.Length + 2
            && candidate.StartsWith(key, StringComparison.Ordinal)
            && candidate[key.Length] = '('
            && candidate[candidate.Length - 1] = ')'
        then
            let rest = candidate.Substring(key.Length + 1, candidate.Length - key.Length - 2)
            let digits = if rest.EndsWith 'i' || rest.EndsWith 'f' then rest.Substring(0, rest.Length - 1) else rest
            digits.Length > 0 && Seq.forall Char.IsAsciiDigit digits
        else
            false

    static member private IsFieldsForStyle(map: ParamMap) : bool =
        map.Count > 0
        && Seq.forall
            (fun i ->
                let k = map.KeyAt i
                let digits = if k.StartsWith '-' then k.Substring 1 else k
                digits.Length > 0
                && Seq.forall Char.IsAsciiDigit digits
                && (match map.ValueAt i with Param.Hash _ -> true | _ -> false))
            (seq { 0 .. map.Count - 1 })

    /// `permit(key: {})`: any scalars, arrays of scalars or hashes, recursively.
    static member private PermitAny(map: ParamMap) : ParamMap =
        let permitted = ParamMap()
        for i in 0 .. map.Count - 1 do
            let k = map.KeyAt i
            match map.ValueAt i with
            | Param.Hash inner -> permitted.Insert(k, Param.Hash(ParamMap.PermitAny inner))
            | Param.Array items ->
                let kept = ResizeArray<Param>()
                for item in items do
                    match item with
                    | Param.Hash inner -> kept.Add(Param.Hash(ParamMap.PermitAny inner))
                    | Param.Array _ -> ()
                    | scalar -> kept.Add scalar
                permitted.Insert(k, Param.Array kept)
            | scalar -> permitted.Insert(k, scalar)
        permitted

/// A `permit` filter.
and Permit =
    /// `:name`
    | Key of string
    /// `name: []`
    | ScalarArray of string
    /// `name: {}`
    | AnyHash of string
    /// `name: [ ... ]` / `name: { ... }`
    | Nested of string * Permit list

/// A decoded string: valid UTF-8 as text, anything else as the bytes it was.
[<Struct>]
type Decoded =
    { Text: string | null
      Invalid: byte[] | null }

/// A `key=value` pair after %-decoding but before the UTF-8 check, which Rails performs in the
/// builder (so pairs whose top-level key is empty are skipped without raising). `HasValue` is
/// false when there was no `=` (Rails gives nil); a file part has `File` set instead of a value.
[<Struct>]
type RawPair =
    { Key: Decoded
      Value: Decoded
      HasValue: bool
      File: UploadedFile | null }

module Params =
    /// Rails' `ActionDispatch::ParamBuilder.default` depth limit.
    [<Literal>]
    let DepthLimit = 100

    /// `Rack::QueryParser` limits, applied to form bodies (Rack parses those, not Rails).
    [<Literal>]
    let FormBytesizeLimit = 4194304

    [<Literal>]
    let FormParamsLimit = 4096

    /// `permit(&["a".into(), ...])`: `permitKeys [ "name"; "avatar" ]`.
    let permitKeys (keys: string list) : Permit list = keys |> List.map Permit.Key

    let private text (s: string) : Decoded = { Text = s; Invalid = null }

    /// A pair for a text field, as the multipart parser and the tests build them.
    let textPair (key: string) (value: string | null) : RawPair =
        { Key = text key
          Value =
            (match value with
             | null -> text ""
             | v -> text v)
          HasValue = not (isNull value)
          File = null }

    let filePair (key: string) (file: UploadedFile) : RawPair =
        { Key = text key
          Value = text ""
          HasValue = true
          File = file }

    let private hexValue (b: byte) : int =
        if b >= byte '0' && b <= byte '9' then int b - int '0'
        elif b >= byte 'a' && b <= byte 'f' then int b - int 'a' + 10
        elif b >= byte 'A' && b <= byte 'F' then int b - int 'A' + 10
        else -1

    /// Decoded bytes as text when they are UTF-8, else as the bytes.
    let private decoded (bytes: ReadOnlySpan<byte>) : Decoded =
        if Utf8.IsValid bytes then
            { Text = Encoding.UTF8.GetString bytes
              Invalid = null }
        else
            { Text = null
              Invalid = bytes.ToArray() }

    /// Ruby's `URI.decode_www_form_component`: `+` is a space, `%XX` is a byte, and a `%` not
    /// followed by two hex digits is an error.
    let decodeWwwFormComponent (s: ReadOnlySpan<byte>) : Result<Decoded, ParamError> =
        if s.IndexOfAny(byte '+', byte '%') < 0 then
            Ok(decoded s)
        else
            let rented = ArrayPool<byte>.Shared.Rent s.Length
            try
                let mutable length = 0
                let mutable i = 0
                let mutable bad = false
                while not bad && i < s.Length do
                    match s[i] with
                    | b when b = byte '+' ->
                        rented[length] <- byte ' '
                        length <- length + 1
                        i <- i + 1
                    | b when b = byte '%' ->
                        if i + 2 < s.Length && hexValue s[i + 1] >= 0 && hexValue s[i + 2] >= 0 then
                            rented[length] <- byte (hexValue s[i + 1] * 16 + hexValue s[i + 2])
                            length <- length + 1
                            i <- i + 3
                        else
                            bad <- true
                    | b ->
                        rented[length] <- b
                        length <- length + 1
                        i <- i + 1
                if bad then
                    Error(ParamError.Invalid $"invalid %%-encoding ({Encoding.UTF8.GetString s})")
                else
                    Ok(decoded (ReadOnlySpan<byte>(rented, 0, length)))
            finally
                ArrayPool<byte>.Shared.Return rented

    let private decodePair (part: ReadOnlySpan<byte>) : Result<RawPair, ParamError> =
        let eq = part.IndexOf(byte '=')
        let key = if eq < 0 then part else part.Slice(0, eq)
        match decodeWwwFormComponent key with
        | Error e -> Error e
        | Ok key ->
            if eq < 0 then
                Ok
                    { Key = key
                      Value = text ""
                      HasValue = false
                      File = null }
            else
                match decodeWwwFormComponent (part.Slice(eq + 1)) with
                | Error e -> Error e
                | Ok value ->
                    Ok
                        { Key = key
                          Value = value
                          HasValue = true
                          File = null }

    /// `qs.split('&')`, the first part as it is and the others without leading spaces, skipping
    /// empty parts: each one's decoded pair is added to `pairs`.
    let private decodePairs (qs: ReadOnlySpan<byte>) (pairs: ResizeArray<RawPair>) : Result<unit, ParamError> =
        let mutable rest = qs
        let mutable first = true
        let mutable failure = ValueNone
        let mutable go = true
        while go && failure.IsNone do
            let amp = rest.IndexOf(byte '&')
            let mutable part = if amp < 0 then rest else rest.Slice(0, amp)
            if not first then
                part <- part.TrimStart(byte ' ')
            first <- false
            if part.Length > 0 then
                match decodePair part with
                | Ok pair -> pairs.Add pair
                | Error e -> failure <- ValueSome e
            if amp < 0 then go <- false else rest <- rest.Slice(amp + 1)
        match failure with
        | ValueSome e -> Error e
        | ValueNone -> Ok()

    /// `ActionDispatch::QueryParser.each_pair`: split on `/& */`, skip empty parts, split on the first
    /// `=`, and `URI.decode_www_form_component` both halves. No size limits (Rails applies none to
    /// the query string).
    let queryPairs (qs: string) : Result<ResizeArray<RawPair>, ParamError> =
        let pairs = ResizeArray<RawPair>()
        if qs.Length = 0 then
            Ok pairs
        else
            let rented = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount qs.Length)
            try
                let length = Encoding.UTF8.GetBytes(qs, 0, qs.Length, rented, 0)
                match decodePairs (ReadOnlySpan<byte>(rented, 0, length)) pairs with
                | Ok() -> Ok pairs
                | Error e -> Error e
            finally
                ArrayPool<byte>.Shared.Return rented

    /// `Rack::Request#form_pairs` for an urlencoded body: Rack's limits, and the trailing `\0`
    /// Safari once appended is dropped.
    let formPairs (body: ReadOnlySpan<byte>) : Result<ResizeArray<RawPair>, ParamError> =
        if body.Length > FormBytesizeLimit then
            Error(ParamError.Limit $"total query size exceeds limit ({FormBytesizeLimit})")
        else
            let body = if body.Length > 0 && body[body.Length - 1] = 0uy then body.Slice(0, body.Length - 1) else body
            let total = body.Count(byte '&') + 1
            if total > FormParamsLimit then
                Error(ParamError.Limit $"total number of query parameters ({total}) exceeds limit ({FormParamsLimit})")
            elif not (Utf8.IsValid body) then
                // Raw non-UTF-8 bytes in a form body can't decode to valid UTF-8 params either.
                Error(ParamError.Invalid "Invalid encoding for parameter")
            else
                let pairs = ResizeArray<RawPair>()
                match decodePairs body pairs with
                | Ok() -> Ok pairs
                | Error e -> Error e

    /// What `store_nested_param` returned: the params hash it was given, a one-element array (for a
    /// trailing `[]` below the top level), or nil (for an empty key).
    type private Stored =
        | StoredParams
        | StoredArray of ResizeArray<Param>
        | StoredNil

    let private intoParam (stored: Stored) (pars: ParamMap) : Param =
        match stored with
        | StoredParams -> Param.Hash pars
        | StoredArray items -> Param.Array items
        | StoredNil -> Param.Null

    let private rubyClass (param: Param) : string =
        match param with
        | Param.Null -> "NilClass"
        | Param.Bool true -> "TrueClass"
        | Param.Bool false -> "FalseClass"
        | Param.Number _ -> "Integer"
        | Param.Str _ -> "String"
        | Param.File _ -> "ActionDispatch::Http::UploadedFile"
        | Param.Array _ -> "Array"
        | Param.Hash _ -> "ActiveSupport::HashWithIndifferentAccess"

    /// `params[k] ||= []` followed by the Array type check.
    let private arraySlot (pars: ParamMap) (k: string) : Result<ResizeArray<Param>, ParamError> =
        match pars.Get k with
        | ValueNone
        | ValueSome Param.Null -> pars.Insert(k, Param.Array(ResizeArray()))
        | _ -> ()
        match pars.Get k with
        | ValueSome(Param.Array items) -> Ok items
        | ValueSome other -> Error(ParamError.Type $"expected Array (got {rubyClass other}) for param `{k}'")
        | ValueNone -> failwith "unreachable"

    let private paramsHashHasKey (hash: ParamMap) (key: string) : bool =
        if key.Contains("[]", StringComparison.Ordinal) then
            false
        else
            // Ruby's `key.split(/[\[\]]+/)`; empty parts are skipped.
            let mutable current: ParamMap | null = hash
            let mutable result = true
            for part in key.Split([| '['; ']' |], StringSplitOptions.RemoveEmptyEntries) do
                if result then
                    match current with
                    | null -> result <- false
                    | map ->
                        match map.Get part with
                        | ValueSome value -> current <- value.AsHash
                        | ValueNone -> result <- false
            result

    let private findByte (s: string) (b: char) (from: int) : int =
        if from >= s.Length then -1 else s.IndexOf(b, from)

    let rec private storeNestedParam (pars: ParamMap) (name: string) (v: Param) (depth: int) : Result<Stored, ParamError> =
        if depth >= DepthLimit then
            Error ParamError.TooDeep
        else
            let k, after =
                if depth = 0 then
                    // Start of parsing, don't treat [] or [ at start of string specially.
                    match findByte name '[' 1 with
                    | -1 -> name, ""
                    | start -> name.Substring(0, start), name.Substring start
                elif name.StartsWith("[]", StringComparison.Ordinal) then
                    "[]", name.Substring 2
                elif name.StartsWith '[' && findByte name ']' 1 >= 0 then
                    let start = findByte name ']' 1
                    name.Substring(1, start - 1), name.Substring(start + 1)
                else
                    // Probably malformed input, nested but not starting with [.
                    name, ""
            if k.Length = 0 then
                Ok StoredNil
            elif after.Length = 0 then
                if k = "[]" && depth <> 0 then
                    Ok(StoredArray(if v.IsNull then ResizeArray() else ResizeArray [ v ]))
                else
                    pars.Insert(k, v)
                    Ok StoredParams
            elif after = "[" then
                pars.Insert(name, v)
                Ok StoredParams
            elif after = "[]" then
                match arraySlot pars k with
                | Error e -> Error e
                | Ok array ->
                    if not v.IsNull then array.Add v
                    Ok StoredParams
            elif after.StartsWith("[]", StringComparison.Ordinal) then
                // Recognize x[][y] (hash inside array) parameters; otherwise nest what follows the [].
                let nested = after.Substring 2
                let childKey =
                    if nested.Length >= 3 && nested[0] = '[' && nested[nested.Length - 1] = ']' then
                        let inner = nested.Substring(1, nested.Length - 2)
                        if inner.Contains '[' || inner.Contains ']' then nested else inner
                    else
                        nested
                match arraySlot pars k with
                | Error e -> Error e
                | Ok array ->
                    let reuse =
                        if array.Count > 0 then
                            match array[array.Count - 1] with
                            | Param.Hash last when not (paramsHashHasKey last childKey) -> ValueSome last
                            | _ -> ValueNone
                        else
                            ValueNone
                    match reuse with
                    | ValueNone ->
                        let child = ParamMap()
                        match storeNestedParam child childKey v (depth + 1) with
                        | Error e -> Error e
                        | Ok stored ->
                            array.Add(intoParam stored child)
                            Ok StoredParams
                    | ValueSome last ->
                        match storeNestedParam last childKey v (depth + 1) with
                        | Error e -> Error e
                        | Ok _ -> Ok StoredParams
            else
                let existing =
                    match pars.Get k with
                    | ValueNone
                    | ValueSome Param.Null -> Ok(ParamMap())
                    | ValueSome(Param.Hash existing) -> Ok existing
                    | ValueSome other -> Error(ParamError.Type $"expected Hash (got {rubyClass other}) for param `{k}'")
                match existing with
                | Error e -> Error e
                | Ok child ->
                    match storeNestedParam child after v (depth + 1) with
                    | Error e -> Error e
                    | Ok stored ->
                        pars.Insert(k, intoParam stored child)
                        Ok StoredParams

    /// `ActionDispatch::ParamBuilder.from_pairs`.
    let fromPairs (pairs: ResizeArray<RawPair>) : Result<ParamMap, ParamError> =
        let pars = ParamMap()
        let mutable failure = ValueNone
        let mutable i = 0
        while failure.IsNone && i < pairs.Count do
            let pair = pairs[i]
            i <- i + 1
            match pair.Key.Text with
            | null ->
                // An empty top-level key is skipped before the encoding check.
                match pair.Key.Invalid with
                | null -> ()
                | bytes when bytes.Length = 0 -> ()
                | _ -> failure <- ValueSome(ParamError.Invalid "Invalid encoding for parameter")
            | key when key.Length = 0 -> ()
            | key ->
                let value =
                    match pair.File with
                    | null ->
                        if not pair.HasValue then
                            Ok Param.Null
                        else
                            match pair.Value.Text with
                            | null ->
                                let lossy = Encoding.UTF8.GetString(nonNull pair.Value.Invalid)
                                Error(ParamError.Invalid $"Invalid encoding for parameter: {lossy}")
                            | s -> Ok(Param.Str s)
                    | file -> Ok(Param.File file)
                match value with
                | Error e -> failure <- ValueSome e
                | Ok value ->
                    if findByte key '[' 1 < 0 then
                        pars.Insert(key, value)
                    else
                        match storeNestedParam pars key value 0 with
                        | Error e -> failure <- ValueSome e
                        | Ok _ -> ()
        match failure with
        | ValueSome e -> Error e
        | ValueNone -> Ok pars

    /// `ActionDispatch::ParamBuilder.from_query_string`.
    let fromQueryString (qs: string) : Result<ParamMap, ParamError> =
        match queryPairs qs with
        | Error e -> Error e
        | Ok pairs -> fromPairs pairs

    let tryParseNested (qs: string) : Result<Value, ParamError> =
        fromQueryString qs |> Result.map (fun map -> map.ToJson())

    /// The query-string parser as JSON, for differential tests against
    /// `ActionDispatch::ParamBuilder.from_query_string(qs)` in the reference container. Returns
    /// `Null` when Rails would raise (a 400); see `tryParseNested` for the error itself.
    let parseNested (qs: string) : Value =
        match tryParseNested qs with
        | Ok value -> value
        | Error _ -> Value.Null

    /// `ParamBuilder.from_hash` for a decoded JSON body: nils are compacted out of arrays
    /// (`NoNilParamEncoder`, i.e. deep munge).
    let rec ofJsonValue (value: Value) : Param =
        match value with
        | Value.Null -> Param.Null
        | Value.Bool b -> Param.Bool b
        | Value.Int i -> Param.Number(NInt i)
        | Value.UInt u -> Param.Number(NUInt u)
        | Value.Float f -> Param.Number(NFloat f)
        | Value.String s -> Param.Str s
        | Value.Array items ->
            let kept = ResizeArray<Param>()
            for item in items do
                match item with
                | Value.Null -> ()
                | item -> kept.Add(ofJsonValue item)
            Param.Array kept
        | Value.Object entries ->
            let pars = ParamMap()
            for (k, v) in entries do
                pars.Insert(k, ofJsonValue v)
            Param.Hash pars

    exception private JsonNumberOutOfRange

    let private jsonNumber (element: JsonElement) : Param =
        let text = element.GetRawText()
        if text.IndexOfAny [| '.'; 'e'; 'E' |] < 0 && text <> "-0" then
            match Int64.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
            | true, i -> Param.Number(NInt i)
            | _ ->
                match UInt64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture) with
                | true, u -> Param.Number(NUInt u)
                | _ -> Param.Number(NFloat(Double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)))
        else
            let f = Double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
            // serde_json: "number out of range".
            if Double.IsInfinity f then raise JsonNumberOutOfRange
            Param.Number(NFloat f)

    let rec private jsonParam (element: JsonElement) : Param =
        match element.ValueKind with
        | JsonValueKind.Null -> Param.Null
        | JsonValueKind.True -> Param.Bool true
        | JsonValueKind.False -> Param.Bool false
        | JsonValueKind.Number -> jsonNumber element
        | JsonValueKind.String -> Param.Str(nonNull (element.GetString()))
        | JsonValueKind.Array ->
            let kept = ResizeArray<Param>()
            for item in element.EnumerateArray() do
                if item.ValueKind <> JsonValueKind.Null then kept.Add(jsonParam item)
            Param.Array kept
        | JsonValueKind.Object ->
            // A repeated key keeps its first position and its last value, as serde_json's map does.
            let pars = ParamMap()
            for property in element.EnumerateObject() do
                pars.Insert(property.Name, jsonParam property.Value)
            Param.Hash pars
        | kind -> failwith $"unexpected {kind}"

    /// `ParamBuilder.from_hash` for a JSON body: `Parameters::DEFAULT_PARSERS[:json]` wraps non-hash
    /// documents as `{ "_json" => data }`.
    let fromJsonBody (body: ReadOnlyMemory<byte>) : Result<ParamMap, ParamError> =
        let span = body.Span
        // serde_json refuses a byte order mark, which the .NET reader would skip.
        let hasBom = span.Length >= 3 && span[0] = 0xEFuy && span[1] = 0xBBuy && span[2] = 0xBFuy
        if hasBom then
            Error ParamError.Parse
        else
            try
                let options = JsonDocumentOptions(MaxDepth = 127)
                use document = JsonDocument.Parse(body, options)
                match jsonParam document.RootElement with
                | Param.Hash map -> Ok map
                | other ->
                    let map = ParamMap()
                    map.Insert("_json", other)
                    Ok map
            with
            | :? JsonException
            | :? InvalidOperationException
            | :? FormatException
            | :? OverflowException
            | :? ArgumentException
            | JsonNumberOutOfRange -> Error ParamError.Parse
