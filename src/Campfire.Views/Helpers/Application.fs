// Port of rust/crates/views/src/helpers/application.rs
/// `ApplicationHelper`, `CableHelper`, `VersionHelper`, `TimeHelper`, `ClipboardHelper`,
/// `DropTargetHelper` and `QrCodeHelper` (`reference/app/helpers/*.rb`).
module Campfire.Views.Helpers.Application

open System
open System.Text
open Campfire.RailsCompat
open Campfire.Routes
open Campfire.Views
open Campfire.Views.Helpers.Tag
open Campfire.Views.Helpers.Links

/// `page_title_tag`: `@page_title || "Campfire"`.
let pageTitleTag (w: Out) (pageTitle: string option) : unit =
    contentTagText w "title" (attrs ()) (defaultArg pageTitle "Campfire")

/// `current_user_meta_tags`.
let currentUserMetaTags (w: Out) (ctx: ViewContext) : unit =
    match ctx.CurrentUser with
    | Some user ->
        legacyTag w "meta" (attrs().Name("current-user-id").Attr("content", user.Id))
        legacyTag w "meta" (attrs().Name("current-user-name").Attr("content", user.Name))
    | None -> ()

/// `script_aware_action_cable_meta_tag`.
let scriptAwareActionCableMetaTag (w: Out) (ctx: ViewContext) : unit =
    builderTag w "meta" (attrs().Name("action-cable-url").Attr("content", ctx.CableUrl))

/// `custom_styles_tag`: the account's CSS, unescaped.
let customStylesTag (w: Out) (ctx: ViewContext) : unit =
    match ctx.CustomStyles with
    | Some styles -> contentTag w "style" (attrs().Data("turbo_track", "reload")) styles
    | None -> ()

let private adminClass = Utf8.lit "admin"
let private accountLogoClass = Utf8.lit "account-has-logo"

/// `body_classes`: `[ @body_class, admin_body_class, account_logo_body_class ].compact.join(" ")`.
/// Written escaped, straight into the buffer, as the template's `{{ }}` would.
let writeBodyClasses (w: Out) (ctx: ViewContext) (bodyClass: string option) : unit =
    let mutable any = false
    match bodyClass with
    | Some bodyClass ->
        w.Text bodyClass
        any <- true
    | None -> ()
    if ctx.CanAdminister then
        if any then w.Byte(byte ' ')
        w.Lit adminClass
        any <- true
    if ctx.Account.HasLogo then
        if any then w.Byte(byte ' ')
        w.Lit accountLogoClass

/// `body_classes` as the helper returns it, a string the template escapes (tests and tools; the layout
/// writes it with `writeBodyClasses`).
let bodyClasses (ctx: ViewContext) (bodyClass: string option) : string =
    let admin = if ctx.CanAdminister then Some "admin" else None
    let logo = if ctx.Account.HasLogo then Some "account-has-logo" else None
    String.Join(" ", [ bodyClass; admin; logo ] |> List.choose id)

/// `link_back_to(destination)`.
let linkBackTo (w: Out) (ctx: ViewContext) (destination: string) : unit =
    linkTo w destination (attrs().Class("btn")) (fun w ->
        Assets.imageTag w ctx "arrow-left.svg" (attrs().AriaHidden().Size 20)
        contentTagText w "span" (attrs().Class("for-screen-reader")) "Go Back")

/// `link_back`: to the referrer, unless it's missing or the current page.
let linkBack (w: Out) (ctx: ViewContext) : unit =
    let backUrl =
        match ctx.Referrer with
        | Some referrer when referrer <> ctx.RequestUrl -> referrer
        | _ -> Routes.root ()
    linkBackTo w ctx backUrl

/// `RoomsHelper#link_back_to_last_room_visited`.
let linkBackToLastRoomVisited (w: Out) (ctx: ViewContext) : unit =
    match ctx.LastRoomVisitedId with
    | Some roomId -> linkBackTo w ctx (Routes.room roomId)
    | None -> linkBackTo w ctx (Routes.root ())

/// `version_badge`.
let versionBadge (w: Out) (ctx: ViewContext) : unit =
    contentTagText w "span" (attrs().Class("version-badge")) ctx.AppVersion

/// `button_to_copy_to_clipboard(url) { content }`.
let inline buttonToCopyToClipboard (w: Out) (url: string) ([<InlineIfLambda>] content: Out -> unit) : unit =
    let options =
        attrs()
            .Class("btn")
            .Data("controller", "copy-to-clipboard")
            .Data("action", "copy-to-clipboard#copy")
            .Data("copy_to_clipboard_success_class", "btn--success")
            .Data("copy_to_clipboard_content_value", url)
    contentTagBlock w "button" options content

/// `link_to_zoom_qr_code(url) { content }`: the QR code route takes the URL,
/// `Base64.urlsafe_encode64`d (reference/app/helpers/qr_code_helper.rb).
let inline linkToZoomQrCode (w: Out) (url: string) ([<InlineIfLambda>] content: Out -> unit) : unit =
    let path = Routes.qrCode (RailsEncoding.urlsafeEncodePadded (Encoding.UTF8.GetBytes url))
    let options =
        attrs().Class("btn").Data("lightbox_target", "image").Data("action", "lightbox#open").Data("lightbox_url_value", path)
    linkTo w path options content

/// `web_share_session_button(url, title, text) { content }` (`Users::ProfilesHelper`).
let inline webShareSessionButton (w: Out) (url: string) (title: string) (text: string) ([<InlineIfLambda>] content: Out -> unit) : unit =
    let options =
        attrs()
            .Class("btn")
            .Hidden()
            .Data("controller", "web-share")
            .Data("action", "web-share#share")
            .Data("web_share_url_value", url)
            .Data("web_share_text_value", text)
            .Data("web_share_title_value", title)
    contentTagBlock w "button" options content

/// `truncate(text, length:, omission:)` with Rails' default of no separator: the result,
/// omission included, is at most `length` characters. Returns plain text (escape on output).
let truncate (text: string) (length: int) (omission: string) : string =
    let characters (s: string) = s.EnumerateRunes() |> Seq.length
    if characters text <= length then
        text
    else
        let keep = max (length - characters omission) 0
        let out = StringBuilder()
        let mutable taken = 0
        let mutable runes = text.EnumerateRunes()
        while taken < keep && runes.MoveNext() do
            out.Append(runes.Current.ToString()) |> ignore
            taken <- taken + 1
        out.Append(omission).ToString()

/// `table`'s mapping of `code`: `table` is flat pairs of a code point and what it maps to, in order.
let private simple (table: int[]) (code: int) : int voption =
    let mutable low = 0
    let mutable high = table.Length / 2 - 1
    let mutable found = ValueNone
    while found.IsNone && low <= high do
        let middle = (low + high) / 2
        let key = table[middle * 2]
        if code < key then high <- middle - 1
        elif code > key then low <- middle + 1
        else found <- ValueSome table[middle * 2 + 1]
    found

let private special (table: (int * int[])[]) (code: int) : int[] voption =
    match table |> Array.tryFind (fun (key, _) -> key = code) with
    | Some(_, mapped) -> ValueSome mapped
    | None -> ValueNone

/// `char::to_uppercase` / `char::to_lowercase`, from the tables Rust's standard library gives
/// (`UnicodeTables`, generated by `bin/views-differential --unicode`) rather than .NET's own, which
/// leave out the Turkish dotless i, special casing, and characters Unicode added since.
let private mapped (simpleTable: int[]) (specialTable: (int * int[])[]) (rune: Rune) : string =
    match special specialTable rune.Value with
    | ValueSome codes -> String.Join("", codes |> Array.map (fun code -> Rune(code).ToString()))
    | ValueNone ->
        match simple simpleTable rune.Value with
        | ValueSome code -> Rune(code).ToString()
        | ValueNone -> rune.ToString()

let private upperCase (rune: Rune) : string = mapped UnicodeTables.upperSimple UnicodeTables.upperSpecial rune
let private lowerCase (rune: Rune) : string = mapped UnicodeTables.lowerSimple UnicodeTables.lowerSpecial rune

let private inRanges (table: struct (int * int)[]) (code: int) : bool =
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

/// Whether, going from `index` in `runes` in direction `step`, the first character that isn't case-ignorable is cased
/// (`case_ignorable_then_cased`).
let private caseIgnorableThenCased (runes: Rune[]) (index: int) (step: int) : bool =
    let mutable at = index
    while at >= 0 && at < runes.Length && inRanges UnicodeTables.caseIgnorable runes[at].Value do
        at <- at + step
    at >= 0 && at < runes.Length && inRanges UnicodeTables.casedNotIgnorable runes[at].Value

/// `str::to_lowercase`: each character's `char::to_lowercase`, except that a capital sigma becomes ς at the end of a
/// word (a cased letter before it, skipping case-ignorable characters, and none after) and σ elsewhere.
let toLowercase (text: string) : string =
    let runes = text.EnumerateRunes() |> Seq.toArray
    let out = StringBuilder(text.Length)
    for index in 0 .. runes.Length - 1 do
        let rune = runes[index]
        if rune.Value = 0x3A3 then
            let final = caseIgnorableThenCased runes (index - 1) -1 && not (caseIgnorableThenCased runes (index + 1) 1)
            out.Append(if final then 'ς' else 'σ') |> ignore
        else
            out.Append(lowerCase rune) |> ignore
    out.ToString()

/// `String#capitalize`: first character upcased, the rest downcased.
let capitalize (text: string) : string =
    let out = StringBuilder(text.Length)
    let mutable first = true
    for rune in text.EnumerateRunes() do
        out.Append(if first then upperCase rune else lowerCase rune) |> ignore
        first <- false
    out.ToString()

/// `Array#to_sentence` with the default English connectors, or a custom `two_words_connector`.
let toSentence (items: string list) (twoWordsConnector: string) : string =
    match items with
    | [] -> ""
    | [ one ] -> one
    | [ one; two ] -> one + twoWordsConnector + two
    | _ ->
        let rest = items |> List.take (items.Length - 1)
        String.Join(", ", rest) + ", and " + List.last items
