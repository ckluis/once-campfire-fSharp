// Port of rust/crates/ruby/src/lib.rs
/// Ruby's own string behaviour, as Rails and Rack apply it, in one place for every crate: ERB
/// escaping, `String#to_i`, `#to_f` and `#strip`, `Float#to_s`, `CGI.escape`,
/// `ERB::Util.url_encode`, how Active Record binds an integer and Rack's byte ranges. Each is
/// checked against Ruby itself on every character from U+0000 to U+00FF and a list of edge cases
/// (`vectors/ruby_core.json`, written by `reference-tools/ruby_core.rb`).
///
/// It depends on nothing but the base library. `Erb` and `Rack` are modules of their own, as in
/// Rust; the rest is re-exported here the way `lib.rs` re-exports it.
module Campfire.Ruby.Ruby

let floatToS = Float.floatToS
let toF = Float.toF
let integerCast = Integer.integerCast
let toI = Integer.toI
let toIChecked = Integer.toIChecked
let strip = RubyString.strip
let cgiEscape = Uri.cgiEscape
let urlEncode = Uri.urlEncode
