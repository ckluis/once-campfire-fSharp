// Port of rust/crates/ruby/src/uri.rs
module Campfire.Ruby.Uri

open System.Text

let private hex = "0123456789ABCDEF"

let private percentEncode (s: string) (spaceAsPlus: bool) : string =
    let bytes = Encoding.UTF8.GetBytes s
    let out = StringBuilder(bytes.Length)
    for b in bytes do
        let c = char b
        if (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '_' || c = '.' || c = '-' || c = '~' then
            out.Append c |> ignore
        elif b = 32uy && spaceAsPlus then
            out.Append '+' |> ignore
        else
            out.Append('%').Append(hex[int (b >>> 4)]).Append(hex[int (b &&& 0xfuy)]) |> ignore
    out.ToString()

/// `CGI.escape`: everything but `A-Za-z0-9_.-~` is percent-encoded, and a space becomes `+`.
let cgiEscape (s: string) : string = percentEncode s true

/// `ERB::Util.url_encode`: everything but `A-Za-z0-9_.-~` is percent-encoded, a space as `%20`.
/// Addressable's `encode_component(s, UNRESERVED)` keeps the same set.
let urlEncode (s: string) : string = percentEncode s false
