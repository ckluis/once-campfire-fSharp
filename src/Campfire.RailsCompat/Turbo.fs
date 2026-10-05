// Port of rust/crates/rails_compat/src/turbo.rs
/// `Turbo::StreamsChannel.signed_stream_name` / `verified_stream_name` (turbo-rails
/// `app/channels/turbo/streams/stream_name.rb`). The verifier is
/// `MessageVerifier.new(generate_key("turbo/signed_stream_verifier_key"), digest: "SHA256",
/// serializer: JSON)`: strict Base64 of the JSON-dumped stream name, no metadata envelope.
module Campfire.RailsCompat.Turbo

open System.Globalization

[<Literal>]
let Salt = "turbo/signed_stream_verifier_key"

let verifier (secrets: Secrets) : MessageVerifier =
    MessageVerifier.create (secrets.KeyGenerator.GenerateKey(Salt, 64)) Digest.Sha256 Encoding.Strict Serializer.Json

/// `streamables` are already-resolved stream name parts, joined with `:` like `stream_name_from`:
/// a record becomes its GID param (`GlobalId.toParam`), a symbol or string itself. So
/// `turbo_stream_from @room, :messages` is `[roomGid.toParam(); "messages"]`.
let signedStreamName (secrets: Secrets) (streamables: string list) : string =
    MessageVerifier.generate (verifier secrets) (Value.String(String.concat ":" streamables)) None None

/// The stream name, or `None` if the signature doesn't check out. (A validly signed non-string
/// can't come from `signedStreamName`; numbers are returned as their `to_s` like Rails would.)
let verifiedStreamName (secrets: Secrets) (signed: string) : string option =
    match MessageVerifier.verify (verifier secrets) signed None Timestamps.unixEpoch with
    | Ok(Value.String name) -> Some name
    | Ok(Value.Int n) -> Some(n.ToString(CultureInfo.InvariantCulture))
    | Ok(Value.UInt n) -> Some(n.ToString(CultureInfo.InvariantCulture))
    | Ok(Value.Float _ as n) -> Some(Json.generate n)
    | _ -> None
