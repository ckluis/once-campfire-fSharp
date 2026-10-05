// Port of rust/crates/rails_compat/src/encoding.rs
/// Ruby's Base64 flavors as Rails uses them. (`Rails.Encoding` in Rust; named so it doesn't
/// shadow `System.Text.Encoding`.)
module Campfire.RailsCompat.RailsEncoding

open System

/// `Base64.strict_encode64`.
let strictEncode (data: byte[]) : string = Convert.ToBase64String data

/// `Base64.urlsafe_encode64(data, padding: false)`.
let urlsafeEncodeUnpadded (data: byte[]) : string =
    (Convert.ToBase64String data).TrimEnd('=').Replace('+', '-').Replace('/', '_')

/// `Base64.urlsafe_encode64(data)` (padded).
let urlsafeEncodePadded (data: byte[]) : string =
    (Convert.ToBase64String data).Replace('+', '-').Replace('/', '_')

let private symbol (c: char) : int =
    if c >= 'A' && c <= 'Z' then int c - int 'A'
    elif c >= 'a' && c <= 'z' then int c - int 'a' + 26
    elif c >= '0' && c <= '9' then int c - int '0' + 52
    elif c = '+' then 62
    elif c = '/' then 63
    else -1

/// `Base64.strict_decode64`: standard alphabet, canonical padding, no whitespace. Like Rust's
/// `STANDARD` engine it refuses a missing or partial `=`, and non-zero trailing bits.
let strictDecode (encoded: string) : byte[] option =
    let n = encoded.Length
    if n % 4 <> 0 then
        None
    else
        let padding = if n >= 2 && encoded[n - 2] = '=' then 2 elif n >= 1 && encoded[n - 1] = '=' then 1 else 0
        let dataLength = n - padding
        let mutable valid = true
        let symbols = Array.zeroCreate<int> dataLength
        for i in 0 .. dataLength - 1 do
            let s = symbol encoded[i]
            if s < 0 then valid <- false
            symbols[i] <- s
        if not valid then
            None
        else
            let outLength = dataLength / 4 * 3 + (match dataLength % 4 with 0 -> 0 | 2 -> 1 | 3 -> 2 | _ -> -1)
            // One leftover symbol can't carry a byte; a leftover pair or triple must have its spare bits clear.
            let spareBits =
                match padding with
                | 2 -> symbols[dataLength - 1] &&& 0x0f
                | 1 -> symbols[dataLength - 1] &&& 0x03
                | _ -> 0
            if outLength < 0 || spareBits <> 0 then
                None
            else
                let out = Array.zeroCreate<byte> outLength
                let mutable o = 0
                let mutable i = 0
                while i < dataLength do
                    let remaining = dataLength - i
                    let a = symbols[i]
                    let b = symbols[i + 1]
                    out[o] <- byte ((a <<< 2) ||| (b >>> 4))
                    o <- o + 1
                    if remaining >= 3 then
                        let c = symbols[i + 2]
                        out[o] <- byte (((b &&& 0x0f) <<< 4) ||| (c >>> 2))
                        o <- o + 1
                        if remaining >= 4 then
                            out[o] <- byte (((c &&& 0x03) <<< 6) ||| symbols[i + 3])
                            o <- o + 1
                    i <- i + 4
                Some out

/// `Base64.urlsafe_decode64`, which pads a short unpadded string and then translates `-_` to
/// `+/` before a strict decode. So it accepts either alphabet (even mixed) and optional padding,
/// and it's the standard alphabet after the translation. The strict decode also refuses partial
/// padding (`"ab="`) and non-zero trailing bits, as Ruby does.
let urlsafeDecode (encoded: string) : byte[] option =
    let translated = encoded.Replace('-', '+').Replace('_', '/')
    let padded =
        if not (encoded.EndsWith '=') && encoded.Length % 4 <> 0 then
            translated.PadRight(translated.Length + (4 - translated.Length % 4), '=')
        else
            translated
    strictDecode padded
