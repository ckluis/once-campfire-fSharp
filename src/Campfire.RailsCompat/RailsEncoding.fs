// Port of rust/crates/rails_compat/src/encoding.rs
/// Ruby's Base64 flavors as Rails uses them. (`Rails.Encoding` in Rust; named so it doesn't
/// shadow `System.Text.Encoding`.)
module Campfire.RailsCompat.RailsEncoding

open System

/// `Base64.strict_encode64`.
let strictEncode (data: byte[]) : string = Convert.ToBase64String data

/// `Base64.urlsafe_encode64(data, padding: false)`.
let urlsafeEncodeUnpadded (data: byte[]) : string = System.Buffers.Text.Base64Url.EncodeToString data

/// `Base64.urlsafe_encode64(data)` (padded).
let urlsafeEncodePadded (data: byte[]) : string =
    let unpadded = System.Buffers.Text.Base64Url.EncodeToString data
    match unpadded.Length % 4 with
    | 2 -> unpadded + "=="
    | 3 -> unpadded + "="
    | _ -> unpadded

let inline private symbol (c: char) (urlsafe: bool) : int =
    if c >= 'A' && c <= 'Z' then int c - int 'A'
    elif c >= 'a' && c <= 'z' then int c - int 'a' + 26
    elif c >= '0' && c <= '9' then int c - int '0' + 52
    elif c = '+' then 62
    elif c = '/' then 63
    elif urlsafe && c = '-' then 62
    elif urlsafe && c = '_' then 63
    else -1

/// The strict decoder over a span, with `urlsafe` also reading `-_` as `+/` (what `urlsafe_decode64`'s translation
/// does before it hands the string to the strict one). One pass over the symbols, the output the only allocation.
let private decodeSpan (encoded: ReadOnlySpan<char>) (urlsafe: bool) : byte[] voption =
    let n = encoded.Length
    if n % 4 <> 0 then
        ValueNone
    else
        let padding = if n >= 2 && encoded[n - 2] = '=' then 2 elif n >= 1 && encoded[n - 1] = '=' then 1 else 0
        let dataLength = n - padding
        let mutable valid = true
        let mutable i = 0
        while valid && i < dataLength do
            if symbol encoded[i] urlsafe < 0 then valid <- false
            i <- i + 1
        if not valid then
            ValueNone
        else
            let outLength = dataLength / 4 * 3 + (match dataLength % 4 with 0 -> 0 | 2 -> 1 | 3 -> 2 | _ -> -1)
            // One leftover symbol can't carry a byte; a leftover pair or triple must have its spare bits clear.
            let spareBits =
                match padding with
                | 2 -> symbol encoded[dataLength - 1] urlsafe &&& 0x0f
                | 1 -> symbol encoded[dataLength - 1] urlsafe &&& 0x03
                | _ -> 0
            if outLength < 0 || spareBits <> 0 then
                ValueNone
            else
                let out = Array.zeroCreate<byte> outLength
                let mutable o = 0
                let mutable i = 0
                while i < dataLength do
                    let remaining = dataLength - i
                    let a = symbol encoded[i] urlsafe
                    let b = symbol encoded[i + 1] urlsafe
                    out[o] <- byte ((a <<< 2) ||| (b >>> 4))
                    o <- o + 1
                    if remaining >= 3 then
                        let c = symbol encoded[i + 2] urlsafe
                        out[o] <- byte (((b &&& 0x0f) <<< 4) ||| (c >>> 2))
                        o <- o + 1
                        if remaining >= 4 then
                            out[o] <- byte (((c &&& 0x03) <<< 6) ||| symbol encoded[i + 3] urlsafe)
                            o <- o + 1
                    i <- i + 4
                ValueSome out

/// `Base64.strict_decode64`: standard alphabet, canonical padding, no whitespace. Like Rust's
/// `STANDARD` engine it refuses a missing or partial `=`, and non-zero trailing bits.
let strictDecodeSpan (encoded: ReadOnlySpan<char>) : byte[] voption = decodeSpan encoded false

let strictDecode (encoded: string) : byte[] option = decodeSpan (encoded.AsSpan()) false |> ValueOption.toOption

/// `Base64.urlsafe_decode64`, which pads a short unpadded string and then translates `-_` to
/// `+/` before a strict decode. So it accepts either alphabet (even mixed) and optional padding,
/// and it's the standard alphabet after the translation. The strict decode also refuses partial
/// padding (`"ab="`) and non-zero trailing bits, as Ruby does.
let urlsafeDecodeSpan (encoded: ReadOnlySpan<char>) : byte[] voption =
    if (encoded.Length = 0 || encoded[encoded.Length - 1] <> '=') && encoded.Length % 4 <> 0 then
        decodeSpan ((String(encoded)).PadRight(encoded.Length + (4 - encoded.Length % 4), '=').AsSpan()) true
    else
        decodeSpan encoded true

let urlsafeDecode (encoded: string) : byte[] option = urlsafeDecodeSpan (encoded.AsSpan()) |> ValueOption.toOption
