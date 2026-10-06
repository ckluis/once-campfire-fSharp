// Port of rust/crates/views/src/helpers/turbo.rs
/// turbo-rails helpers: `turbo_frame_tag`, `turbo_stream_from`, `turbo_page_requires_reload`.
module Campfire.Views.Helpers.Turbo

open Campfire.Views
open Campfire.Views.Helpers.Tag

/// `turbo_frame_tag(id, src:, target:, **attributes) { content }`'s attributes: the given ones
/// first, then `id`, `src` and `target` (nil ones dropped).
let turboFrameOptions (id: string) (src: string option) (target: string option) (attributes: Attrs) : Attrs =
    attributes.Attr("id", id).AttrOpt("src", src).AttrOpt("target", target)

/// `turbo_stream_from(*streamables)`. The signed stream name comes from the caller
/// (`Turbo::StreamsChannel.signed_stream_name`).
let turboStreamFrom (w: Out) (signedStreamName: string) : unit =
    builderTag w "turbo-cable-stream-source" (attrs().Attr("channel", "Turbo::StreamsChannel").Attr("signed-stream-name", signedStreamName))

/// `turbo_page_requires_reload_tag`, which `turbo_page_requires_reload` provides to `:head`.
let turboPageRequiresReloadTag (w: Out) : unit =
    builderTag w "meta" (attrs().Name("turbo-visit-control").Attr("content", "reload"))

/// `dom_id(record, prefix)`: "prefix_model_id".
let domId (model: string) (id: 'a) (prefix: string option) : string =
    match prefix with
    | Some prefix -> $"{prefix}_{model}_{id}"
    | None -> $"{model}_{id}"
