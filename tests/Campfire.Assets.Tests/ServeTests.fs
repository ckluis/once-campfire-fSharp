// Port of rust/crates/assets/src/serve.rs `accepts`, which Rust checks only through the response
// headers; the case below is the one where the byte and char index spaces differ.
module Campfire.Assets.Tests.ServeTests

open Xunit
open Campfire.Assets

[<Fact>]
let ``accept-encoding matches whole words, as /\bbr\b/i`` () =
    let accepts = Serve.accepts
    Assert.True(accepts "gzip, br" "br")
    Assert.True(accepts "BR;q=0.5" "br")
    Assert.True(accepts "gzip, deflate, br" "gzip")
    Assert.False(accepts "brotli" "br")
    Assert.False(accepts "xbr" "br")
    Assert.False(accepts "" "br")
    // Non-ASCII before the match: the boundary is the byte before "br" ('x', a word byte), not
    // the byte at the same index counted in chars.
    Assert.False(accepts "éxbr" "br")
    Assert.False(accepts "éxbr, gzip" "br")
    // A hyphen is a boundary whatever precedes it.
    Assert.True(accepts "é-br" "br")
