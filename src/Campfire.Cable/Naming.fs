// Port of rust/crates/cable/src/naming.rs
//
// Broadcasting names: `Channel::Naming#channel_name`, `Channel::Broadcasting#broadcasting_for` and
// Turbo's `stream_name_from`.
//
// Callers pass each streamable already converted with `to_gid_param` (records, see `gidParam`) or
// `to_param` (symbols and strings, which are themselves).
module Campfire.Cable.Naming

open System
open System.Text
open Campfire.RailsCompat

/// `GlobalID#to_param`: the URL-safe, unpadded Base64 of `gid://app/Model/id`. Records use their
/// STI class name, e.g. `Rooms::Open`.
let gidParam (gid: GlobalId) : string = GlobalId.toParam gid

/// `ActiveSupport::Inflector.underscore` without acronym inflections (Campfire defines none).
let private underscore (word: string) : string =
    let chars = word.Replace("::", "/")
    let out = StringBuilder(chars.Length + 4)
    for i in 0 .. chars.Length - 1 do
        let c = chars[i]
        if Char.IsAsciiLetterUpper c && i > 0 then
            let prev = chars[i - 1]
            let next = if i + 1 < chars.Length then ValueSome chars[i + 1] else ValueNone
            // ([a-z\d])([A-Z]) and ([A-Z\d]+)([A-Z][a-z])
            let lowerBefore = Char.IsAsciiLetterLower prev || Char.IsAsciiDigit prev
            let acronymEnd =
                (Char.IsAsciiLetterUpper prev || Char.IsAsciiDigit prev)
                && (match next with
                    | ValueSome n -> Char.IsAsciiLetterLower n
                    | ValueNone -> false)
            if lowerBefore || acronymEnd then out.Append '_' |> ignore
        out.Append(if c = '-' then '_' elif Char.IsAsciiLetterUpper c then char (int c + 32) else c) |> ignore
    out.ToString()

/// `ActionCable::Channel::Naming.channel_name`:
/// `name.delete_suffix("Channel").gsub("::", ":").underscore`.
let channelName (className: string) : string =
    let name = if className.EndsWith("Channel", StringComparison.Ordinal) then className.Substring(0, className.Length - 7) else className
    underscore (name.Replace("::", ":"))

/// `Channel::Broadcasting.broadcasting_for`: the channel name followed by each broadcastable,
/// joined with `:`. `stream_for @room` in `RoomChannel` streams from `room:<gid param>`.
let broadcastingFor (className: string) (broadcastables: string list) : string =
    String.Join(":", channelName className :: broadcastables)

/// `Turbo::Streams::StreamName#stream_name_from`: `turbo_stream_from @room, :messages` names the
/// stream `<room gid param>:messages`.
let streamNameFrom (streamables: string list) : string = String.Join(":", streamables)
