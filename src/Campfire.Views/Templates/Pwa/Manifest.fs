// Port of rust/crates/views/templates/pwa/manifest.json (reference/app/views/pwa/manifest.json.erb)
/// `pwa/manifest.json.erb`. ERB HTML-escapes the values into the JSON, so an account named `a\b` or `"a"` made the
/// manifest invalid and the logo URL came out as `?size=small&amp;v=...`; the values are JSON strings here
/// (README, Known differences).
module Campfire.Views.Templates.Pwa.Manifest

open System
open Campfire.Views

let private t0 = Utf8.lit "{\n  \"name\": "
let private t1 = Utf8.lit ",\n  \"icons\": [\n    {\n      \"src\": "
let private t2 = Utf8.lit ",\n      \"type\": \"image/png\",\n      \"sizes\": \"192x192\"\n    },\n    {\n      \"src\": "
let private t3 = Utf8.lit ",\n      \"type\": \"image/png\",\n      \"sizes\": \"512x512\"\n    },\n    {\n      \"src\": "
let private t4 = Utf8.lit ",\n      \"type\": \"image/png\",\n      \"sizes\": \"512x512\",\n      \"purpose\": \"maskable\"\n    }\n  ],\n  \"start_url\": \"/\",\n  \"display\": \"standalone\",\n  \"scope\": \"/\",\n  \"description\": \"A chat app from the makers of Basecamp and HEY.\",\n  \"categories\": [\"social\", \"business\", \"productivity\"],\n  \"theme_color\": \"#ffffff\",\n  \"background_color\": \"#ffffff\",\n  \"shortcuts\": [\n    {\n      \"name\": \"New chat room\",\n      \"description\": \"Open Campfire and start a new chat room\",\n      \"url\": \"rooms/opens/new\",\n      \"icons\": [{ \"src\": "
let private t5 = Utf8.lit ", \"sizes\": \"any\" }]\n    },\n    {\n      \"name\": \"My profile\",\n      \"description\": \"Open Campfire and view your profile\",\n      \"url\": \"/users/me/profile\",\n      \"icons\": [{ \"src\": "
let private t6 = Utf8.lit ", \"sizes\": \"any\" }]\n    }\n  ],\n  \"screenshots\": [\n    {\n      \"src\": "
let private t7 = Utf8.lit ",\n      \"sizes\": \"1080x2400\",\n      \"form_factor\": \"narrow\",\n      \"label\": \"Campfire is an installable, self-hosted group chat system.\"\n    },\n    {\n      \"src\": "
let private t8 = Utf8.lit ",\n      \"sizes\": \"1080x2400\",\n      \"form_factor\": \"narrow\",\n      \"label\": \"Easily invite people. Make rooms. @mentions, DMs, and mobile support.\"\n    },\n    {\n      \"src\": "
let private t9 = Utf8.lit ",\n      \"sizes\": \"1080x2400\",\n      \"form_factor\": \"narrow\",\n      \"label\": \"Full support for dark mode, customizable to your brand.\"\n    }\n  ]\n}\n"

let private hexDigits = "0123456789abcdef"

/// `serde_json::to_string(value)` of a string, written straight to `w`: quote, backslash and control characters are
/// escaped (lowercase hex), everything else, `/` and non-ASCII included, is copied.
let private writeJsonString (w: Out) (value: string) : unit =
    w.Byte(byte '"')
    let mutable start = 0
    for at in 0 .. value.Length - 1 do
        let c = value[at]
        if c = '"' || c = '\\' || c < ' ' then
            if at > start then w.Raw(value.AsSpan(start, at - start))
            start <- at + 1
            match c with
            | '"' -> w.Raw "\\\""
            | '\\' -> w.Raw "\\\\"
            | '\b' -> w.Raw "\\b"
            | '\012' -> w.Raw "\\f"
            | '\n' -> w.Raw "\\n"
            | '\r' -> w.Raw "\\r"
            | '\t' -> w.Raw "\\t"
            | c ->
                w.Raw "\\u00"
                w.Byte(byte hexDigits[int c >>> 4])
                w.Byte(byte hexDigits[int c &&& 0xf])
    if value.Length > start then w.Raw(value.AsSpan(start, value.Length - start))
    w.Byte(byte '"')

/// `accountName` is `Current.account&.name`, `logoPathSmall` `fresh_account_logo_path(size: :small)`, `logoPath`
/// `fresh_account_logo_path`, `baseUrl` `request.base_url` (for `image_url`) and `assetPath` the asset resolver.
let render (w: Out) (accountName: string option) (logoPathSmall: string) (logoPath: string) (baseUrl: string) (assetPath: string -> string) : unit =
    let imageUrl (source: string) = writeJsonString w (baseUrl + assetPath source)
    w.Lit t0
    writeJsonString w (defaultArg accountName "Campfire")
    w.Lit t1
    writeJsonString w logoPathSmall
    w.Lit t2
    writeJsonString w logoPath
    w.Lit t3
    writeJsonString w logoPath
    w.Lit t4
    imageUrl "add.svg"
    w.Lit t5
    imageUrl "person.svg"
    w.Lit t6
    imageUrl "screenshots/android-chat.png"
    w.Lit t7
    imageUrl "screenshots/android-sidebar.png"
    w.Lit t8
    imageUrl "screenshots/android-dark-mode.png"
    w.Lit t9
