// Port of rust/crates/views/src/helpers/users.rs
/// `UsersHelper`, `Users::AvatarsHelper`, `Users::FilterHelper`, `Users::ProfilesHelper`,
/// `Users::SidebarHelper` and `AccountsHelper`.
module Campfire.Views.Helpers.UsersHelper

open System
open System.Text
open Campfire.Routes
open Campfire.Views
open Campfire.Views.Helpers.Tag
open Campfire.Views.Helpers.Links
open Campfire.Views.Helpers.Turbo
open Campfire.Views.Helpers.Url

/// `Users::AvatarsHelper::AVATAR_COLORS`.
let avatarColors: string[] =
    [| "#AF2E1B"; "#CC6324"; "#3B4B59"; "#BFA07A"; "#ED8008"; "#ED3F1C"; "#BF1B1B"; "#736B1E"; "#D07B53"; "#736356"; "#AD1D1D"; "#BF7C2A"
       "#C09C6F"; "#698F9C"; "#7C956B"; "#5D618F"; "#3B3633"; "#67695E" |]

/// CRC-32 (IEEE, as `Zlib.crc32` and the `crc32fast` crate), byte at a time.
let private crcTable =
    Array.init 256 (fun n ->
        let mutable c = uint32 n
        for _ in 0..7 do
            c <- if c &&& 1u <> 0u then 0xEDB88320u ^^^ (c >>> 1) else c >>> 1
        c)

let private crc32 (bytes: ReadOnlySpan<byte>) : uint32 =
    let mutable crc = 0xFFFFFFFFu
    for b in bytes do
        crc <- crcTable[int ((crc ^^^ uint32 b) &&& 0xFFu)] ^^^ (crc >>> 8)
    crc ^^^ 0xFFFFFFFFu

/// `avatar_background_color(user)`: `Zlib.crc32(user.to_param)` picks the color.
let avatarBackgroundColor (userId: int64) : string =
    let crc = crc32 (ReadOnlySpan<byte>(Encoding.UTF8.GetBytes(string userId)))
    avatarColors[int (crc % uint32 avatarColors.Length)]

/// `char::is_alphanumeric`.
let private isAlphanumeric (rune: Rune) : bool =
    let table = UnicodeTables.alphanumeric
    let code = rune.Value
    let mutable low = 0
    let mutable high = table.Length - 1
    let mutable found = false
    while not found && low <= high do
        let middle = (low + high) / 2
        let struct (first, last) = table[middle]
        if code < first then high <- middle - 1
        elif code > last then low <- middle + 1
        else found <- true
    found

/// `User#initials`: `name.scan(/\b\w/).join`. Ruby's `\w` is ASCII-only while `\b` sees
/// Unicode word characters, so "Élodie" contributes nothing.
let initials (name: string) : string =
    let isWord (rune: Rune) = isAlphanumeric rune || rune.Value = int '_'
    let out = StringBuilder()
    let mutable previous: Rune voption = ValueNone
    for c in name.EnumerateRunes() do
        let ascii = c.IsAscii && (Char.IsAsciiLetterOrDigit(char c.Value) || c.Value = int '_')
        let afterWord =
            match previous with
            | ValueSome p -> isWord p
            | ValueNone -> false
        if ascii && not afterWord then out.Append(char c.Value) |> ignore
        previous <- ValueSome c
    out.ToString()

/// `User#title`: `[ name, bio ].compact_blank.join(" – ")`.
let userTitle (name: string) (bio: string option) : string =
    let blank (part: string) = String.IsNullOrWhiteSpace part
    String.Join(" – ", [ Some name; bio ] |> List.choose id |> List.filter (not << blank))

/// What `avatar_tag` needs to know about a user.
type AvatarUser =
    { Id: int64
      /// `User#title`.
      Title: string
      /// `fresh_user_avatar_path(user)`.
      AvatarPath: string }

/// `avatar_tag(user, **options)`: the options go to the image.
let avatarTag (w: Out) (ctx: ViewContext) (user: AvatarUser) (options: Attrs) : unit =
    linkTo w (Routes.user user.Id) (attrs().Title(user.Title).Class("btn avatar").Data("turbo_frame", "_top")) (fun w ->
        Assets.imageTag w ctx user.AvatarPath (attrs().AriaHidden().Size(48).Merge options))

/// `button_to_direct_room_with(user)`.
let buttonToDirectRoomWith (w: Out) (ctx: ViewContext) (userId: int64) : unit =
    Forms.buttonToBlock w (roomsDirectsWithUsers [ userId ]) (attrs().Class("btn btn--primary full-width txt--large")) (fun w ->
        Assets.imageTag w ctx "messages.svg" (attrs ()))

/// The bot curl commands in `accounts/bots/_bot`.
let curlTextLine (url: string) : string = $"curl -d 'Hello!' {url}"

let curlUploadLine (url: string) : string = $"curl -F \"attachment=@/path/to/file\" {url}"

/// `account_logo_tag(style:)`. A nil style leaves a trailing space in the class.
let accountLogoTag (w: Out) (ctx: ViewContext) (style: string option) : unit =
    contentTagBlock w "figure" (attrs().Class("account-logo avatar " + defaultArg style "")) (fun w ->
        Assets.imageTag w ctx ctx.Account.LogoUrl (attrs().Alt("Account logo").Size 300))

/// `profile_form_submit_button`.
let profileFormSubmitButton (w: Out) (ctx: ViewContext) : unit =
    contentTagBlock w "button" (attrs().Class("btn btn--reversed center txt-large").Type("submit")) (fun w ->
        Assets.imageTag w ctx "check.svg" (attrs().AriaHidden().Size 20)
        contentTagText w "span" (attrs().Class("for-screen-reader")) "Save changes")

/// `sidebar_turbo_frame_tag`'s attributes: `data: { turbo_permanent: true, controller: ...,
/// rooms_list_unread_class: ..., action: ... }` for `turbo_frame_tag`.
let sidebarTurboFrameOptions (src: string option) : Attrs =
    let data =
        Attrs(7)
            .Attr("data-turbo-permanent", true)
            .Attr("data-controller", "rooms-list read-rooms turbo-frame")
            .Attr("data-rooms-list-unread-class", "unread")
            // html_safe in the reference so "->" isn't escaped
            .Attr(
                "data-action",
                Safe
                    "presence:present@window->rooms-list#read read-rooms:read->rooms-list#read turbo:frame-load->rooms-list#loaded refresh-room:visible@window->turbo-frame#reload"
            )
    turboFrameOptions "user_sidebar" src (Some "_top") data

/// `sidebar_turbo_frame_tag(src:) { content }`.
let inline sidebarTurboFrameTag (w: Out) (src: string option) ([<InlineIfLambda>] content: Out -> unit) : unit =
    contentTagBlock w "turbo-frame" (sidebarTurboFrameOptions src) content

/// `user_filter_menu_tag { content }`.
let inline userFilterMenuTag (w: Out) ([<InlineIfLambda>] content: Out -> unit) : unit =
    let options =
        attrs()
            .Class("flex flex-column gap margin-none pad overflow-y constrain-height")
            .Data("controller", "filter")
            .Data("filter_active_class", "filter--active")
            .Data("filter_selected_class", "selected")
    contentTagBlock w "menu" options content

/// `user_filter_search_tag`.
let userFilterSearchTag (w: Out) : unit =
    builderTag
        w
        "input"
        (attrs()
            .Type("search")
            .Id("search")
            .Attr("autocorrect", "off")
            .Autocomplete("off")
            .Attr("data-1p-ignore", "true")
            .Class("input input--transparent full-width")
            .Placeholder("Filter…")
            .Data("action", "input->filter#filter"))
