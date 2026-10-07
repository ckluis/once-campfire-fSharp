// Port of rust/crates/rails_compat/src/signed_id.rs
/// `ActiveRecord::SignedId` (`signed_id(purpose:, expires_in:)` / `find_signed`).
///
/// The verifier is `Rails.application.message_verifiers["active_record/signed_id"]` with the
/// legacy options prepended (`use_legacy_signed_id_verifier` defaults to `:generate_and_verify`):
/// it generates with SHA256, `::JSON`, URL-safe Base64, and falls back when reading to the
/// app-wide default (SHA1, `:json_allow_marshal`, strict Base64). The purpose is
/// `"<base class name underscored>/<purpose>"`, e.g. `user/avatar`, or just `user`.
module Campfire.RailsCompat.SignedId

open System
open System.Globalization

[<Literal>]
let Salt = "active_record/signed_id"

/// `String#underscore` for class names: `Rooms::Open` -> `rooms/open`, `WebPush` -> `web_push`.
let private underscore (name: string) : string =
    let chars = name.Replace("::", "/")
    let out = Text.StringBuilder()
    for i in 0 .. chars.Length - 1 do
        let c = chars[i]
        if c >= 'A' && c <= 'Z' then
            let previous = if i > 0 then Some chars[i - 1] else None
            let next = if i + 1 < chars.Length then Some chars[i + 1] else None
            let isLower (c: char) = c >= 'a' && c <= 'z'
            let isUpper (c: char) = c >= 'A' && c <= 'Z'
            let isDigit (c: char) = c >= '0' && c <= '9'
            let afterLowerOrDigit = previous |> Option.exists (fun p -> isLower p || isDigit p)
            let acronymEnd = previous |> Option.exists isUpper && next |> Option.exists isLower
            if afterLowerOrDigit || acronymEnd then out.Append '_' |> ignore
            out.Append(Char.ToLowerInvariant c) |> ignore
        else
            out.Append(if c = '-' then '_' else c) |> ignore
    out.ToString()

/// `combine_signed_id_purposes`: `[base_class.name.underscore, purpose.to_s].compact_blank.join("/")`.
let combinePurposes (modelName: string) (purpose: string option) : string =
    [ underscore modelName; defaultArg purpose "" ]
    |> List.filter (fun part -> not (String.IsNullOrWhiteSpace part))
    |> String.concat "/"

let verifier (secrets: Secrets) : MessageVerifier =
    let secret = secrets.KeyGenerator.SharedKey(Salt, 64)
    let fallback = MessageVerifier.create secret Digest.Sha1 Encoding.Strict (Serializer.JsonWithFallback true)
    MessageVerifier.create secret Digest.Sha256 Encoding.UrlSafe Serializer.Json
    |> MessageVerifier.fallBackTo fallback

/// `modelName` is the record's *base* class name, e.g. "User" or "Room" (not "Rooms::Open").
let private make (secrets: Secrets) (modelName: string) (id: int64) (purpose: string option) (expiresAt: Timestamp option) : string =
    MessageVerifier.generate (verifier secrets) (Value.Int id) (Some(combinePurposes modelName purpose)) expiresAt

let generate (secrets: Secrets) (modelName: string) (id: int64) (purpose: string option) (expiresAt: Timestamp option) : string =
    match expiresAt with
    // Without an expiry the token is a function of its inputs: an avatar token is the same string every time.
    | None ->
        let purposeName = defaultArg purpose ""
        match secrets.Tokens.TryGetSignedId(modelName, purposeName, id) with
        | null ->
            let token = make secrets modelName id purpose None
            secrets.Tokens.AddSignedId(modelName, purposeName, id, token)
            token
        | token -> token
    | Some _ -> make secrets modelName id purpose expiresAt

/// `find_signed`'s verification step: the id to look up, or `None`.
let verify (secrets: Secrets) (modelName: string) (signedId: string) (purpose: string option) (now: Timestamp) : int64 option =
    match MessageVerifier.verify (verifier secrets) signedId (Some(combinePurposes modelName purpose)) now with
    | Ok(Value.Int n) -> Some n
    // `find_by(id: "7")` casts the string.
    | Ok(Value.String s) ->
        match Int64.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
        | true, n -> Some n
        | _ -> None
    | _ -> None
