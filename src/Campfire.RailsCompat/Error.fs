// Port of the `Error` enum in rust/crates/rails_compat/src/lib.rs
namespace Campfire.RailsCompat

type Error =
    /// Malformed message or bad signature (Ruby's `:invalid_message_format`).
    | InvalidSignature
    /// Authentic, but the payload doesn't deserialize (`:invalid_message_serialization`).
    | InvalidMessage
    | Expired
    /// Wrong purpose, or a purpose was expected and the message carries no metadata.
    | PurposeMismatch

module Error =
    /// `ActiveSupport::Messages::Rotator` only falls back to the next rotation on format and
    /// serialization errors. An expired or mismatched message stops at the first verifier.
    let internal rotates (error: Error) : bool =
        match error with
        | InvalidSignature
        | InvalidMessage -> true
        | Expired
        | PurposeMismatch -> false
