// Port of `Secrets` and `app_verifier` in rust/crates/rails_compat/src/lib.rs
namespace Campfire.RailsCompat

/// Everything derived from `secret_key_base`, built once at boot and shared.
type Secrets = { KeyGenerator: KeyGenerator }

module Secrets =
    let create (secretKeyBase: string) : Secrets =
        { KeyGenerator = KeyGenerator secretKeyBase }

module RailsCompat =
    /// `Rails.application.message_verifier(name)`: `message_verifiers[name]` with the app defaults
    /// (key `generate_key(name, 64)`, HMAC-SHA1, strict Base64, `:json_allow_marshal`, `_rails`
    /// envelope). Active Storage uses `appVerifier secrets "ActiveStorage"` for blob signed ids
    /// (purpose `"blob_id"`, *not* `signed_id`'s `model/purpose` scheme), variation keys, disk URLs
    /// and upload tokens. Use `generateRaw`/`verifyRaw` to control the JSON key order.
    let appVerifier (secrets: Secrets) (name: string) : MessageVerifier =
        MessageVerifier.create (secrets.KeyGenerator.GenerateKey(name, 64)) Digest.Sha1 Encoding.Strict (Serializer.JsonWithFallback true)
