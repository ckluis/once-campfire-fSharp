// Ports of the #[cfg(test)] modules in rust/crates/views/src/helpers/{application,users}.rs and of the
// shared partial tests in rust/crates/views/tests (autocompletable JSON, search paths)
module Campfire.Views.Tests.HelpersTests

open Xunit
open Campfire.Views
open Campfire.Views.Helpers

// helpers/application.rs

[<Fact>]
let ``truncates like rails`` () =
    Assert.Equal("abc…", Application.truncate "abcdef" 4 "…")
    Assert.Equal("abcd", Application.truncate "abcd" 4 "…")
    // Characters, not UTF-16 units.
    Assert.Equal("😀😀…", Application.truncate "😀😀😀😀😀" 3 "…")

[<Fact>]
let ``capitalizes like rust`` () =
    Assert.Equal("Chrome", Application.capitalize "cHROME")
    Assert.Equal("", Application.capitalize "")
    // Special casing: the first character may become several, and the Turkish dotless i has an upper case.
    Assert.Equal("SSa", Application.capitalize "ßA")
    Assert.Equal("I", Application.capitalize "ı")
    Assert.Equal("i̇", Application.capitalize "i̇" |> fun s -> s.ToLowerInvariant())

[<Fact>]
let ``builds sentences`` () =
    Assert.Equal("A+B", Application.toSentence [ "A"; "B" ] "+")
    Assert.Equal("A, B, and C", Application.toSentence [ "A"; "B"; "C" ] "+")
    Assert.Equal("A", Application.toSentence [ "A" ] " and ")
    Assert.Equal("A and B", Application.toSentence [ "A"; "B" ] " and ")
    Assert.Equal("", Application.toSentence [] " and ")

[<Fact>]
let ``qr code links take the url urlsafe base64 encoded`` () =
    // `Base64.urlsafe_encode64` in the reference: padded, with `-` and `_`.
    let link (url: string) = Render.text (fun w -> Application.linkToZoomQrCode w url ignore)
    Assert.Contains("href=\"/qr_code/aHR0cDovL3gvP2E=\"", link "http://x/?a")
    Assert.Contains("href=\"/qr_code/aHR0cDovL3gvPz4_\"", link "http://x/?>?")

[<Fact>]
let ``body classes join the page the admin and the logo`` () =
    let ctx (admin: bool) (logo: bool) : ViewContext =
        { CurrentUser = Some { Id = 1L; Name = "n"; Administrator = admin; Bot = false; AvatarUrl = "/a" }
          Account = { Name = "A"; LogoUrl = "/l"; HasLogo = logo }
          FlashNotice = None
          FlashAlert = None
          Platform = Platform.none
          VapidPublicKey = None
          AssetPath = id
          ImportmapTags = ""
          StylesheetTags = ""
          CustomStyles = None
          CableUrl = "/cable"
          BaseUrl = "http://x"
          RequestUrl = "http://x/"
          Referrer = None
          LastRoomVisitedId = None
          AppVersion = "0" }
    Assert.Equal("sidebar admin account-has-logo", Application.bodyClasses (ctx true true) (Some "sidebar"))
    Assert.Equal("admin", Application.bodyClasses (ctx true false) None)
    Assert.Equal("", Application.bodyClasses (ctx false false) None)
    // `compact` drops only nil.
    Assert.Equal(" admin", Application.bodyClasses (ctx true false) (Some ""))
    // The layout writes it escaped straight into the buffer: what `{{ body_classes }}` prints.
    for admin in [ true; false ] do
        for logo in [ true; false ] do
            for bodyClass in [ None; Some ""; Some "sidebar"; Some "a&\"b<'c'>" ] do
                let expected = Campfire.Ruby.Erb.htmlEscape (Application.bodyClasses (ctx admin logo) bodyClass)
                Assert.Equal(expected, Render.text (fun w -> Application.writeBodyClasses w (ctx admin logo) bodyClass))

// helpers/users.rs

[<Fact>]
let ``computes initials like ruby`` () =
    // `name.scan(/\b\w/).join`: Ruby's `\w` is ASCII-only while `\b` sees Unicode word characters.
    Assert.Equal("SoB3_", UsersHelper.initials "Élodie Ünal-Smith o'Brien 3po _x ñ")
    Assert.Equal("DHH", UsersHelper.initials "David Heinemeier Hansson")

[<Fact>]
let ``avatar colors like zlib crc32`` () =
    // `AVATAR_COLORS[Zlib.crc32(id.to_s) % 18]` in the reference.
    for (id, index) in [ 1L, 11; 2L, 13; 42L, 8; 1000L, 3 ] do
        Assert.Equal(UsersHelper.avatarColors[index], UsersHelper.avatarBackgroundColor id)

[<Fact>]
let ``a title is the name and the bio unless the bio is blank`` () =
    Assert.Equal("Kevin – Programmer", UsersHelper.userTitle "Kevin" (Some "Programmer"))
    Assert.Equal("Kevin", UsersHelper.userTitle "Kevin" (Some " \t"))
    Assert.Equal("Kevin", UsersHelper.userTitle "Kevin" None)

// helpers/rooms.rs

[<Fact>]
let ``involvement cycles through its states`` () =
    Assert.Equal("everything", RoomsHelper.nextInvolvement false "mentions")
    Assert.Equal("mentions", RoomsHelper.nextInvolvement false "invisible")
    Assert.Equal("nothing", RoomsHelper.nextInvolvement true "everything")
    Assert.Equal("everything", RoomsHelper.nextInvolvement true "nothing")
    Assert.Equal("everything", RoomsHelper.nextInvolvement true "unknown")

// helpers/translations.rs

[<Fact>]
let ``every translation key has its popup and an unknown key is a bug`` () =
    let html = Render.text (fun w -> Translations.translationsFor w "password")
    Assert.StartsWith("<dl class=\"language-list\"><dt>🇺🇸</dt><dd class=\"margin-none\">Enter your password</dd>", html)
    Assert.Throws<exn>(fun () -> Render.plain (fun w -> Translations.translationsFor w "nope") |> ignore) |> ignore

// `str::to_lowercase`, which the room forms' `data-value` calls on a user's name: the answers are Rust's (the
// differential compares every code point and the sigma rules live; these are the cases that explain them).

[<Fact>]
let ``to_lowercase maps a capital sigma by where the word ends`` () =
    Assert.Equal("ας", Application.toLowercase "ΑΣ")
    Assert.Equal("ασα", Application.toLowercase "ΑΣΑ")
    Assert.Equal("σα", Application.toLowercase "ΣΑ")
    Assert.Equal("σ", Application.toLowercase "Σ")
    Assert.Equal("οδυσσευς", Application.toLowercase "ΟΔΥΣΣΕΥΣ")
    // a case-ignorable character (a combining mark, a soft hyphen, an apostrophe) between the letter and the sigma is skipped
    Assert.Equal("άς", Application.toLowercase "ΆΣ")
    Assert.Equal("α­ς", Application.toLowercase "Α­Σ")
    Assert.Equal("α'ς", Application.toLowercase "Α'Σ")
    // ... on either side: a cased letter after the ignorable characters that follow the sigma makes it medial
    Assert.Equal("ασ'α", Application.toLowercase "ΑΣ'Α")
    Assert.Equal("ας'", Application.toLowercase "ΑΣ'")
    // a space or digit is not ignorable, so nothing cased is before the sigma
    Assert.Equal("α σ", Application.toLowercase "Α Σ")
    Assert.Equal("1σα", Application.toLowercase "1ΣΑ")

[<Fact>]
let ``to_lowercase lowercases the way char::to_lowercase does`` () =
    Assert.Equal("i̇stanbul", Application.toLowercase "İstanbul")
    Assert.Equal("straße", Application.toLowercase "STRAßE")
    Assert.Equal("日本語 テキスト", Application.toLowercase "日本語 テキスト")
    Assert.Equal("😀 emoji 👍🏽", Application.toLowercase "😀 EMOJI 👍🏽")
