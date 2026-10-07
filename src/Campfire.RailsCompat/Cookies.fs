// Port of rust/crates/rails_compat/src/cookies.rs
//
// `cookies.signed[...]` / `cookies.encrypted[...]` values (`action_dispatch/middleware/cookies.rb`),
// plus Rack's escaping of cookie values on the wire. Attributes (path, expires, HttpOnly,
// SameSite) are the HTTP layer's job.
//
// The functions here work on the *raw* jar value. On the wire Rack escapes it with
// `URI.encode_www_form_component` (`escape`) and unescapes incoming cookies (`unescape`).
//
// How the jars work, per Rails main:
// - the value is first dumped with `cookies_serializer` (`:json` here, so `ActiveSupport::JSON`),
// - then signed (HMAC-**SHA1**, key `generate_key("signed cookie")`) or encrypted (aes-256-gcm,
//   key `generate_key("authenticated encrypted cookie", 32)`) with the legacy metadata envelope
//   carrying `pur: "cookie.<name>"` and `exp` (ISO 8601 with milliseconds, or `null`),
// - reading tries purpose `cookie.<name>` first, then *no purpose*, so a value signed without
//   metadata (pre-Rails 5.2) is accepted under any cookie name.
module Campfire.RailsCompat.Cookies

open System

[<Literal>]
let SignedCookieSalt = "signed cookie"

[<Literal>]
let AuthenticatedEncryptedCookieSalt = "authenticated encrypted cookie"

/// `cookies.permanent`: expires 20 years from now (calendar years, like `20.years.from_now`).
let permanentExpiresAt (now: Timestamp) : Timestamp = now.ToUniversalTime().AddYears 20

let private purpose (name: string) : string = "cookie." + name

let signedCookieVerifier (secrets: Secrets) : MessageVerifier =
    // `signed_cookie_digest` is unset, so the jar falls back to "SHA1" even though the key itself
    // is derived with PBKDF2-SHA256.
    let secret = secrets.KeyGenerator.SharedKey(SignedCookieSalt, 64)
    MessageVerifier.create secret Digest.Sha1 Encoding.Strict Serializer.Null

let encryptedCookieEncryptor (secrets: Secrets) : MessageEncryptor =
    let secret = secrets.KeyGenerator.SharedKey(AuthenticatedEncryptedCookieSalt, 32)
    MessageEncryptor.createShared secret Serializer.Null

/// `SerializerWithFallback[:json].load`: Marshal payloads aren't allowed for cookies.
let private load (dumped: Value) : Value option =
    match dumped with
    | Value.String dumped ->
        match Serializer.load (Serializer.JsonWithFallback false) (System.Text.Encoding.UTF8.GetBytes dumped) with
        | Ok value -> Some value
        | Error _ -> None
    | _ -> None

/// The raw value for `cookies.signed[name] = { value:, expires: expires_at }`.
/// `cookies.signed.permanent[...]` is `expires_at: Some(permanentExpiresAt now)`.
let sign (secrets: Secrets) (name: string) (value: string) (expiresAt: Timestamp option) : string =
    let dumped = Json.encode (Value.String value)
    MessageVerifier.generate (signedCookieVerifier secrets) (Value.String dumped) (Some(purpose name)) expiresAt

/// Reads `cookies.signed[name]` as whatever JSON value it holds.
let verifySignedValue (secrets: Secrets) (name: string) (raw: string) (now: Timestamp) : Value option =
    let verifier = signedCookieVerifier secrets
    let dumped =
        match MessageVerifier.verify verifier raw (Some(purpose name)) now with
        | Ok dumped -> Ok dumped
        | Error _ -> MessageVerifier.verify verifier raw None now
    match dumped with
    | Ok dumped -> load dumped
    | Error _ -> None

/// Reads `cookies.signed[name]`; `None` wherever Rails returns nil. A value that is valid JSON
/// but not a string (Rails would return it) is also `None`.
let verifySigned (secrets: Secrets) (name: string) (raw: string) (now: Timestamp) : string option =
    match verifySignedValue secrets name raw now with
    | Some(Value.String s) -> Some s
    | _ -> None

/// The raw value for `cookies.encrypted[name] = { value:, expires: expires_at }`.
/// The session store writes `_campfire_session` this way with a 20-year `expire_after`.
let encrypt (secrets: Secrets) (name: string) (value: Value) (expiresAt: Timestamp option) : string =
    let dumped = Json.encode value
    MessageEncryptor.encryptAndSign (encryptedCookieEncryptor secrets) (Value.String dumped) (Some(purpose name)) expiresAt

/// Reads `cookies.encrypted[name]`; `None` wherever Rails returns nil.
let decrypt (secrets: Secrets) (name: string) (raw: string) (now: Timestamp) : Value option =
    let encryptor = encryptedCookieEncryptor secrets
    let dumped =
        match MessageEncryptor.decryptAndVerify encryptor raw (Some(purpose name)) now with
        | Ok dumped -> Ok dumped
        | Error _ -> MessageEncryptor.decryptAndVerify encryptor raw None now
    match dumped with
    | Ok dumped -> load dumped
    | Error _ -> None

let private hex = "0123456789ABCDEF"

/// `Rack::Utils.escape` (`URI.encode_www_form_component`), which Rack applies to every cookie
/// value it writes: `*-._` and alphanumerics stay, a space becomes `+`, the rest is `%XX`. That's
/// the form encoding the `form_urlencoded` crate writes.
let escape (raw: string) : string =
    let bytes = System.Text.Encoding.UTF8.GetBytes raw
    let out = System.Text.StringBuilder(bytes.Length)
    for b in bytes do
        let c = char b
        if (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '*' || c = '-' || c = '.' || c = '_' then
            out.Append c |> ignore
        elif c = ' ' then
            out.Append '+' |> ignore
        else
            out.Append('%').Append(hex[int (b >>> 4)]).Append(hex[int (b &&& 0xfuy)]) |> ignore
    out.ToString()

let private hexValue (b: byte) : int =
    if b >= byte '0' && b <= byte '9' then int b - int '0'
    elif b >= byte 'a' && b <= byte 'f' then int b - int 'a' + 10
    elif b >= byte 'A' && b <= byte 'F' then int b - int 'A' + 10
    else -1

/// `Rack::Utils.parse_cookies_header`'s `unescape(value) rescue value`: `+` is a space, `%XX` is
/// decoded, and a malformed escape leaves the value untouched.
let unescape (wire: string) : string =
    let bytes = System.Text.Encoding.UTF8.GetBytes wire
    let out = ResizeArray<byte>(bytes.Length)
    let mutable i = 0
    let mutable malformed = false
    while not malformed && i < bytes.Length do
        match bytes[i] with
        | b when b = byte '+' -> out.Add(byte ' ')
        | b when b = byte '%' ->
            if i + 2 < bytes.Length && hexValue bytes[i + 1] >= 0 && hexValue bytes[i + 2] >= 0 then
                out.Add(byte (hexValue bytes[i + 1] * 16 + hexValue bytes[i + 2]))
                i <- i + 2
            else
                malformed <- true
        | b -> out.Add b
        i <- i + 1
    if malformed then wire else System.Text.Encoding.UTF8.GetString(out.ToArray())
