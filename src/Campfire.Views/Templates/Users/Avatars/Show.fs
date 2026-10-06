// Port of rust/crates/views/templates/users/avatars/show.svg (reference/app/views/users/avatars/show.svg.erb)
/// `users/avatars/show.svg.erb`: the initials avatar for users without an uploaded one.
module Campfire.Views.Templates.Users.Avatars.Show

open Campfire.Views
open Campfire.Views.Helpers

let private t0 = Utf8.lit "<svg version=\"1.1\" xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"\n  viewBox=\"0 0 512 512\" class=\"avatar\" aria-hidden=\"true\">\n  <defs>\n    <clipPath id=\"porthole\">\n      <circle cx=\"50%\" cy=\"50%\" r=\"50%\" />\n    </clipPath>\n  </defs>\n\n  <g>\n    <rect width=\"100%\" height=\"100%\" rx=\"50\" fill=\""
let private t1 = Utf8.lit "\" />\n\n    <text x=\"50%\" y=\"50%\" fill=\"#FFFFFF\"\n      text-anchor=\"middle\" dy=\"0.35em\"\n      "
let private t2 = Utf8.lit "textLength=\"85%\" lengthAdjust=\"spacingAndGlyphs\""
let private t3 = Utf8.lit "\n      font-family=\"-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, Helvetica, Arial, sans-serif\"\n      font-size=\"230\"\n      font-weight=\"800\"\n      letter-spacing=\"-5\">\n      "
let private t4 = Utf8.lit "\n    </text>\n  </g>\n</svg>\n"

/// `userId` is the user's id and `initials` `User#initials`.
let render (w: Out) (userId: int64) (initials: string) : unit =
    w.Lit t0
    w.Text(UsersHelper.avatarBackgroundColor userId)
    w.Lit t1
    // `initials.chars().count() >= 3`
    let mutable characters = 0
    for _ in initials.EnumerateRunes() do
        characters <- characters + 1
    if characters >= 3 then
        w.Lit t2
    w.Lit t3
    w.Text initials
    w.Lit t4
