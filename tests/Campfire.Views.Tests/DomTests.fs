// The DOM normalizer behind the Rails-golden comparisons (Support/Dom.fs), against what Rust's
// tests/support/dom.rs prints: a comment ends the text before it, and a doctype keeps its name.
module Campfire.Views.Tests.DomTests

open Xunit
open Campfire.Views.Tests.Dom

[<Fact>]
let ``a comment splits the text around it`` () =
    // Merged, this would be one token "ab"; Rust's `CommentToken(_) => self.flush_text()` makes two.
    Assert.Equal<string list>([ "<p>"; "\"a\""; "\"b\""; "</p>" ], normalizeHtml "<p>a<!-- c -->b</p>")
    // Without one, adjacent text still merges and whitespace collapses.
    Assert.Equal<string list>([ "<p>"; "\"a b\""; "</p>" ], normalizeHtml "<p>a  \n b</p>")

[<Fact>]
let ``a doctype keeps its name as rust prints it`` () =
    Assert.Equal<string list>([ "<!DOCTYPE Some(\"html\")>"; "<p>"; "</p>" ], normalizeHtml "<!DOCTYPE html><p></p>")
    // html5ever lowercases the name.
    Assert.Equal<string list>([ "<!DOCTYPE Some(\"html\")>" ], normalizeHtml "<!doctype HTML>")
    Assert.Equal<string list>([ "<!DOCTYPE Some(\"svg\")>" ], normalizeHtml "<!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"x\">")
    // A wrong name no longer passes as the right one.
    Assert.NotEqual<string list>(normalizeHtml "<!DOCTYPE html>", normalizeHtml "<!DOCTYPE xhtml>")
    // No name, and nothing before the end of the input.
    Assert.Equal<string list>([ "<!DOCTYPE None>" ], normalizeHtml "<!DOCTYPE>")
    Assert.Equal<string list>([ "<!DOCTYPE None>" ], normalizeHtml "<!DOCTYPE ")
    Assert.Equal<string list>([ "<!DOCTYPE Some(\"ht\")>" ], normalizeHtml "<!DOCTYPE ht")
