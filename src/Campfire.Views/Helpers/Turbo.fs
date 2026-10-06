// Port of rust/crates/views/src/helpers/turbo.rs
/// turbo-rails helpers: `turbo_frame_tag`, `turbo_stream_from`, `turbo_page_requires_reload`.
module Campfire.Views.Helpers.Turbo

open Campfire.Views
open Campfire.Views.Helpers.Tag

/// `turbo_frame_tag(id, src:, target:, **attributes) { content }`'s attributes: the given ones
/// first, then `id`, `src` and `target` (nil ones dropped).
let turboFrameOptions (id: string) (src: string option) (target: string option) (attributes: Attrs) : Attrs =
    attributes.Attr("id", id).AttrOpt("src", src).AttrOpt("target", target)

/// `turboFrameOptions` for an id that is an attribute value (see `domIdValue`).
let turboFrameOptionsValue (id: AttrValue) (src: string option) (target: string option) (attributes: Attrs) : Attrs =
    attributes.Attr("id", id).AttrOpt("src", src).AttrOpt("target", target)

/// `turbo_stream_from(*streamables)`. The signed stream name comes from the caller
/// (`Turbo::StreamsChannel.signed_stream_name`).
let turboStreamFrom (w: Out) (signedStreamName: string) : unit =
    builderTag w "turbo-cable-stream-source" (attrs().Attr("channel", "Turbo::StreamsChannel").Attr("signed-stream-name", signedStreamName))

/// `turbo_page_requires_reload_tag`, which `turbo_page_requires_reload` provides to `:head`.
let turboPageRequiresReloadTag (w: Out) : unit =
    builderTag w "meta" (attrs().Name("turbo-visit-control").Attr("content", "reload"))

/// `"prefix_model_"` (or `"model_"`), made once per distinct pair: the pairs are a template's literal
/// prefix and a model's param key, so the tables are as small as the templates are.
let private prefixed =
    System.Collections.Concurrent.ConcurrentDictionary<struct (string * string), string>()

let private unprefixed = System.Collections.Concurrent.ConcurrentDictionary<string, string>()

let private domPrefix (model: string) (prefix: string option) : string =
    // No prefix is not the empty prefix: `dom_id(x, "")` is "_x_1", `dom_id(x)` is "x_1".
    match prefix with
    | Some prefix ->
        let key = struct (prefix, model)
        match prefixed.TryGetValue key with
        | true, found -> found
        | _ ->
            let made = $"{prefix}_{model}_"
            prefixed[key] <- made
            made
    | None ->
        match unprefixed.TryGetValue model with
        | true, found -> found
        | _ ->
            let made = $"{model}_"
            unprefixed[model] <- made
            made

/// Whether a dom_id prefix can go out unescaped: ASCII letters, digits and `_`, as every model param
/// key and literal prefix is. Anything else (a view-model field that someday carries user text) is
/// escaped instead, so the unescaped fast path never depends on the caller.
let private isPlain (prefix: string) =
    let mutable plain = true
    for c in prefix do
        if not (System.Char.IsAsciiLetterOrDigit c || c = '_') then plain <- false
    plain

/// `dom_id(record, prefix)` as an attribute value: "prefix_model_id", written by the tag with the id
/// formatted straight into the buffer, so no string is made for it.
let domIdValue (model: string) (id: int64) (prefix: string option) : AttrValue =
    let text = domPrefix model prefix
    if isPlain text then Numbered(text, id)
    else Text(text + id.ToString(System.Globalization.CultureInfo.InvariantCulture))

/// `dom_id(record, prefix)`: "prefix_model_id".
let domId (model: string) (id: 'a) (prefix: string option) : string = domPrefix model prefix + string id

/// `dom_id(record, prefix)` written into a template, with no string made for it.
let writeDomId (w: Out) (model: string) (id: int64) (prefix: string option) : unit =
    let text = domPrefix model prefix
    if isPlain text then w.Raw text else w.Text text
    w.Int id
