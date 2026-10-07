// What Rust's `char` and the regex crate's `\w` say about characters, which .NET's `Char` doesn't
// say the same way (see script/generate-tables.py).
module internal Campfire.RichText.Unicode.Chars

open System

/// `char::is_whitespace`: Unicode's White_Space property.
let isWhitespace (c: char) : bool =
    match c with
    | '\u0009' | '\u000a' | '\u000b' | '\u000c' | '\u000d' | ' ' | '\u0085' | ' ' | ' ' | ' ' | ' ' | ' '
    | ' ' | '　' -> true
    | c -> c >= ' ' && c <= ' '

/// The regex crate's `\w` for a code point.
let isWordCodePoint (cp: int) : bool =
    let ranges = WordRanges.ranges
    if cp < 0x80 then
        (cp >= int '0' && cp <= int '9') || (cp >= int 'A' && cp <= int 'Z') || cp = int '_' || (cp >= int 'a' && cp <= int 'z')
    else
        // Binary search over the lo/hi pairs.
        let mutable lo = 0
        let mutable hi = ranges.Length / 2 - 1
        let mutable found = false
        while not found && lo <= hi do
            let mid = (lo + hi) / 2
            if cp < ranges[mid * 2] then hi <- mid - 1
            elif cp > ranges[mid * 2 + 1] then lo <- mid + 1
            else found <- true
        found

/// The code point that ends `s`, and where it starts (`s.chars().last()`); -1 for an empty string.
let lastCodePoint (s: string) : struct (int * int) =
    if s.Length = 0 then
        struct (-1, 0)
    elif s.Length >= 2 && Char.IsLowSurrogate s[s.Length - 1] && Char.IsHighSurrogate s[s.Length - 2] then
        struct (Char.ConvertToUtf32(s[s.Length - 2], s[s.Length - 1]), s.Length - 2)
    else
        struct (int s[s.Length - 1], s.Length - 1)

/// `str::to_lowercase`, which maps every character through Unicode's lowercase mapping. .NET's
/// invariant lowercasing agrees except for U+0130, which Rust turns into "i" and a combining dot,
/// and a capital sigma at the end of a word, which Rust makes a final sigma. Callers only compare
/// the result with ASCII, or with another string lowercased the same way (and hosts that reach it
/// are ASCII: `RubyUri.parse` refuses anything else).
let toLowercase (s: string) : string =
    let lowered = s.ToLowerInvariant()
    if lowered.IndexOf 'İ' >= 0 then lowered.Replace("İ", "i̇") else lowered
