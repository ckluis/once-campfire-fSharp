// Port of rust/crates/views/src/helpers/assets.rs
/// `image_tag` and asset resolution (`AssetTagHelper`, `AssetUrlHelper`).
module Campfire.Views.Helpers.Assets

open System
open Campfire.Views
open Campfire.Views.Helpers.Tag

let private isScheme (scheme: ReadOnlySpan<char>) : bool =
    let mutable all = scheme.Length > 0
    for c in scheme do
        if not (Char.IsAsciiLetter c || c = '-') then all <- false
    all

/// `asset_path(source)`: URLs and absolute paths pass through; logical asset paths are digested.
let assetPath (ctx: ViewContext) (source: string) : string =
    let isUrl =
        source.StartsWith('/')
        || source.StartsWith("data:", StringComparison.Ordinal)
        || source.StartsWith("cid:", StringComparison.Ordinal)
        || (match source.IndexOf("://", StringComparison.Ordinal) with
            | -1 -> false
            | at -> isScheme (source.AsSpan(0, at)))
    if isUrl then source else ctx.Asset source

/// `image_tag(source, options)`: the options in order, then `src`, then `width`/`height` from
/// `size:` ("20" or "20x30").
let imageTag (w: Out) (ctx: ViewContext) (source: string) (options: Attrs) : unit =
    let size = options.Remove "size"
    options.Set("src", ValueSome(Text(assetPath ctx source)))
    match size with
    | ValueSome size ->
        let size = size.AsString
        let width, height =
            match size.IndexOf 'x' with
            | -1 -> size, size
            | at -> size.Substring(0, at), size.Substring(at + 1)
        options.Set("width", ValueSome(Text width))
        options.Set("height", ValueSome(Text height))
    | ValueNone -> ()
    legacyTag w "img" options
