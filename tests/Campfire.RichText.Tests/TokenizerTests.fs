// Port of the tests in rust/crates/richtext/vendor/html5ever/src/util/str.rs (`lower_ascii_letter`).
//
// Not ported: the tokenizer's `push_to_None_gives_singleton`, `push_to_empty_appends` and
// `push_to_nonempty_appends` (`option_push` builds the doctype's name and ids, which the F# tokenizer
// doesn't keep because the DOM drops doctypes) and `check_lines` and `check_lines_with_new_line` (line
// numbers, which Html5ever/Types.fs says are not kept).
module Campfire.RichText.Tests.TokenizerTests

open Xunit
open Campfire.RichText.Html5ever

// `lower_ascii_letter` returns an Option<char>; the F# helper returns the lowercase letter or -1.

[<Fact>]
let ``lower_letter_a_is_a`` () =
    Assert.Equal(int 'a', TokenizerChars.lowerAsciiLetter 'a')

[<Fact>]
let ``lower_letter_A_is_a`` () =
    Assert.Equal(int 'a', TokenizerChars.lowerAsciiLetter 'A')

[<Fact>]
let ``lower_letter_symbol_is_None`` () =
    Assert.Equal(-1, TokenizerChars.lowerAsciiLetter '!')

[<Fact>]
let ``lower_letter_nonascii_is_None`` () =
    Assert.Equal(-1, TokenizerChars.lowerAsciiLetter 'ꙮ')
