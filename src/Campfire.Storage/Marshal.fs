// Port of rust/crates/storage/src/marshal.rs
/// The subset of Ruby's `Marshal.dump` (format 4.8) needed to digest variation transformations:
/// `ActiveStorage::Variation#digest` is `SHA1.base64digest(Marshal.dump(transformations))`.
///
/// Symbols and strings are distinct: transformations built in Ruby carry symbols
/// (`format: :webp`), while a variation decoded from a signed URL key carries strings
/// (`format: "webp"`), and the two digest differently.
module Campfire.Storage.Marshal

open System
open System.Text

/// A Ruby value as it appears in a transformations hash (after `deep_symbolize_keys`).
[<RequireQualifiedAccess>]
type Value =
    | Nil
    | Bool of bool
    | Int of int64
    | Symbol of string
    /// A UTF-8 string (the encoding of every string Active Storage puts in a transformation).
    | Str of string
    | Array of Value list
    /// A hash with symbol keys, in insertion order.
    | Hash of (string * Value) list

type private Writer() =
    let out = ResizeArray<byte>([ 4uy; 8uy ])
    let symbols = ResizeArray<string>()

    member _.Out : ResizeArray<byte> = out

    /// `w_long` in marshal.c.
    member _.Long(n: int64) : unit =
        if n = 0L then
            out.Add 0uy
        elif 0L < n && n < 123L then
            out.Add(byte (n + 5L))
        elif -124L < n && n < 0L then
            out.Add(byte ((n - 5L) &&& 0xffL))
        else
            let buf = Array.zeroCreate<byte> 9
            let mutable x = n
            let mutable i = 1
            let mutable finished = false
            while not finished && i <= 8 do
                buf[i] <- byte (x &&& 0xffL)
                x <- x >>> 8
                if x = 0L then
                    buf[0] <- byte i
                    out.AddRange(ArraySegment<byte>(buf, 0, i + 1))
                    finished <- true
                elif x = -1L then
                    buf[0] <- byte (-(sbyte i))
                    out.AddRange(ArraySegment<byte>(buf, 0, i + 1))
                    finished <- true
                i <- i + 1

    member this.Bytes(bytes: byte[]) : unit =
        this.Long(int64 bytes.Length)
        out.AddRange bytes

    member this.Symbol(name: string) : unit =
        match symbols.IndexOf name with
        | -1 ->
            symbols.Add name
            out.Add(byte ':')
            this.Bytes(Encoding.UTF8.GetBytes name)
        | index ->
            out.Add(byte ';')
            this.Long(int64 index)

    /// Integers whose tagged VALUE fits in 32 bits (31-bit signed) are written inline (`i`),
    /// larger ones as bignums (`l`).
    member this.Integer(n: int64) : unit =
        if -(1L <<< 30) <= n && n < (1L <<< 30) then
            out.Add(byte 'i')
            this.Long n
        else
            out.Add(byte 'l')
            out.Add(if n < 0L then byte '-' else byte '+')
            // `unsigned_abs`
            let mutable magnitude = if n < 0L then uint64 (-(n + 1L)) + 1UL else uint64 n
            let digits = ResizeArray<byte>()
            while magnitude > 0UL do
                digits.Add(byte (magnitude &&& 0xffUL))
                magnitude <- magnitude >>> 8
            if digits.Count % 2 = 1 then digits.Add 0uy
            this.Long(int64 (digits.Count / 2))
            out.AddRange digits

    member this.Value(value: Value) : unit =
        match value with
        | Value.Nil -> out.Add(byte '0')
        | Value.Bool true -> out.Add(byte 'T')
        | Value.Bool false -> out.Add(byte 'F')
        | Value.Int n -> this.Integer n
        | Value.Symbol name -> this.Symbol name
        | Value.Str s ->
            // A string with an encoding is wrapped in an ivar list holding `E: true` (UTF-8).
            out.Add(byte 'I')
            out.Add(byte '"')
            this.Bytes(Encoding.UTF8.GetBytes s)
            this.Long 1L
            this.Symbol "E"
            out.Add(byte 'T')
        | Value.Array items ->
            out.Add(byte '[')
            this.Long(int64 (List.length items))
            for item in items do
                this.Value item
        | Value.Hash entries ->
            out.Add(byte '{')
            this.Long(int64 (List.length entries))
            for key, item in entries do
                this.Symbol key
                this.Value item

let dump (value: Value) : byte[] =
    let writer = Writer()
    writer.Value value
    writer.Out.ToArray()
