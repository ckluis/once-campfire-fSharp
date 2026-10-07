// Port of rust/crates/cable/src/protocol.rs
//
// Action Cable wire format (`ActionCable::INTERNAL` in actioncable/lib/action_cable.rb).
//
// Frames are Ruby hashes run through `ActiveSupport::JSON.encode`, so key order is the order the
// hash literal is written in `Connection::Base` and `Channel::Base`.
namespace Campfire.Cable

open System.Globalization
open Campfire.RailsCompat

/// `ActionCable::INTERNAL[:disconnect_reasons]`.
type DisconnectReason =
    | Unauthorized
    | InvalidRequest
    | ServerRestart
    | Remote

module Protocol =
    /// Offered in `Sec-WebSocket-Protocol`; the first one the client lists that's in here wins
    /// (websocket-driver's hybi negotiation walks the client's list, not the server's).
    let Protocols = [| "actioncable-v1-json"; "actioncable-unsupported" |]

    [<Literal>]
    let DefaultMountPath = "/cable"

    /// `ActionCable::Server::Connections::BEAT_INTERVAL`, in seconds.
    [<Literal>]
    let BeatInterval = 3

    let reasonToString (reason: DisconnectReason) : string =
        match reason with
        | Unauthorized -> "unauthorized"
        | InvalidRequest -> "invalid_request"
        | ServerRestart -> "server_restart"
        | Remote -> "remote"

    /// `{"type":"welcome"}`
    let welcome () : string = """{"type":"welcome"}"""

    /// `{"type":"ping","message":<unix seconds>}` (`Connection::Base#beat`).
    let ping (unixSeconds: int64) : string =
        "{\"type\":\"ping\",\"message\":" + unixSeconds.ToString(CultureInfo.InvariantCulture) + "}"

    /// `{"type":"disconnect","reason":...,"reconnect":...}` (`Connection::Base#close`). `reason` is
    /// `nil` when Rails calls `close` without one, and `reconnect` is whatever JSON value the remote
    /// disconnect carried (it's `message.fetch("reconnect", true)`, unvalidated).
    let disconnect (reason: DisconnectReason option) (reconnect: Value) : string =
        let reason =
            match reason with
            | Some r -> "\"" + reasonToString r + "\""
            | None -> "null"
        "{\"type\":\"disconnect\",\"reason\":" + reason + ",\"reconnect\":" + Json.encode reconnect + "}"

    /// `{"identifier":...,"type":"confirm_subscription"}`
    let confirmation (identifier: string) : string =
        "{\"identifier\":" + Json.encode (Value.String identifier) + ",\"type\":\"confirm_subscription\"}"

    /// `{"identifier":...,"type":"reject_subscription"}`
    let rejection (identifier: string) : string =
        "{\"identifier\":" + Json.encode (Value.String identifier) + ",\"type\":\"reject_subscription\"}"

    /// What comes before the message in `{"identifier":...,"message":...}`.
    let messagePrefix (encodedIdentifier: string) : string =
        "{\"identifier\":" + encodedIdentifier + ",\"message\":"

    /// `{"identifier":...,"message":...}` where `encodedMessage` is already Active Support JSON.
    /// Rails decodes each broadcast and re-encodes it inside this hash; passing the encoded payload
    /// through is equivalent and saves the round trip per subscriber.
    let message (encodedIdentifier: string) (encodedMessage: string) : string =
        messagePrefix encodedIdentifier + encodedMessage + "}"
