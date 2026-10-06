// Port of `Secrets` and `app_verifier` in rust/crates/rails_compat/src/lib.rs
namespace Campfire.RailsCompat

open System
open System.Collections.Concurrent
open System.Collections.Generic

/// The comparer of `TokenMemo`'s signed ids: hashed by id alone (the model and purpose are few, the ids are what vary).
[<Sealed>]
type internal SignedIdKeys() =
    interface IEqualityComparer<struct (string * string * int64)> with
        member _.Equals(struct (m1, p1, i1), struct (m2, p2, i2)) =
            i1 = i2 && String.Equals(m1, m2, StringComparison.Ordinal) && String.Equals(p1, p2, StringComparison.Ordinal)

        member _.GetHashCode(struct (_, _, id)) = id.GetHashCode()

/// Tokens that are a pure function of their inputs (a signed id with no expiry, a signed stream name), remembered once made: the page
/// paths sign the same few (an avatar per user, a stream per room) on every request. Bounded; a full table is emptied, not evicted.
[<Sealed>]
type TokenMemo() =
    static let capacity = 16384
    let signedIds = ConcurrentDictionary<struct (string * string * int64), string>(SignedIdKeys())
    let named = ConcurrentDictionary<string, string>(StringComparer.Ordinal)

    member _.TryGetSignedId(modelName: string, purpose: string, id: int64) : string | null =
        match signedIds.TryGetValue(struct (modelName, purpose, id)) with
        | true, token -> token
        | _ -> null

    member _.AddSignedId(modelName: string, purpose: string, id: int64, token: string) : unit =
        if signedIds.Count >= capacity then signedIds.Clear()
        signedIds[struct (modelName, purpose, id)] <- token

    member _.TryGetNamed(name: string) : string | null =
        match named.TryGetValue name with
        | true, token -> token
        | _ -> null

    member _.AddNamed(name: string, token: string) : unit =
        if named.Count >= capacity then named.Clear()
        named[name] <- token

/// Everything derived from `secret_key_base`, built once at boot and shared.
type Secrets =
    { KeyGenerator: KeyGenerator
      Tokens: TokenMemo }

module Secrets =
    let create (secretKeyBase: string) : Secrets =
        { KeyGenerator = KeyGenerator secretKeyBase
          Tokens = TokenMemo() }

module RailsCompat =
    /// `Rails.application.message_verifier(name)`: `message_verifiers[name]` with the app defaults
    /// (key `generate_key(name, 64)`, HMAC-SHA1, strict Base64, `:json_allow_marshal`, `_rails`
    /// envelope). Active Storage uses `appVerifier secrets "ActiveStorage"` for blob signed ids
    /// (purpose `"blob_id"`, *not* `signed_id`'s `model/purpose` scheme), variation keys, disk URLs
    /// and upload tokens. Use `generateRaw`/`verifyRaw` to control the JSON key order.
    let appVerifier (secrets: Secrets) (name: string) : MessageVerifier =
        MessageVerifier.create (secrets.KeyGenerator.SharedKey(name, 64)) Digest.Sha1 Encoding.Strict (Serializer.JsonWithFallback true)
