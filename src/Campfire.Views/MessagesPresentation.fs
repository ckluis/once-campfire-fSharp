// Port of rust/crates/views/src/messages/presentation.rs
/// `MessagesHelper#message_presentation` and `Messages::AttachmentPresentation`
/// (`reference/app/helpers/messages_helper.rb`, `reference/app/helpers/messages/attachment_presentation.rb`).
/// Each writes its HTML straight into the writer, as the template prints Rust's string with `|safe`.
module Campfire.Views.MessagesPresentation

open Campfire.Views
open Campfire.Views.Messages
open Campfire.Views.MessagesSupport

/// `Message::THUMBNAIL_MAX_WIDTH` / `THUMBNAIL_MAX_HEIGHT`.
let private thumbnailMaxWidth = 1200L
let private thumbnailMaxHeight = 800L

let private soundStart =
    Utf8.lit "<div class=\"sound\" data-controller=\"sound\" data-action=\"messages:play-&gt;sound#play\" data-sound-url-value=\""

let private soundButton = Utf8.lit "\"><button class=\"btn btn--plain\" data-action=\"sound#play\">🔊</button>"
let private divEnd = Utf8.lit "</div>"
let private imgWidth = Utf8.lit "<img width=\""
let private imgHeight = Utf8.lit "\" height=\""
let private imgSound = Utf8.lit "\" class=\"align--middle\" src=\""
let private imgEnd = Utf8.lit "\" />"

let private videoStart = Utf8.lit "<video src=\""
let private videoPoster = Utf8.lit "\" poster=\""
let private videoEnd =
    Utf8.lit "\" controls=\"controls\" preload=\"none\" width=\"100%\" height=\"100%\" class=\"message__attachment\"></video>"

let private imageStart = Utf8.lit "<img"
let private sizeWidth = Utf8.lit " width=\""
let private sizeHeight = Utf8.lit "\" height=\""
let private imageAttachment = Utf8.lit " class=\"message__attachment\" loading=\"lazy\" src=\""
let private linkStart =
    Utf8.lit "<a class=\"flex\" data-lightbox-target=\"image\" data-action=\"lightbox#open\" data-lightbox-url-value=\""
let private linkHref = Utf8.lit "\" href=\""
let private linkEnd = Utf8.lit "\">"
let private anchorEnd = Utf8.lit "</a>"

let private constrainedStart = Utf8.lit "<div class=\"max-inline-size center flex overflow-clip\" style=\"width: "
let private constrainedRatio = Utf8.lit "px; aspect-ratio: "
let private constrainedEnd = Utf8.lit ";\">"
let private unconstrained = Utf8.lit "<div class=\"max-inline-size center overflow-clip\">"

let private fileStart =
    Utf8.lit "<div class=\"flex-inline align-center gap-half\"><img class=\"colorize--black\" aria-hidden=\"true\" src=\""
let private fileIconEnd = Utf8.lit "\" width=\"22\" height=\"22\" /><span>"
let private fileNameEnd =
    Utf8.lit "</span><a class=\"btn message__action-btn hide-in-ios-pwa\" style=\"--width: auto;\" href=\""
let private fileDownloadIcon = Utf8.lit "\"><img aria-hidden=\"true\" src=\""
let private fileDownloadText = Utf8.lit "\" width=\"20\" height=\"20\" /><span class=\"for-screen-reader\">Download "
let private fileShareStart =
    Utf8.lit "</span></a><button class=\"btn message__action-btn\" style=\"--width: auto;\" data-controller=\"web-share\" data-action=\"web-share#share\" data-web-share-files-value=\""
let private fileShareIcon = Utf8.lit "\"><img aria-hidden=\"true\" src=\""
let private fileShareText = Utf8.lit "\" width=\"20\" height=\"20\" /><span class=\"for-screen-reader\">Share "
let private fileEnd = Utf8.lit "</span></button></div>"

/// `message_sound_presentation`: a play button followed by the sound's image or text.
let private soundPresentation (w: Out) (sound: SoundView) : unit =
    w.Lit soundStart
    w.Text sound.Url
    w.Lit soundButton
    match sound.Image, sound.Text with
    | Some image, _ ->
        w.Lit imgWidth
        w.Int(int64 image.Width)
        w.Lit imgHeight
        w.Int(int64 image.Height)
        w.Lit imgSound
        w.Text image.Src
        w.Lit imgEnd
    | None, Some text -> w.Text text
    | None, None -> ()
    w.Lit divEnd

/// `preview_dimensions`: the metadata size, scaled down to fit the thumbnail bounds.
let private previewDimensions (attachment: AttachmentView) : (RubyNumber * RubyNumber) option =
    match attachment.Width, attachment.Height with
    | Some width, Some height ->
        if width.ToF <= float thumbnailMaxWidth && height.ToF <= float thumbnailMaxHeight then
            Some(width, height)
        else
            let widthFactor = float thumbnailMaxWidth / width.ToF
            let heightFactor = float thumbnailMaxHeight / height.ToF
            let scale = min widthFactor heightFactor
            Some(Float(width.ToF * scale), Float(height.ToF * scale))
    | _ -> None

let private inlineMediaDimensionConstraints (w: Out) (dimensions: (RubyNumber * RubyNumber) option) (content: Out -> unit) : unit =
    match dimensions with
    | Some(width, height) ->
        let aspectRatio = Float(width.ToF / height.ToF)
        w.Lit constrainedStart
        w.Raw(width.Half.ToString())
        w.Lit constrainedRatio
        w.Raw(aspectRatio.ToString())
        w.Lit constrainedEnd
        content w
        w.Lit divEnd
    | None ->
        w.Lit unconstrained
        content w
        w.Lit divEnd

let private videoPreview (w: Out) (attachment: AttachmentView) (posterUrl: string) : unit =
    inlineMediaDimensionConstraints w (previewDimensions attachment) (fun w ->
        w.Lit videoStart
        w.Text attachment.BlobPath
        w.Lit videoPoster
        w.Text posterUrl
        w.Lit videoEnd)

let private lightboxedImagePreview (w: Out) (attachment: AttachmentView) (thumbUrl: string) : unit =
    let dimensions = previewDimensions attachment
    inlineMediaDimensionConstraints w dimensions (fun w ->
        w.Lit linkStart
        w.Text attachment.DownloadPath
        w.Lit linkHref
        w.Text attachment.BlobPath
        w.Lit linkEnd
        w.Lit imageStart
        match dimensions with
        | Some(width, height) ->
            w.Lit sizeWidth
            w.Raw(width.ToString())
            w.Lit sizeHeight
            w.Raw(height.ToString())
            w.Byte(byte '"')
        | None -> ()
        w.Lit imageAttachment
        w.Text thumbUrl
        w.Lit imgEnd
        w.Lit anchorEnd)

/// `render_link`: file icon, name, download link and share button, with no whitespace between.
let private fileLink (w: Out) (ctx: ViewContext) (attachment: AttachmentView) : unit =
    w.Lit fileStart
    w.Text(ctx.Asset "common-file-text.svg")
    w.Lit fileIconEnd
    w.Text attachment.Filename
    w.Lit fileNameEnd
    w.Text attachment.DownloadPath
    w.Lit fileDownloadIcon
    w.Text(ctx.Asset "download.svg")
    w.Lit fileDownloadText
    w.Text attachment.Filename
    w.Lit fileShareStart
    w.Text attachment.DownloadPath
    w.Lit fileShareIcon
    w.Text(ctx.Asset "share.svg")
    w.Lit fileShareText
    w.Text attachment.Filename
    w.Lit fileEnd

/// `Messages::AttachmentPresentation#render`.
let attachmentPresentation (w: Out) (ctx: ViewContext) (attachment: AttachmentView) : unit =
    match attachment.Preview with
    | Video posterUrl -> videoPreview w attachment posterUrl
    | Image thumbUrl -> lightboxedImagePreview w attachment thumbUrl
    | File -> fileLink w ctx attachment

/// `message_presentation(message)`.
let messagePresentation (w: Out) (ctx: ViewContext) (message: MessageView) : unit =
    match message.Content with
    | Attachment attachment -> attachmentPresentation w ctx attachment
    | Sound sound -> soundPresentation w sound
    | Text html -> w.Raw html
    // `messages/_message` renders `messages/_unrenderable` instead.
    | Unrenderable -> ()
