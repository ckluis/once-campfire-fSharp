// Port of rust/crates/storage/src/content_types.rs
/// The effective `config.active_storage` content type lists (Rails 8.2 engine defaults with
/// `load_defaults 8.2`, minus the types removed in `reference/config/initializers/vips.rb`).
module Campfire.Storage.ContentTypes

/// `variable_content_types` (bmp, ico and psd removed by config/initializers/vips.rb).
let variable: string[] =
    [| "image/png"; "image/gif"; "image/jpeg"; "image/tiff"; "image/webp"; "image/avif"; "image/heic"; "image/heif" |]

/// `web_image_content_types` (webp added by `load_defaults 7.2`).
let webImage: string[] = [| "image/png"; "image/jpeg"; "image/gif"; "image/webp" |]

let allowedInline: string[] =
    [| "image/webp"
       "image/avif"
       "image/png"
       "image/gif"
       "image/jpeg"
       "image/tiff"
       "image/bmp"
       "image/vnd.adobe.photoshop"
       "image/vnd.microsoft.icon"
       "application/pdf" |]

let serveAsBinary: string[] =
    [| "text/html"
       "image/svg+xml"
       "application/postscript"
       "application/x-shockwave-flash"
       "text/xml"
       "application/xml"
       "application/xhtml+xml"
       "application/mathml+xml"
       "text/cache-manifest" |]

[<Literal>]
let BinaryContentType = "application/octet-stream"

/// `ActiveStorage.video_preview_arguments` from `load_defaults 7.0`, already shell-split.
let videoPreviewArguments: string[] =
    [| "-vf"; @"select=eq(n\,0)+eq(key\,1)+gt(scene\,0.015),loop=loop=-1:size=2,trim=start_frame=1"; "-frames:v"; "1"; "-f"; "image2" |]

let isVariable (contentType: string) : bool = Array.contains contentType variable

let isWebImage (contentType: string) : bool = Array.contains contentType webImage

let isAllowedInline (contentType: string) : bool = Array.contains contentType allowedInline

let serveAsBinaryType (contentType: string) : bool = Array.contains contentType serveAsBinary

/// `content_type_for_serving`.
let forServing (contentType: string) : string =
    if serveAsBinaryType contentType then BinaryContentType else contentType

/// `forced_disposition_for_serving`: `Some "attachment"` for binary or non-inline types.
let forcedDisposition (contentType: string) : string option =
    if serveAsBinaryType contentType || not (isAllowedInline contentType) then Some "attachment" else None

/// `ActiveStorage.paths` is empty in Campfire, so the binaries come from `PATH`.
let ffmpegPath () : string = "ffmpeg"

let ffprobePath () : string = "ffprobe"
