// Port of rust/crates/views/templates/layouts/_lightbox.html (reference/app/views/layouts/_lightbox.html.erb)
module Campfire.Views.Templates.Layouts._Lightbox

open Campfire.Views
open Campfire.Views.Helpers

let private t0 = Utf8.lit "<dialog class=\"lightbox\" aria-label=\"Image Viewer (Press escape to close)\" data-lightbox-target=\"dialog\" data-action=\"close->lightbox#reset\">\n  <img src=\"\" class=\"lightbox__image\" data-lightbox-target=\"zoomedImage\" />\n\n  <form method=\"dialog\" class=\"lightbox__btn\">\n    <button class=\"btn\">\n      "
let private t1 = Utf8.lit "\n      <span class=\"for-screen-reader\">Close image viewer</span>\n    </button>\n  </form>\n\n  <a href=\"\" class=\"lightbox__btn--download btn hide-in-ios-pwa\" data-lightbox-target=\"download\">\n    "
let private t2 = Utf8.lit "\n    <span class=\"for-screen-reader\">Download file</span>\n  </a>\n\n  <button class=\"lightbox__btn--share btn\"\n      data-controller=\"web-share\"\n      data-action=\"web-share#share\"\n      data-web-share-files-value=\"\"\n      data-lightbox-target=\"share\">\n    "
let private t3 = Utf8.lit "\n    <span class=\"for-screen-reader\">Share file</span>\n  </button>\n</dialog>\n"

let render (w: Out) (ctx: ViewContext) : unit =
    w.Lit t0
    Assets.imageTag w ctx "remove.svg" (Tag.attrs().AriaHidden())
    w.Lit t1
    Assets.imageTag w ctx "download.svg" (Tag.attrs().AriaHidden())
    w.Lit t2
    Assets.imageTag w ctx "share.svg" (Tag.attrs().AriaHidden())
    w.Lit t3
