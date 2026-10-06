// Port of rust/crates/assets/src/tags.rs
/// The asset tags in reference/app/views/layouts/application.html.erb:
///
///   <%= stylesheet_link_tag :all, "data-turbo-track": "reload" %>
///   <%= javascript_importmap_tags %>
module Campfire.Assets.Tags

open System.Text
open Campfire.Ruby

/// What `stylesheet_link_tag` renders, plus the preload links Rails adds to the response's
/// `link` header while rendering it (`config.action_view.preload_links_header`, on by default).
type StylesheetTags = { Html: string; PreloadLinks: string list }

/// Rails' MAX_HEADER_SIZE for the preload `link` header (ActionView::Helpers::AssetTagHelper).
[<Literal>]
let private MaxLinkHeaderSize = 1_000

/// Propshaft::Helper#all_stylesheets_paths: every CSS logical path on the load path, sorted.
let allStylesheetPaths () : string[] = Embedded.stylesheets

/// `stylesheet_link_tag *sources, **options` as Propshaft::Helper renders it: one Rails
/// `stylesheet_link_tag` per source, joined by newlines. A missing source raises, like Rails.
let stylesheetLinkTag (sources: string seq) (options: (string * string) list) : StylesheetTags =
    let html = ResizeArray<string>()
    let preloadLinks = ResizeArray<string>()

    for source in sources do
        let href = Helpers.stylesheetPath source
        if href <> "" && not (href.StartsWith "data:") then
            preloadLinks.Add $"<{href}>; rel=preload; as=style; nopush"

        let tag = StringBuilder()
        tag.Append("<link rel=\"stylesheet\" href=\"").Append(Erb.htmlEscape href).Append('"') |> ignore
        for name, value in options do
            tag.Append(' ').Append(name).Append("=\"").Append(Erb.htmlEscape value).Append('"') |> ignore
        tag.Append(" />") |> ignore
        html.Add(tag.ToString())

    { Html = String.concat "\n" html; PreloadLinks = List.ofSeq preloadLinks }

/// `stylesheet_link_tag :all, **options`
let stylesheetLinkTagAll (options: (string * string) list) : StylesheetTags = stylesheetLinkTag (allStylesheetPaths ()) options

/// Appends preload links to a response's existing `link` header value the way
/// `send_preload_links_header` does: a link that would push the header past 1,000 bytes is
/// left out (Propshaft renders each stylesheet separately, so later, shorter ones can still fit).
/// Sizes are bytes, as Rust's `len()`; the links are ASCII unless a path says otherwise.
let private appendPreloadLinksUncached (header: string) (preloadLinks: string seq) : string =
    let utf8 (s: string) = Encoding.UTF8.GetByteCount s
    let out = StringBuilder(header, MaxLinkHeaderSize + 16)
    let mutable size = utf8 header
    for link in preloadLinks do
        let linkSize = utf8 link
        if size + linkSize <= MaxLinkHeaderSize then
            if size > 0 then
                out.Append ',' |> ignore
                size <- size + 1
            out.Append link |> ignore
            size <- size + linkSize
    out.ToString()

/// The last answer, for the layout's own list of links (one object for the life of the process) and the header a
/// request has: the same every time, so a page does not build the string again.
[<Sealed>]
type private LastPreload(header: string, links: obj, result: string) =
    member _.Header = header
    member _.Links = links
    member _.Result = result

/// Starts as an answer no real list of links matches.
let mutable private lastPreload = LastPreload("", obj (), "")

let appendPreloadLinks (header: string) (preloadLinks: string seq) : string =
    match preloadLinks with
    | :? (string list) as links ->
        // An F# list is immutable, so the same object is the same links.
        let last = lastPreload
        match last with
        | last when obj.ReferenceEquals(last.Links, links) && last.Header = header -> last.Result
        | _ ->
            let result = appendPreloadLinksUncached header preloadLinks
            lastPreload <- LastPreload(header, (links :> obj), result)
            result
    | _ -> appendPreloadLinksUncached header preloadLinks

/// `javascript_importmap_tags`: the import map, a modulepreload link per pin, and the
/// `import "application"` module script. Computed at build time from config/importmap.rb.
let javascriptImportmapTags () : string = Embedded.importmapTags.Value
