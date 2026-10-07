// Port of rust/crates/storage/src/disposition.rs
/// Active Storage's `Content-Disposition` for a blob, and the Journey path escaping used for the
/// `*filename` glob in Active Storage routes.
module Campfire.Storage.Disposition

open System.Text

/// `ActiveStorage::Service#content_disposition_with`: anything but "attachment" is "inline".
let contentDispositionWith (disposition: string) (sanitizedFilename: string) : string =
    let disposition = if disposition = "attachment" then "attachment" else "inline"
    Campfire.RailsCompat.ContentDisposition.format disposition sanitizedFilename

let private isAsciiAlphanumeric (b: byte) : bool =
    (b >= byte 'a' && b <= byte 'z') || (b >= byte 'A' && b <= byte 'Z') || (b >= byte '0' && b <= byte '9')

let private percentEscape (s: string) (keep: byte -> bool) : string =
    let out = StringBuilder(s.Length)
    for b in Encoding.UTF8.GetBytes s do
        if keep b then
            out.Append(char b) |> ignore
        else
            out.Append('%').Append("0123456789ABCDEF"[int b >>> 4]).Append("0123456789ABCDEF"[int b &&& 0xf]) |> ignore
    out.ToString()

/// `Journey::Router::Utils.escape_path`: keeps unreserved, sub-delims, ":", "@" and "/".
let escapePath (s: string) : string =
    percentEscape s (fun b -> isAsciiAlphanumeric b || "-._~!$&'()*+,;=:@/".IndexOf(char b) >= 0)

/// `Journey::Router::Utils.escape_segment`: like `escapePath`, but "/" is escaped too.
let escapeSegment (s: string) : string =
    percentEscape s (fun b -> isAsciiAlphanumeric b || "-._~!$&'()*+,;=:@".IndexOf(char b) >= 0)
