// Port of rust/crates/rails_compat/src/marshal.rs
/// The sliver of Ruby's Marshal format needed to read Rails 7-era signed messages, whose payload
/// is a marshaled String (`Marshal.dump("gid://campfire/User/1")`). Anything else is rejected.
module internal Campfire.RailsCompat.Marshal

open System

let signature: byte[] = [| 4uy; 8uy |]

/// The Marshal fixnum at `index`, and the index after it.
let private readFixnum (bytes: byte[]) (index: int) : (int64 * int) option =
    if index >= bytes.Length then
        None
    else
        let first = int (sbyte bytes[index])
        let rest = index + 1
        match first with
        | 0 -> Some(0L, rest)
        | n when n >= 1 && n <= 4 ->
            if rest + n > bytes.Length then
                None
            else
                let mutable value = 0L
                for i in n - 1 .. -1 .. 0 do
                    value <- (value <<< 8) ||| int64 bytes[rest + i]
                Some(value, rest + n)
        | n when n >= -4 && n <= -1 ->
            let n = -n
            if rest + n > bytes.Length then
                None
            else
                let mutable value = -1L
                for i in 0 .. n - 1 do
                    value <- value &&& ~~~(0xffL <<< (8 * i))
                    value <- value ||| (int64 bytes[rest + i] <<< (8 * i))
                Some(value, rest + n)
        | n when n >= 5 -> Some(int64 n - 5L, rest)
        | n -> Some(int64 n + 5L, rest)

let private startsWith (bytes: byte[]) (prefix: byte[]) =
    bytes.Length >= prefix.Length && bytes.AsSpan(0, prefix.Length).SequenceEqual(ReadOnlySpan prefix)

/// Loads a marshaled String: `"\x04\x08" ["I"] '"' <len> <bytes> [<ivars>]`.
let loadString (dumped: byte[]) : byte[] option =
    if not (startsWith dumped signature) then
        None
    else
        let mutable index = signature.Length
        if index < dumped.Length && dumped[index] = byte 'I' then
            index <- index + 1
        if index >= dumped.Length || dumped[index] <> byte '"' then
            None
        else
            match readFixnum dumped (index + 1) with
            | Some(len, start) when len >= 0L && int64 start + len <= int64 dumped.Length -> Some(dumped[start .. start + int len - 1])
            | _ -> None
