// Port of rust/crates/richtext/src/content.rs
//
// `ActionText::Content`: loading (canonicalization), attachment rendering, and plain text.
//
// Each Ruby step that serializes a node and parses the markup back (`Fragment#replace` with a
// string, `inner_html=`, a filter returning HTML) does the same here, in the same parse context,
// because those round trips are where the output takes its shape.
namespace Campfire.RichText

open System
open System.Text
open Campfire.RailsCompat
open Campfire.Ruby
open Campfire.RichText.RubyExt

/// A fragment of rich text living in a DOM arena.
type Content = { Dom: Dom; Root: NodeId }

module Content =
    [<Literal>]
    let AttachmentTag = "action-text-attachment"

    let attachmentNodes (dom: Dom) (root: NodeId) : NodeId list =
        dom.Descendants root |> Seq.filter (fun n -> dom.IsNamed(n, AttachmentTag)) |> List.ofSeq

    /// `ActionText::TrixAttachment::ATTRIBUTES`, in order
    let private trixAttributes =
        [ "sgid", "sgid"
          "contentType", "content-type"
          "url", "url"
          "href", "href"
          "filename", "filename"
          "filesize", "filesize"
          "width", "width"
          "height", "height"
          "previewable", "previewable"
          "content", "content"
          "caption", "caption"
          "presentation", "presentation" ]

    /// The attributes `data-trix-attachment` and `data-trix-attributes` carry, merged in that order,
    /// keyed by their Trix names.
    let private trixAttributesOf (dom: Dom) (node: NodeId) : Result<ResizeArray<string * Value>, RenderError> =
        let attributes = ResizeArray<string * Value>()
        let mutable failed = ValueNone
        for name in [ "data-trix-attachment"; "data-trix-attributes" ] do
            if failed.IsNone then
                let parsed =
                    match dom.Attr(node, name) with
                    // Unparseable JSON is logged and treated as no attributes
                    | ValueSome json -> defaultArg (jsonParse json) Value.Null
                    | ValueNone -> Value.Null
                match parsed with
                | Value.Null
                | Value.Bool false -> ()
                | Value.Object map ->
                    for (key, value) in map do
                        match trixAttributes |> List.tryFind (fun (trix, _) -> trix = key) with
                        | Some(trix, _) ->
                            match attributes.FindIndex(fun (k, _) -> k = trix) with
                            | -1 -> attributes.Add((trix, value))
                            | i -> attributes[i] <- (trix, value)
                        | None -> ()
                | _ -> failed <- ValueSome(Raised "NoMethodError: merge")
        match failed with
        | ValueSome e -> Error e
        | ValueNone -> Ok attributes

    /// `fragment_by_converting_trix_attachments`: `figure[data-trix-attachment]` (or any element
    /// carrying the attribute) becomes an `<action-text-attachment>`, or disappears if it has none of
    /// the attachment attributes.
    let private convertTrixAttachments (dom: Dom) (root: NodeId) (ctx: RenderContext) : Result<unit, RenderError> =
        let nodes = dom.Descendants root |> Seq.filter (fun n -> dom.HasAttr(n, "data-trix-attachment")) |> List.ofSeq
        result {
            for node in nodes do
                let! attributes = trixAttributesOf dom node
                let elementAttrs = ResizeArray<string * string>()
                for name in Sanitizer.attachmentAttributes do
                    let trixName = trixAttributes |> List.tryFind (fun (_, dashed) -> dashed = name) |> Option.map fst
                    match attributes |> Seq.tryFind (fun (k, _) -> Some k = trixName) with
                    | Some(_, value) -> elementAttrs.Add((name, jsonValueToS value))
                    | None -> ()
                let! replacement =
                    if elementAttrs.Count = 0 then
                        Ok ""
                    else
                        let element = dom.CreateElement(AttachmentTag, List.ofSeq elementAttrs)
                        // Attachment.from_node resolves the attachable, which may raise
                        Attachables.attachmentFromNode dom element ctx
                        |> Result.map (fun _ -> dom.ToHtml element)
                do! parseErr (dom.ReplaceWithHtml(node, replacement))
        }

    let private isGalleryAttachment (dom: Dom) (node: NodeId) : bool =
        dom.IsNamed(node, AttachmentTag) && dom.Attr(node, "presentation") = ValueSome "gallery"

    /// `AttachmentGallery.find_attachment_gallery_nodes`: `div:has(A + A)` for gallery attachments A,
    /// whose children are all gallery attachments or newline/space text.
    let attachmentGalleryNodes (dom: Dom) (root: NodeId) : NodeId list =
        dom.Descendants root
        |> Seq.filter (fun d -> dom.IsNamed(d, "div"))
        |> Seq.filter (fun div ->
            dom.Descendants div
            |> Seq.exists (fun n ->
                isGalleryAttachment dom n
                && (let parent = (dom.Parent n).Value
                    let siblings = dom.ElementChildren parent |> Array.ofList
                    let index = siblings |> Array.findIndex (fun s -> s = n)
                    index > 0 && isGalleryAttachment dom siblings[index - 1])))
        |> Seq.filter (fun div ->
            dom.Children div
            |> Seq.forall (fun child ->
                match dom.Text child with
                | ValueSome text -> text |> Seq.forall (fun c -> c = '\n' || c = ' ')
                | ValueNone -> isGalleryAttachment dom child))
        |> List.ofSeq

    /// Loads (and canonicalizes) `html` as a new fragment in `dom`.
    let loadInto (dom: Dom) (html: string) (ctx: RenderContext) : Result<NodeId, RenderError> =
        result {
            let! root = parseErr (dom.ParseFragment(RubyString.strip html))
            do! convertTrixAttachments dom root ctx
            for node in attachmentNodes dom root do
                do! parseErr (dom.SetInnerHtml(node, ""))
            for gallery in attachmentGalleryNodes dom root do
                let html = "<div>" + dom.InnerHtml gallery + "</div>"
                do! parseErr (dom.ReplaceWithHtml(gallery, html))
            return root
        }

    /// `ActionText::Content.new(html)`, which is also how a stored body loads: canonicalized.
    let load (html: string) (ctx: RenderContext) : Result<Content, RenderError> =
        let dom = Dom()
        loadInto dom html ctx |> Result.map (fun root -> { Dom = dom; Root = root })

    /// `ActionText::Content.new(html, canonicalize: false)`, i.e. `Fragment.from_html`.
    let wrap (html: string) : Result<Content, RenderError> =
        let dom = Dom()
        parseErr (dom.ParseFragment(RubyString.strip html)) |> Result.map (fun root -> { Dom = dom; Root = root })

    let toHtml (content: Content) : string = content.Dom.ToHtml content.Root

    /// `render_attachments`' first step: an attachment's `content` attribute is sanitized with Action
    /// Text's allowlist, and dropped if that leaves nothing.
    let private sanitizeContentAttribute (dom: Dom) (node: NodeId) : Result<unit, RenderError> =
        match dom.RemoveAttr(node, "content") with
        | ValueSome content ->
            Sanitizer.sanitize content SafeList.actionText
            |> parseErr
            |> Result.map (fun sanitized -> if not (isBlank sanitized) then dom.SetAttr(node, "content", sanitized))
        | ValueNone -> Ok()

    /// `ActionText::Content#to_plain_text`
    let toPlainText (content: Content) (ctx: RenderContext) : Result<string, RenderError> =
        let dom = content.Dom.Clone()
        let root = content.Root
        result {
            for node in attachmentNodes dom root do
                do! sanitizeContentAttribute dom node
                let! attachment = Attachables.attachmentFromNode dom node ctx
                match Attachables.attachmentPlainText attachment with
                | PlainTextRepresentation.Markup text -> do! parseErr (dom.ReplaceWithHtml(node, text))
                | PlainTextRepresentation.Fragment fragmentHtml ->
                    let! fragment = loadInto dom fragmentHtml ctx
                    let children = dom.Children fragment |> Seq.toArray
                    dom.ReplaceWithNodes(node, children)
            return PlainText.nodeToPlainText dom root
        }

    /// `Attachment#with_full_attributes`: a new node carrying the node's attachment attributes, the
    /// attachable's own (sgid and content type, for a user), and the node's sgid if it had one.
    let private nodeWithFullAttributes (dom: Dom) (node: NodeId) (attachable: Attachable) : Result<NodeId, RenderError> =
        let attrs = ResizeArray<string * string>()
        for name in Sanitizer.attachmentAttributes do
            let value =
                match name, attachable with
                | "sgid", Attachable.User user -> Some(defaultValueArg (dom.Attr(node, "sgid")) user.AttachableSgid)
                | "content-type", Attachable.User _ -> Some Attachables.MentionContentType
                | _ -> match dom.Attr(node, name) with ValueSome v -> Some v | ValueNone -> None
            match value with
            | Some v -> attrs.Add((name, v))
            | None -> ()
        if attrs.Count = 0 then
            // from_attributes returns nil, and the render block calls #node on it
            Error(Raised "NoMethodError: node for nil")
        else
            Ok(dom.CreateElement(AttachmentTag, List.ofSeq attrs))

    /// How deep content attachments render inside one another. Each level parses and sanitizes
    /// everything nested below it again, so Rails' unbounded nesting makes rendering quadratic in the
    /// body's size; deeper content attachments render empty. Nothing Campfire's composer makes nests
    /// them at all.
    [<Literal>]
    let MaxContentAttachmentDepth = 8

    [<Literal>]
    let private GalleryAttachmentPresentation = "gallery"

    /// `render`, for content `depth` content attachments down.
    let rec private renderNested (content: Content) (ctx: RenderContext) (depth: int) : Result<string, RenderError> =
        let dom = content.Dom.Clone()
        let root = content.Root
        result {
            do! renderAttachments dom root ctx depth
            do! renderAttachmentGalleries dom root ctx depth
            return! Sanitizer.sanitize (dom.ToHtml root) SafeList.actionText |> parseErr
        }

    /// `render_action_text_attachment`, with nested content attachments rendered through
    /// `ContentAttachment#to_html` (the content partial, without the layout).
    and private renderAttachmentHtmlAt (attachment: Attachment) (ctx: RenderContext) (depth: int) : Result<string, RenderError> =
        Attachables.renderAttachment attachment (fun contentHtml ->
            if depth >= MaxContentAttachmentDepth then
                Ok ""
            else
                result {
                    let! nested = load contentHtml ctx
                    let! rendered = renderNested nested ctx (depth + 1)
                    return rendered + "\n"
                })

    and private renderAttachments (dom: Dom) (root: NodeId) (ctx: RenderContext) (depth: int) : Result<unit, RenderError> =
        result {
            for node in attachmentNodes dom root do
                do! sanitizeContentAttribute dom node
                let! attachment = Attachables.attachmentFromNode dom node ctx
                let! full = nodeWithFullAttributes dom node attachment.Attachable
                let attachment =
                    { Attachable = attachment.Attachable
                      Caption = presence (dom.Attr(full, "caption")) |> (function ValueSome c -> Some c | ValueNone -> None) }
                let! html = renderAttachmentHtmlAt attachment ctx depth
                do! parseErr (dom.SetInnerHtml(full, html))
                let replacement = dom.ToHtml full
                do! parseErr (dom.ReplaceWithHtml(node, replacement))
        }

    and private renderAttachmentGalleries (dom: Dom) (root: NodeId) (ctx: RenderContext) (depth: int) : Result<unit, RenderError> =
        result {
            for gallery in attachmentGalleryNodes dom root do
                let members = dom.Descendants gallery |> Seq.filter (isGalleryAttachment dom) |> List.ofSeq
                let rendered = StringBuilder()
                for memberNode in members do
                    let! attachment = Attachables.attachmentFromNode dom memberNode ctx
                    let! full = nodeWithFullAttributes dom memberNode attachment.Attachable
                    let! html = renderAttachmentHtmlAt attachment ctx depth
                    do! parseErr (dom.SetInnerHtml(full, html))
                    rendered.Append(dom.ToHtml full) |> ignore
                let html =
                    "<div class=\"attachment-gallery attachment-gallery--"
                    + string members.Length
                    + "\">\n  "
                    + rendered.ToString()
                    + "\n</div>"
                do! parseErr (dom.ReplaceWithHtml(gallery, html))
        }

    /// `render_action_text_attachment`, with nested content attachments rendered through
    /// `ContentAttachment#to_html` (the content partial, without the layout).
    let renderAttachmentHtml (attachment: Attachment) (ctx: RenderContext) : Result<string, RenderError> =
        renderAttachmentHtmlAt attachment ctx 0

    /// `render_action_text_content(content)`: attachments and galleries rendered, then sanitized
    /// with Action Text's allowlist.
    let render (content: Content) (ctx: RenderContext) : Result<string, RenderError> = renderNested content ctx 0

    /// `Content#to_s`: the content partial inside `layouts/action_text/contents/_content.html.erb`,
    /// which Campfire overrides with a `lexxy-content` wrapper.
    let toRenderedHtmlWithLayout (content: Content) (ctx: RenderContext) : Result<string, RenderError> =
        render content ctx |> Result.map (fun html -> "<div class=\"lexxy-content\">\n  " + html + "\n</div>\n")
