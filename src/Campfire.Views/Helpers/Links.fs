// Port of rust/crates/views/src/helpers/links.rs
/// `link_to`, `link_to_if` and `mail_to` (`UrlHelper`).
module Campfire.Views.Helpers.Links

open Campfire.Ruby
open Campfire.Views
open Campfire.Views.Helpers.Tag

/// `link_to`'s attributes: `href` goes after the given options.
let linkOptions (url: string) (options: Attrs) : Attrs = options.Attr("href", url)

/// `link_to(url, options) do ... end` (`link_to(url, options, content)` with the content written).
let inline linkTo (w: Out) (url: string) (options: Attrs) ([<InlineIfLambda>] content: Out -> unit) : unit =
    contentTagBlock w "a" (linkOptions url options) content

/// `link_to(text, url, options)` with a plain-text name.
let linkToText (w: Out) (text: string) (url: string) (options: Attrs) : unit =
    contentTagBlock w "a" (linkOptions url options) (fun w -> w.Text text)

/// `link_to_if(condition, name, url, options)`: just the escaped name when false.
let linkToIf (w: Out) (condition: bool) (text: string) (url: string) (options: Attrs) : unit =
    if condition then linkToText w text url options else w.Text text

/// `mail_to(email)`: the address percent-escaped (`ERB::Util.url_encode`, keeping "@") in the
/// href, and the plain address as the link text.
let mailTo (w: Out) (email: string) : unit =
    let encoded = Ruby.urlEncode(email).Replace("%40", "@")
    contentTagBlock w "a" (attrs().Attr("href", "mailto:" + encoded)) (fun w -> w.Text email)
