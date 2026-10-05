// Port of rust/crates/ruby/src/string.rs
module Campfire.Ruby.RubyString

/// Ruby's `ISSPACE`: what `String#to_i` and `#to_f` skip, and `\s` in a regexp
/// (space, `\t`, `\n`, `\v`, `\f`, `\r`).
let inline internal isSpace (c: char) = c = ' ' || (c >= '\t' && c <= '\r')

let inline internal isSpaceByte (b: byte) = b = 32uy || (b >= 9uy && b <= 13uy)

/// `String#strip`: NUL and ASCII whitespace off both ends (not Unicode spaces like U+00A0).
let strip (s: string) : string =
    let strippable (c: char) = c = '\000' || isSpace c
    let mutable start = 0
    let mutable stop = s.Length
    while start < stop && strippable s[start] do
        start <- start + 1
    while stop > start && strippable s[stop - 1] do
        stop <- stop - 1
    s.Substring(start, stop - start)
