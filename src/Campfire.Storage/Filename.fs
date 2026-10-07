// Port of rust/crates/storage/src/filename.rs
namespace Campfire.Storage

open System.Text
open Campfire.Ruby

/// `ActiveStorage::Filename`, with Ruby's `File.extname`/`File.basename` semantics.
type Filename = Filename of string

module Filename =
    /// `File.basename(path)`: the last component, ignoring trailing slashes.
    let basename (path: string) : string =
        let trimmed = path.TrimEnd '/'
        if trimmed.Length = 0 then
            (if path.Length = 0 then "" else "/")
        else
            match trimmed.LastIndexOf '/' with
            | -1 -> trimmed
            | i -> trimmed.Substring(i + 1)

    /// `File.extname(path)` on Unix: leading dots don't start an extension, and a trailing dot is
    /// an extension of its own (`"foo."` -> `"."`).
    let extname (path: string) : string =
        let withoutLeadingDots = (basename path).TrimStart '.'
        match withoutLeadingDots.LastIndexOf '.' with
        | -1 -> ""
        | i -> withoutLeadingDots.Substring i

    let create (filename: string) : Filename = Filename filename

    /// Raw bytes as they arrived; invalid UTF-8 is replaced the way `sanitized`'s `encode` does.
    let fromBytes (bytes: byte[]) : Filename = Filename(Encoding.UTF8.GetString bytes)

    /// The stored (unsanitized) value, as written to `active_storage_blobs.filename`.
    let raw (Filename value) : string = value

    let extensionWithDelimiter (Filename value) : string = extname value

    /// `File.basename(filename, extension_with_delimiter)`.
    let baseName (Filename value) : string =
        let baseName = basename value
        let ext = extname value
        if ext.Length > 0 && baseName.Length > ext.Length && baseName.EndsWith(ext, System.StringComparison.Ordinal) then
            baseName.Substring(0, baseName.Length - ext.Length)
        else
            baseName

    /// `extension_without_delimiter`, aliased as `extension`.
    let extension (filename: Filename) : string =
        let ext = extensionWithDelimiter filename
        if ext.Length > 1 then ext.Substring 1 else ""

    let private replaced = "\u202E%$|:;/<>?*\"\t\r\n\\"

    /// `strip`, then replace RTL override, path separators and shell/HTML metacharacters with "-".
    let sanitized (Filename value) : string =
        (RubyString.strip value).ToCharArray()
        |> Array.map (fun c -> if replaced.IndexOf c >= 0 then '-' else c)
        |> System.String

    /// `Display`.
    let display (filename: Filename) : string = sanitized filename
