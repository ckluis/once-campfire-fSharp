// Port of rust/crates/rails_compat/src/json.rs
//
// Rust builds on serde_json's `Value` (with `preserve_order`); `Value` here is its counterpart,
// and `Json.parse` reads what serde_json reads.
namespace Campfire.RailsCompat

open System
open System.Collections.Generic
open System.Globalization
open System.Numerics
open System.Text
open System.Text.Json

/// A JSON value. Objects keep their keys in insertion order and compare without regard to it, as
/// serde_json's `Map` does; numbers are an i64, a u64 past it, or an f64, as serde_json's are.
[<CustomEquality; NoComparison; RequireQualifiedAccess>]
type Value =
    | Null
    | Bool of bool
    | Int of int64
    | UInt of uint64
    | Float of float
    | String of string
    | Array of Value list
    | Object of (string * Value) list

    override this.Equals(other: obj) =
        match other with
        | :? Value as other ->
            match this, other with
            | Null, Null -> true
            | Bool a, Bool b -> a = b
            | Int a, Int b -> a = b
            | UInt a, UInt b -> a = b
            | Float a, Float b -> a = b
            | String a, String b -> String.Equals(a, b, StringComparison.Ordinal)
            | Array a, Array b -> a.Length = b.Length && List.forall2 (fun (x: Value) y -> x.Equals y) a b
            | Object a, Object b ->
                a.Length = b.Length
                && a
                   |> List.forall (fun (key, value) ->
                       match List.tryFind (fun (k, _) -> k = key) b with
                       | Some(_, other) -> value.Equals other
                       | None -> false)
            | _ -> false
        | _ -> false

    override this.GetHashCode() =
        match this with
        | Null -> 0
        | Bool b -> hash b
        | Int i -> hash i
        | UInt u -> hash u
        | Float f -> hash f
        | String s -> hash s
        | Array items -> items |> List.fold (fun acc v -> acc * 31 + v.GetHashCode()) 17
        | Object entries -> entries |> List.sumBy (fun (k, v) -> hash k ^^^ v.GetHashCode())

    /// `value.get(key)`: the entry of an object, None for any other value.
    member this.TryGet(key: string) : Value option =
        match this with
        | Object entries -> entries |> List.tryFind (fun (k, _) -> k = key) |> Option.map snd
        | _ -> None

    /// `as_str`
    member this.AsString : string option = (match this with String s -> Some s | _ -> None)

    /// `as_i64`: a float, or a u64 past i64::MAX, is None.
    member this.AsInt64 : int64 option = (match this with Int i -> Some i | _ -> None)

    /// `as_object`
    member this.AsObject : (string * Value) list option = (match this with Object entries -> Some entries | _ -> None)

exception internal JsonNumberOutOfRange

module Json =
    let private hexDigits = "0123456789abcdef"

    /// serde_json's string escaping: quote, backslash and control characters (lowercase hex);
    /// everything else, `/` and non-ASCII included, as it is.
    let private writeString (out: StringBuilder) (s: string) =
        out.Append '"' |> ignore
        for c in s do
            match c with
            | '"' -> out.Append "\\\"" |> ignore
            | '\\' -> out.Append "\\\\" |> ignore
            | '\b' -> out.Append "\\b" |> ignore
            | '\012' -> out.Append "\\f" |> ignore
            | '\n' -> out.Append "\\n" |> ignore
            | '\r' -> out.Append "\\r" |> ignore
            | '\t' -> out.Append "\\t" |> ignore
            | c when c < ' ' -> out.Append("\\u00").Append(hexDigits[int c >>> 4]).Append(hexDigits[int c &&& 0xf]) |> ignore
            | c -> out.Append c |> ignore
        out.Append '"' |> ignore

    /// Whether `magnitude` is exactly halfway between the decimal `digits` (whose last digit is
    /// `10^k`) and the same digits one higher: 2v = (2D + 1) * 10^k, compared as integers.
    let private isTieAbove (magnitude: float) (digits: string) (k: int) : bool =
        let bits = BitConverter.DoubleToInt64Bits magnitude
        let biased = int ((bits >>> 52) &&& 0x7FFL)
        let fraction = bits &&& 0xFFFFFFFFFFFFFL
        let mantissa, exponent =
            if biased = 0 then fraction, -1074 else (fraction ||| (1L <<< 52)), biased - 1075
        let two = BigInteger 2
        let ten = BigInteger 10
        let left = BigInteger 2 * BigInteger mantissa * BigInteger.Pow(two, max exponent 0) * BigInteger.Pow(ten, max -k 0)
        let right = (BigInteger 2 * BigInteger.Parse digits + BigInteger.One) * BigInteger.Pow(ten, max k 0) * BigInteger.Pow(two, max -exponent 0)
        left = right

    /// The shortest digits that read back as `magnitude`, and where the decimal point goes in
    /// them (`1.2345e6` is "12345" and 7). .NET's "R" can write a form that reads back as the
    /// neighbour below at a power of two, where the interval is lopsided, so a form that doesn't
    /// read back is replaced by the first correctly rounded length that does.
    ///
    /// Where the value is an exact tie between two candidates of that length, .NET takes the even
    /// last digit and Rust's `{:e}` the upper one (checked for `{:e}`, not for the json gem; see
    /// rust/crates/ruby/src/float.rs: "Ruby's dtoa takes the even one and Rust the upper one").
    /// This port takes the upper candidate on a tie so that it matches Rust's output.
    let private shortestDigits (magnitude: float) : string * int =
        let roundTrips (text: string) =
            Double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) = magnitude
        let split (text: string) =
            let e = text.IndexOf 'E'
            let mantissa, exponent =
                if e < 0 then text, 0
                else text.Substring(0, e), Int32.Parse(text.Substring(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
            let dot = mantissa.IndexOf '.'
            let integerLength = if dot < 0 then mantissa.Length else dot
            let raw = mantissa.Replace(".", "")
            let trimmed = raw.TrimStart '0'
            (trimmed.TrimEnd '0', integerLength - (raw.Length - trimmed.Length) + exponent)
        let r = magnitude.ToString("R", CultureInfo.InvariantCulture)
        let (digits, point) =
            if roundTrips r then
                split r
            else
                let significand = ((r.Split 'E').[0]).Replace(".", "").TrimStart '0'
                seq {
                    for n in significand.Length + 1 .. 17 do
                        yield magnitude.ToString("E" + string (n - 1), CultureInfo.InvariantCulture)
                }
                |> Seq.find roundTrips
                |> split
        // A carry (a last digit of 9) would have been found as a shorter form, so it is skipped.
        if digits.Length >= 16 && digits[digits.Length - 1] <> '9' then
            let k = point - digits.Length
            if isTieAbove magnitude digits k then
                let upper = digits.Substring(0, digits.Length - 1) + string (char (int digits[digits.Length - 1] + 1))
                if roundTrips (upper + "E" + string k) then (upper, point) else (digits, point)
            else
                (digits, point)
        else
            (digits, point)

    /// A finite float as the json gem writes it, which is not `Float#to_s`: json 2.21.2's
    /// `fpconv_dtoa` (`ext/json/ext/vendor/fpconv.c`, `emit_digits`) writes `1e15` as `1e+15` and
    /// `1e-5` as `0.00001`.
    ///
    /// The digits are the shortest that round-trip. fpconv's Grisu2 now and then writes a longer
    /// equivalent (`250.70174600000001` for `250.701746`), which parses to the same float.
    let private floatText (f: float) : string =
        let sign = if Double.IsNegative f then "-" else ""
        if f = 0.0 then
            sign + "0.0"
        else
            let (digits, point) = shortestDigits (abs f)
            // fpconv's `K` is the power of ten of the last digit.
            let exponent = point - 1
            let k = exponent + 1 - digits.Length
            let body =
                if k >= 0 && exponent < 15 then
                    digits + String('0', k) + ".0"
                elif k < 0 && (k > -7 || abs exponent < 10) then
                    if point <= 0 then "0." + String('0', -point) + digits
                    else digits.Substring(0, point) + "." + digits.Substring point
                else
                    let fraction = if digits.Length > 1 then "." + digits.Substring 1 else ""
                    digits.Substring(0, 1) + fraction + "e" + (if exponent < 0 then "-" else "+") + string (abs exponent)
            sign + body

    let rec private write (out: StringBuilder) (value: Value) : unit =
        match value with
        | Value.Null -> out.Append "null" |> ignore
        | Value.Bool b -> out.Append(if b then "true" else "false") |> ignore
        | Value.Int i -> out.Append(i.ToString(CultureInfo.InvariantCulture)) |> ignore
        | Value.UInt u -> out.Append(u.ToString(CultureInfo.InvariantCulture)) |> ignore
        // serde_json writes a non-finite float as `null`, which is what `ActiveSupport::JSON` does
        // (`Float#as_json`); `JSON.generate` would raise.
        | Value.Float f when Double.IsNaN f || Double.IsInfinity f -> out.Append "null" |> ignore
        | Value.Float f -> out.Append(floatText f) |> ignore
        | Value.String s -> writeString out s
        | Value.Array items ->
            out.Append '[' |> ignore
            items
            |> List.iteri (fun i item ->
                if i > 0 then out.Append ',' |> ignore
                write out item)
            out.Append ']' |> ignore
        | Value.Object entries ->
            out.Append '{' |> ignore
            entries
            |> List.iteri (fun i (key, item) ->
                if i > 0 then out.Append ',' |> ignore
                writeString out key
                out.Append ':' |> ignore
                write out item)
            out.Append '}' |> ignore

    /// `::JSON.generate` / `JSON.dump`: plain JSON, non-ASCII left as UTF-8.
    let generate (value: Value) : string =
        let out = StringBuilder 128
        write out value
        out.ToString()

    /// Re-escapes already-encoded JSON. `<`, `>` and `&` can only appear inside JSON strings, so
    /// replacing them anywhere in the document is safe, and doing it twice is harmless.
    let escapeHtmlEntities (json: string) : string =
        if json.IndexOfAny [| '<'; '>'; '&' |] < 0 then
            json
        else
            let escaped = StringBuilder(json.Length + 16)
            for c in json do
                match c with
                | '<' -> escaped.Append "\\u003c" |> ignore
                | '>' -> escaped.Append "\\u003e" |> ignore
                | '&' -> escaped.Append "\\u0026" |> ignore
                | c -> escaped.Append c |> ignore
            escaped.ToString()

    /// `ActiveSupport::JSON.encode` with `escape_html_entities_in_json` (the default): like
    /// `JSON.generate`, plus `<`, `>` and `&` escaped as `<`, `>` and `&`. U+2028/U+2029
    /// are *not* escaped: `load_defaults` 8.1+ turns `escape_js_separators_in_json` off.
    let encode (value: Value) : string = escapeHtmlEntities (generate value)

    let private number (element: JsonElement) : Value =
        let text = element.GetRawText()
        if text.IndexOfAny [| '.'; 'e'; 'E' |] < 0 && text <> "-0" then
            match Int64.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
            | true, i -> Value.Int i
            | _ ->
                match UInt64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture) with
                | true, u -> Value.UInt u
                | _ -> Value.Float(Double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture))
        else
            let f = Double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
            // serde_json: "number out of range".
            if Double.IsInfinity f then raise JsonNumberOutOfRange
            Value.Float f

    let private scanLimit = 8

    let rec private convert (element: JsonElement) : Value =
        match element.ValueKind with
        | JsonValueKind.Null -> Value.Null
        | JsonValueKind.True -> Value.Bool true
        | JsonValueKind.False -> Value.Bool false
        | JsonValueKind.Number -> number element
        | JsonValueKind.String -> Value.String(nonNull (element.GetString()))
        | JsonValueKind.Array -> Value.Array [ for item in element.EnumerateArray() -> convert item ]
        | JsonValueKind.Object ->
            // A repeated key keeps its first position and its last value, as serde_json's map does.
            // Small objects scan; past `scanLimit` entries a dictionary finds the key, so an object
            // of n keys costs O(n) rather than O(n^2) (a Cable message can hold 100k of them).
            let entries = ResizeArray<string * Value>()
            let mutable positions: Dictionary<string, int> | null = null
            for property in element.EnumerateObject() do
                let value = convert property.Value
                let name = property.Name
                match positions with
                | null ->
                    let mutable found = -1
                    let mutable i = 0
                    while found < 0 && i < entries.Count do
                        if String.Equals(fst entries[i], name, StringComparison.Ordinal) then found <- i
                        i <- i + 1
                    if found >= 0 then entries[found] <- (name, value)
                    else
                        entries.Add((name, value))
                        if entries.Count > scanLimit then
                            let index = Dictionary<string, int>(StringComparer.Ordinal)
                            for j in 0 .. entries.Count - 1 do
                                index[fst entries[j]] <- j
                            positions <- index
                | positions ->
                    match positions.TryGetValue name with
                    | true, i -> entries[i] <- (name, value)
                    | _ ->
                        positions[name] <- entries.Count
                        entries.Add((name, value))
            Value.Object(List.ofSeq entries)
        | kind -> failwith $"unexpected {kind}"

    /// `serde_json::from_slice`: None unless the whole input is one valid JSON value.
    let parse (bytes: byte[]) : Value option =
        // serde_json refuses a byte order mark, which the .NET reader would skip.
        let hasBom = bytes.Length >= 3 && bytes[0] = 0xEFuy && bytes[1] = 0xBBuy && bytes[2] = 0xBFuy
        if hasBom then
            None
        else
            try
                let options = JsonDocumentOptions(MaxDepth = 127)
                use document = JsonDocument.Parse(ReadOnlyMemory bytes, options)
                Some(convert document.RootElement)
            with
            | :? JsonException
            | :? InvalidOperationException
            | :? FormatException
            | :? OverflowException
            | :? ArgumentException
            | JsonNumberOutOfRange -> None
