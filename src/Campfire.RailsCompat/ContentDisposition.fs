// Port of rust/crates/rails_compat/src/content_disposition.rs
/// `ActionDispatch::Http::ContentDisposition.format` (actionpack's
/// `action_dispatch/http/content_disposition.rb`), as `send_file`, `send_data` and Active Storage
/// name a download: an ASCII `filename=` for old clients, transliterated with I18n's default
/// approximations, and the full name in RFC 5987's `filename*=`.
module Campfire.RailsCompat.ContentDisposition

open System.Collections.Generic
open System.Text
open Campfire.RailsCompat.ContentDispositionApproximations

let private approximationsByCodePoint =
    lazy (
        let table = Dictionary<int, string>(approximations.Length)
        for (codePoint, ascii) in approximations do
            table[codePoint] <- ascii
        table
    )

let private isAsciiAlphanumeric (b: byte) =
    (b >= byte 'a' && b <= byte 'z') || (b >= byte 'A' && b <= byte 'Z') || (b >= byte '0' && b <= byte '9')

/// What `TRADITIONAL_ESCAPED_CHAR` leaves alone: ``[ A-Za-z0-9!#$+.^_`|~-]``.
let private traditional (b: byte) : bool = b = byte ' ' || isAsciiAlphanumeric b || "!#$+.^_`|~-".IndexOf(char b) >= 0

/// What `RFC_5987_ESCAPED_CHAR` leaves alone: ``[A-Za-z0-9!#$&+.^_`|~-]``.
let private rfc5987 (b: byte) : bool = isAsciiAlphanumeric b || "!#$&+.^_`|~-".IndexOf(char b) >= 0

/// `percent_escape(string, pattern)`: each escaped character's bytes as `%XX`. The kept
/// characters are all ASCII, so going byte by byte is the same.
let private percentEscape (s: string) (keep: byte -> bool) : string =
    let bytes = Encoding.UTF8.GetBytes s
    let out = StringBuilder(bytes.Length)
    for b in bytes do
        if keep b then out.Append(char b) |> ignore else out.Append('%').Append(b.ToString "X2") |> ignore
    out.ToString()

/// `I18n.transliterate` with the default approximations, and "?" for anything else non-ASCII.
let private transliterate (s: string) : string =
    let table = approximationsByCodePoint.Value
    let out = StringBuilder(s.Length)
    for rune in s.EnumerateRunes() do
        if rune.IsAscii then
            out.Append(char rune.Value) |> ignore
        else
            match table.TryGetValue rune.Value with
            | true, ascii -> out.Append ascii |> ignore
            | _ -> out.Append '?' |> ignore
    out.ToString()

/// `ContentDisposition.format(disposition:, filename:)` for a filename (without one it's just the
/// disposition).
let format (disposition: string) (filename: string) : string =
    $"{disposition}; filename=\"{percentEscape (transliterate filename) traditional}\"; filename*=UTF-8''{percentEscape filename rfc5987}"
