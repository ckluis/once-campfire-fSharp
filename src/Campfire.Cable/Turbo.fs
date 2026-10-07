// Port of rust/crates/cable/src/turbo.rs
//
// turbo-rails: `Turbo::StreamsChannel`, the `<turbo-stream>` tags its broadcasts carry
// (`Turbo::Streams::ActionHelper#turbo_stream_action_tag`) and the `broadcast_*_to` helpers
// (`Turbo::Streams::Broadcasts`).
module Campfire.Cable.Turbo

open System
open System.Text
open System.Threading.Tasks
open Campfire.RailsCompat
open Campfire.Ruby

[<Literal>]
let StreamsChannelName = "Turbo::StreamsChannel"

/// `Turbo::StreamsChannel`, optionally with a guard prepended the way Campfire prepends
/// `RoomStreamsAreAuthorized` (reference/app/channels/concerns/room_streams_are_authorized.rb).
type StreamsChannel =
    { Verifier: string -> string option
      Guard: (string -> bool) option }

/// `params[:signed_stream_name]` verified with `verifier`. A missing or `null` name is simply
/// unverified; any other non-string makes `MessageVerifier#verified` raise.
let verifiedStreamNameFromParams (parameters: Params) (verifier: string -> string option) : ChannelResult<string option> =
    match parameters |> List.tryFind (fun (k, _) -> k = "signed_stream_name") |> Option.map snd with
    | None
    | Some Value.Null -> Ok None
    | Some(Value.String signed) -> Ok(verifier signed)
    | Some other -> Error { Message = $"undefined method 'valid_encoding?' for {Json.generate other}" }

module StreamsChannel =
    let withVerifier (verifier: string -> string option) : StreamsChannel = { Verifier = verifier; Guard = None }

    /// Verifies signed stream names with `Turbo.signed_stream_verifier`.
    let create (secrets: Secrets) : StreamsChannel =
        withVerifier (fun signed -> Turbo.verifiedStreamName secrets signed)

    /// Rejects any subscription whose verified stream name `guarded` returns true for, before the
    /// stock behavior runs. The guard also sees names that failed verification, as `""` (Ruby's
    /// `nil.to_s`).
    let guardedBy (guarded: string -> bool) (channel: StreamsChannel) : StreamsChannel = { channel with Guard = Some guarded }

    /// `verified_stream_name_from_params`, for channels (like `RoomMessagesChannel`) that take
    /// signed stream names too.
    let verifiedStreamNameFromParams (channel: StreamsChannel) (parameters: Params) : ChannelResult<string option> =
        verifiedStreamNameFromParams parameters channel.Verifier

    /// The channel class a subscription gets.
    let channel<'U> (streams: StreamsChannel) : Channel<'U> =
        { Channel.empty with
            Subscribed =
                fun sub ->
                    match verifiedStreamNameFromParams streams (sub.Params()) with
                    | Error error -> Task.FromResult(Error error)
                    | Ok streamName ->
                        let guarded =
                            match streams.Guard with
                            | Some guard -> guard (defaultArg streamName "")
                            | None -> false
                        if guarded then
                            sub.Reject()
                        else
                            match streamName with
                            | Some streamName -> sub.StreamFrom streamName
                            | None -> sub.Reject()
                        Channel.ok }

/// Turbo Stream actions.
type Action =
    | Append
    | Prepend
    | Replace
    | Update
    | Remove
    | Before
    | After
    | Refresh

module Action =
    let asString (action: Action) : string =
        match action with
        | Append -> "append"
        | Prepend -> "prepend"
        | Replace -> "replace"
        | Update -> "update"
        | Remove -> "remove"
        | Before -> "before"
        | After -> "after"
        | Refresh -> "refresh"

/// Where the action applies. Records are passed as their `dom_id` (`dom_id(@room, :list)`), and
/// `targets` as a CSS selector (records become `#<dom_id>`).
type Target =
    | NoTarget
    | Target of string
    | Targets of string

/// What goes between `<template>` and `</template>`, already safe HTML.
type Template =
    | NoTemplate
    | Html of string
    /// HTML as a view renders it, UTF-8.
    | HtmlUtf8 of ReadOnlyMemory<byte>

/// Writes a tag in pieces, each escaped for wherever it is going (a string for `actionTag`, a JSON
/// string for a broadcast).
type private TagSink =
    abstract Raw: string -> unit
    abstract Html: Template -> unit

let private htmlSpecials = System.Buffers.SearchValues.Create "&<>\"'"

/// An attribute value as `ERB::Util.unwrapped_html_escape` writes it (the escaping is `Campfire.Ruby`'s;
/// most values have nothing to escape and are used as they are).
let private escapedAttribute (value: string) : string =
    if value.AsSpan().IndexOfAny htmlSpecials < 0 then value else Erb.htmlEscape value

let private pushAttribute (sink: TagSink) (name: string) (value: string) : unit =
    sink.Raw " "
    sink.Raw name
    sink.Raw "=\""
    sink.Raw(escapedAttribute value)
    sink.Raw "\""

let private writeActionTag (sink: TagSink) (action: Action) (target: Target) (template: Template) (attributes: (string * string option) list) =
    sink.Raw "<turbo-stream"
    for (name, value) in attributes do
        match value with
        | Some value -> pushAttribute sink name value
        | None -> ()
    pushAttribute sink "action" (Action.asString action)
    match target with
    | Target target -> pushAttribute sink "target" target
    | Targets targets -> pushAttribute sink "targets" targets
    | NoTarget -> ()
    sink.Raw ">"
    match action with
    | Remove
    | Refresh -> ()
    | _ ->
        sink.Raw "<template>"
        sink.Html template
        sink.Raw "</template>"
    sink.Raw "</turbo-stream>"

/// `turbo_stream_action_tag(action, target:, targets:, template:, **attributes)`.
///
/// Extra attributes come first, then `action`, then `target`/`targets`, exactly as the tag helper's
/// hash is built. Attribute names are used as given (Rails only dasherizes the tag name, so
/// `maintain_scroll: true` renders `maintain_scroll="true"`), and a `None` value omits the
/// attribute. `template` is already-safe HTML; `remove` and `refresh` never carry one.
let actionTag (action: Action) (target: Target) (template: string option) (attributes: (string * string option) list) : string =
    let tag = StringBuilder()
    let sink =
        { new TagSink with
            member _.Raw text = tag.Append text |> ignore
            member _.Html template =
                match template with
                | Html html -> tag.Append html |> ignore
                | HtmlUtf8 html -> tag.Append(Encoding.UTF8.GetString html.Span) |> ignore
                | NoTemplate -> () }
    writeActionTag sink action target (match template with Some html -> Html html | None -> NoTemplate) attributes
    tag.ToString()

/// `turbo_stream_refresh_tag(request_id:)`.
let refreshTag (requestId: string option) : string =
    actionTag Refresh NoTarget None [ "request-id", requestId |> Option.filter (fun id -> id <> "") ]

// `Turbo::StreamsChannel.broadcast_*_to`. Streamables are the stream name parts (GID params and
// symbols); blank ones are dropped and nothing is sent if none remain, as in `broadcast_stream_to`.

/// The stream name of `streamables` that aren't blank, if any are left.
let private streamName (streamables: string list) : string option =
    match streamables |> List.filter (fun s -> s.Trim() <> "") with
    | [] -> None
    | remaining -> Some(Naming.streamNameFrom remaining)

let broadcastStreamTo (server: IServer) (streamables: string list) (content: string) : int =
    match streamName streamables with
    | Some name -> server.BroadcastText(name, content)
    | None -> 0

/// The tag as `actionTag` writes it, escaped as a JSON string as it is written, so that neither
/// the tag nor the HTML inside it is ever a string of its own.
let broadcastActionTo
    (server: IServer)
    (streamables: string list)
    (action: Action)
    (target: Target)
    (template: Template)
    (attributes: (string * string option) list)
    : int =
    match streamName streamables with
    | None -> 0
    | Some name ->
        use json = new JsonStringWriter(256)
        json.Begin()
        let sink =
            { new TagSink with
                member _.Raw text = json.Append text
                member _.Html template =
                    match template with
                    | Html html -> json.Append html
                    | HtmlUtf8 html -> json.AppendUtf8 html.Span
                    | NoTemplate -> () }
        writeActionTag sink action target template attributes
        json.End()
        server.BroadcastEncoded(name, json.Span)

let broadcastAppendTo (server: IServer) (streamables: string list) (target: string) (html: string) : int =
    broadcastActionTo server streamables Append (Target target) (Html html) []

let broadcastPrependTo (server: IServer) (streamables: string list) (target: string) (html: string) : int =
    broadcastActionTo server streamables Prepend (Target target) (Html html) []

let broadcastReplaceTo (server: IServer) (streamables: string list) (target: string) (html: string) : int =
    broadcastActionTo server streamables Replace (Target target) (Html html) []

let broadcastUpdateTo (server: IServer) (streamables: string list) (target: string) (html: string) : int =
    broadcastActionTo server streamables Update (Target target) (Html html) []

let broadcastRemoveTo (server: IServer) (streamables: string list) (target: string) : int =
    broadcastActionTo server streamables Remove (Target target) NoTemplate []

let broadcastRefreshTo (server: IServer) (streamables: string list) (requestId: string option) : int =
    broadcastActionTo server streamables Refresh NoTarget NoTemplate [ "request-id", requestId |> Option.filter (fun id -> id <> "") ]
