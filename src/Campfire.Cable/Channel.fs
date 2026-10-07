// Port of rust/crates/cable/src/channel.rs
//
// `ActionCable::Channel::Base`: one instance per subscription, driven by the connection.
namespace Campfire.Cable

open System
open System.Collections.Generic
open System.Text
open System.Threading.Tasks
open Campfire.RailsCompat
open Campfire.Cable.Socket

/// A decoded identifier or `perform` payload: a JSON object's entries, in order.
type Params = (string * Value) list

/// An exception escaping a channel callback. Rails logs it (`Subscriptions#execute_command`) and
/// sends nothing: a subscription whose `subscribed` raised is neither confirmed nor rejected, and
/// stays registered.
type ChannelError = { Message: string }

type ChannelResult<'T> = Result<'T, ChannelError>

/// What a subscription needs of its server: the hub it streams from and the ways to broadcast.
/// (Rust's `Subscription` holds the generic `Server<U>`, and `Server<U>` holds the channels that take
/// a `Subscription<U>`; an interface breaks that cycle for F#'s file order.)
type IServer =
    abstract Config: Config
    abstract Hub: Hub
    abstract Logger: Microsoft.Extensions.Logging.ILogger
    /// `ActionCable.server.broadcast(broadcasting, message)`.
    abstract Broadcast: broadcasting: string * message: Value -> int
    /// A broadcast of text (a Turbo Stream tag, say) sent as a JSON string, escaped straight into
    /// the broadcast's buffer.
    abstract BroadcastText: broadcasting: string * text: string -> int
    /// A broadcast of JSON the caller has encoded as Active Support does, as UTF-8.
    abstract BroadcastEncoded: broadcasting: string * json: ReadOnlySpan<byte> -> int

/// The per-subscription state a channel works with: params, the connection's identity, streams,
/// rejection and transmissions.
[<Sealed>]
type Subscription<'U>
    internal (server: IServer, className: string, identifier: string, encodedIdentifier: string, currentUser: 'U, wake: Wake) =
    let streams = List<Subscriber>()
    /// Streams started by the last callback, for the connection to start reading once that
    /// callback's own frames (transmissions, the confirmation) are queued ahead of them.
    let started = List<Subscriber>()
    let transmissions = List<string>()
    let mutable rejected = false
    let mutable unsubscribed = false

    /// The raw identifier string the client subscribed with.
    member _.Identifier: string = identifier

    /// The decoded identifier, including `channel`. Parsed when asked for (subscriptions live as
    /// long as their sockets, and are many).
    member _.Params() : Params =
        match Json.parse (Encoding.UTF8.GetBytes identifier) with
        | Some(Value.Object entries) -> entries
        | _ -> []

    member this.Param(key: string) : Value option =
        this.Params() |> List.tryFind (fun (k, _) -> k = key) |> Option.map snd

    /// `identified_by :current_user`.
    member _.CurrentUser: 'U = currentUser

    member _.Server: IServer = server

    member _.ChannelName() : string = Naming.channelName className

    /// `broadcasting_for` for this channel's class.
    member _.BroadcastingFor(broadcastables: string list) : string = Naming.broadcastingFor className broadcastables

    /// `broadcast_to` for this channel's class.
    member this.BroadcastTo(broadcastables: string list, message: Value) : unit =
        server.Broadcast(this.BroadcastingFor broadcastables, message) |> ignore

    member _.StreamFrom(broadcasting: string) : unit =
        if not unsubscribed then
            // The hub wraps each broadcast for this identifier once, for every subscriber sharing it.
            let subscriber = server.Hub.Subscribe(broadcasting, encodedIdentifier, wake)
            started.Add subscriber
            streams.Add subscriber

    member this.StreamFor(broadcastables: string list) : unit = this.StreamFrom(this.BroadcastingFor broadcastables)

    member _.StopStreamFrom(broadcasting: string) : unit =
        let mutable i = 0
        while i < streams.Count do
            if streams[i].Broadcasting = broadcasting then
                (streams[i] :> IDisposable).Dispose()
                streams.RemoveAt i
            else
                i <- i + 1

    member _.StopAllStreams() : unit =
        for stream in streams do
            (stream :> IDisposable).Dispose()
        streams.Clear()
        started.Clear()

    /// `stream_or_reject_for`.
    member this.StreamOrRejectFor(broadcastables: string list option) : unit =
        match broadcastables with
        | Some broadcastables -> this.StreamFor broadcastables
        | None -> this.Reject()

    member _.Streams: string list = [ for stream in streams -> stream.Broadcasting ]

    member _.Reject() : unit = rejected <- true

    /// `subscription_rejected?`
    member _.Rejected: bool = rejected

    /// Sends `{"identifier":...,"message":...}` to this subscriber only.
    member _.Transmit(message: Value) : unit =
        transmissions.Add(Protocol.message encodedIdentifier (Json.encode message))

    member internal _.Unsubscribed
        with get () = unsubscribed
        and set value = unsubscribed <- value

    member internal _.ClassName: string = className

    /// Moves the frames the last callback transmitted onto `target`.
    member internal _.DrainTransmissions(target: List<Frame>) : unit =
        for text in transmissions do
            target.Add(Frame.OfString text)
        transmissions.Clear()

    /// Moves the streams the last callback started onto `target`.
    member internal _.DrainStarted(target: List<Subscriber>) : unit =
        target.AddRange started
        started.Clear()

    interface IDisposable with
        member this.Dispose() = this.StopAllStreams()

/// A channel class. The server builds a fresh instance per subscription from the factory registered
/// under the Ruby class name (see `ServerBuilder.channel`); what state it keeps is in the closures.
///
/// Rails' `on_subscribe`/`on_unsubscribe` callbacks run after `subscribed`/`unsubscribed`, so put
/// them at the end of these functions (guarding on `Subscription.Rejected` where the Ruby does
/// `unless: :subscription_rejected?`).
///
/// An exception that escapes a callback is logged and treated as `Error`, as Rails rescues
/// whatever `execute_command` raises.
type Channel<'U> =
    { Subscribed: Subscription<'U> -> Task<ChannelResult<unit>>
      /// Also runs when a subscription is rejected, as in Rails (`reject_subscription` removes the
      /// subscription, which calls `unsubscribe_from_channel`).
      Unsubscribed: Subscription<'U> -> Task<ChannelResult<unit>>
      /// Dispatches a `perform` from the client. Return `Ok false` when `action` isn't one of the
      /// channel's public methods, which Rails logs as "Unable to process". `action` is
      /// `data["action"]`, or `"receive"` when that's blank.
      Perform: string -> Params -> Subscription<'U> -> Task<ChannelResult<bool>> }

module Channel =
    let private done' : Task<ChannelResult<unit>> = Task.FromResult(Ok())
    let private notPerformed: Task<ChannelResult<bool>> = Task.FromResult(Ok false)

    /// A channel with no callbacks: `ApplicationCable::Channel` itself and `HeartbeatChannel`
    /// subscribe, confirm and do nothing else. Build others with `{ Channel.empty with ... }`.
    let empty<'U> : Channel<'U> =
        { Subscribed = fun _ -> done'
          Unsubscribed = fun _ -> done'
          Perform = fun _ _ _ -> notPerformed }

    let ok: Task<ChannelResult<unit>> = done'

    let error (message: string) : ChannelError = { Message = message }
