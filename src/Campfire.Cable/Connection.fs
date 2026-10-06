// Port of rust/crates/cable/src/connection.rs
//
// `ActionCable::Connection::Base` and `Connection::Subscriptions`: one task per socket.
//
// Commands are handled one at a time in arrival order. The connection reads its streams straight from
// the hub's per-broadcasting rings, so there's no per-connection queue: a client that stops reading
// falls behind by the ring's capacity, which closes the connection with `reconnect: true`. Frames that
// are ready together go out in one socket write.
//
// Where the Rust connection selects over its sockets and streams, this one awaits a single `Wake` that
// the reader, every stream it reads, the heartbeat and a restart all set, and then looks at each in
// turn. The socket's read half lives in a task of its own that hands incoming messages over in order,
// so it's only polled when the socket is readable, not every time a delivery wakes the connection.
module internal Campfire.Cable.Connection

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Campfire.RailsCompat
open Campfire.Cable.Socket

type private Entry<'U> = { Channel: Channel<'U>; Sub: Subscription<'U> }

/// Why the socket is going away, once we've decided to close it.
type private Closing =
    { Reason: DisconnectReason option
      Reconnect: Value }

/// A stream fell behind (`reason: nil`, as `Connection::Base#close` without one).
let private lagged = { Reason = None; Reconnect = Value.Bool true }

/// The most subscriptions one connection may hold, and the longest identifier it may subscribe with.
[<Literal>]
let private MaxSubscriptions = 64

[<Literal>]
let private MaxIdentifierBytes = 4096

/// Incoming messages buffered between the reader task and the connection. A client that sends
/// commands faster than they're handled is held back by TCP once this fills.
[<Literal>]
let private IncomingCapacity = 16

type private Received = Result<Incoming, ReadError>

let private tryRead (reader: Channels.ChannelReader<'T>) : 'T voption =
    let mutable item = Unchecked.defaultof<'T>
    if reader.TryRead(&item) then ValueSome item else ValueNone

/// Runs `callback`, treating an exception as the `Error` Rust's `ChannelError` is.
let private safely (callback: unit -> Task<ChannelResult<'T>>) : Task<ChannelResult<'T>> =
    task {
        try
            return! callback ()
        with e ->
            return Error { Message = e.Message }
    }

/// Whether `work` completed without an I/O failure: a failed socket write ends the connection.
let private attempt (work: Task) : Task<bool> =
    task {
        try
            do! work
            return true
        with _ ->
            return false
    }

type private Drained =
    | Drained = 0
    /// The batch is full; more may be waiting.
    | Full = 1
    | Lagged = 2

/// Reads the ready frames of every stream onto `pending`, up to `limit` of them in all.
let private drain (readers: List<Subscriber>) (pending: List<Frame>) (limit: int) : Drained =
    let mutable outcome = Drained.Drained
    let mutable i = 0
    while outcome = Drained.Drained && i < readers.Count do
        let reader = readers[i]
        if reader.Stopped then
            readers.RemoveAt i
        else
            let mutable reading = true
            while reading do
                let mutable frame = Unchecked.defaultof<Frame>
                match reader.TryRecv(&frame) with
                | RecvStatus.Got ->
                    pending.Add frame
                    if pending.Count >= limit then
                        outcome <- Drained.Full
                        reading <- false
                | RecvStatus.Lagged ->
                    outcome <- Drained.Lagged
                    reading <- false
                | _ -> reading <- false
            i <- i + 1
    outcome

/// `InternalChannel#process_internal_message`.
let private processInternalMessage (frame: Frame) : Closing voption =
    match Json.parse frame.Bytes with
    | Some(Value.Object _ as message) when message.TryGet "type" = Some(Value.String "disconnect") ->
        ValueSome
            { Reason = Some Remote
              Reconnect = defaultArg (message.TryGet "reconnect") (Value.Bool true) }
    | _ -> ValueNone

/// Reads the internal channel's ready messages, up to the first that disconnects this connection.
let private drainInternal (internal': Subscriber) : Closing voption =
    let mutable closing = ValueNone
    let mutable frame = Unchecked.defaultof<Frame>
    let mutable reading = true
    while reading do
        match internal'.TryRecv(&frame) with
        | RecvStatus.Got ->
            match processInternalMessage frame with
            | ValueSome remote ->
                closing <- ValueSome remote
                reading <- false
            | ValueNone -> ()
        | _ -> reading <- false
    closing

let private tryGet (key: string) (data: Params) : Value option = data |> List.tryFind (fun (k, _) -> k = key) |> Option.map snd

[<Sealed>]
type private Connection<'U>(server: Server<'U>, user: 'U, wake: Wake) =
    let logger = server.Logger
    /// Keyed by the raw identifier string, in subscription order (a Ruby hash).
    let subscriptions = List<Entry<'U>>()

    let position (identifier: string) : int =
        subscriptions.FindIndex(fun entry -> entry.Sub.Identifier = identifier)

    let find (data: Params) : int =
        match tryGet "identifier" data with
        | Some(Value.String identifier) -> position identifier
        | _ -> -1

    /// Frames waiting for the next socket write.
    member val Pending = List<Frame>()

    /// Streams the last command's callbacks started, to read from once its frames are queued.
    member val Started = List<Subscriber>()

    /// `Subscriptions#remove_subscription` -> `Channel::Base#unsubscribe_from_channel`.
    member private this.RemoveSubscription(index: int) : Task =
        task {
            let entry = subscriptions[index]
            subscriptions.RemoveAt index
            entry.Sub.Unsubscribed <- true
            match! safely (fun () -> entry.Channel.Unsubscribed entry.Sub) with
            | Error error -> logger.LogError("Could not execute command: {Error}", error.Message)
            | Ok() -> ()
            entry.Sub.StopAllStreams()
            entry.Sub.DrainTransmissions this.Pending
            entry.Sub.DrainStarted this.Started
        }

    /// `Channel::Base#subscribe_to_channel`.
    member private this.SubscribeToChannel(identifier: string) : Task =
        task {
            let index = position identifier
            let entry = subscriptions[index]
            let! result = safely (fun () -> entry.Channel.Subscribed entry.Sub)
            entry.Sub.DrainTransmissions this.Pending
            entry.Sub.DrainStarted this.Started
            match result with
            | Error error -> logger.LogError("Could not execute command {Identifier}: {Error}", identifier, error.Message)
            | Ok() ->
                if entry.Sub.Rejected then
                    do! this.RemoveSubscription index
                    this.Pending.Add(Frame.OfString(Protocol.rejection identifier))
                else
                    this.Pending.Add(Frame.OfString(Protocol.confirmation identifier))
        }

    /// `Subscriptions#add`. A repeated identifier (byte for byte) is ignored without a reply.
    member private this.Add(data: Params) : Task =
        task {
            match tryGet "identifier" data with
            | Some(Value.String identifier) ->
                match Json.parse (Encoding.UTF8.GetBytes identifier) with
                | Some(Value.Object parameters) ->
                    if position identifier < 0 then
                        // Bounds on what one socket can make the server hold (Rails has none). A page
                        // subscribes to six channels with identifiers of a few hundred bytes.
                        if subscriptions.Count >= MaxSubscriptions || Encoding.UTF8.GetByteCount identifier > MaxIdentifierBytes then
                            logger.LogError("Could not execute command: subscription limit reached ({Subscriptions})", subscriptions.Count)
                        else
                            let requested =
                                match tryGet "channel" parameters with
                                | Some(Value.String channel) -> channel
                                | _ -> ""
                            match server.Channel requested with
                            | ValueNone -> logger.LogError("Subscription class not found {Channel}", requested)
                            | ValueSome(struct (className, factory)) ->
                                let channel = factory ()
                                let sub =
                                    new Subscription<'U>(server, className, identifier, Json.encode (Value.String identifier), user, wake)
                                subscriptions.Add { Channel = channel; Sub = sub }
                                do! this.SubscribeToChannel identifier
                | _ -> logger.LogError("Could not execute command: invalid identifier {Identifier}", identifier)
            | _ -> logger.LogError "Could not execute command: missing identifier"
        }

    /// `Subscriptions#remove`: no reply either way.
    member private this.Remove(data: Params) : Task =
        task {
            match find data with
            | -1 -> logger.LogError "Unable to find subscription with identifier"
            | index -> do! this.RemoveSubscription index
        }

    /// `Subscriptions#perform_action` -> `Channel::Base#perform_action`.
    member private this.PerformAction(data: Params) : Task =
        task {
            match find data with
            | -1 -> logger.LogError "Unable to find subscription with identifier"
            | index ->
                let payload =
                    match tryGet "data" data with
                    | Some(Value.String text) ->
                        match Json.parse (Encoding.UTF8.GetBytes text) with
                        | Some(Value.Object payload) -> Some payload
                        | _ -> None
                    | _ -> None
                match payload with
                | None -> logger.LogError "Could not execute command: invalid data"
                | Some payload ->
                    let action =
                        match tryGet "action" payload with
                        | None
                        | Some Value.Null -> Some "receive"
                        | Some(Value.String action) when String.IsNullOrWhiteSpace action -> Some "receive"
                        | Some(Value.String action) -> Some action
                        | Some _ -> None
                    match action with
                    | None -> logger.LogError "Could not execute command: invalid action"
                    | Some action ->
                        let entry = subscriptions[index]
                        let! result = safely (fun () -> entry.Channel.Perform action payload entry.Sub)
                        entry.Sub.DrainTransmissions this.Pending
                        entry.Sub.DrainStarted this.Started
                        match result with
                        | Ok true -> ()
                        | Ok false -> logger.LogError("Unable to process {Action}", action)
                        | Error error -> logger.LogError("Could not execute command {Action}: {Error}", action, error.Message)
        }

    /// `Subscriptions#execute_command`. Anything malformed raises in Rails, which is logged and
    /// otherwise ignored; the connection stays open.
    member this.Dispatch(text: string) : Task =
        task {
            match Json.parse (Encoding.UTF8.GetBytes text) with
            | Some(Value.Object data) ->
                match tryGet "command" data with
                | Some(Value.String "subscribe") -> do! this.Add data
                | Some(Value.String "unsubscribe") -> do! this.Remove data
                | Some(Value.String "message") -> do! this.PerformAction data
                | _ -> logger.LogError("Received unrecognized command {Message}", text)
            | _ -> logger.LogError("Could not execute command {Message}", text)
        }

    /// `Connection::Base#handle_close`: unsubscribe everything.
    member this.HandleClose() : Task =
        task {
            while subscriptions.Count > 0 do
                do! this.RemoveSubscription 0
        }

/// Reads the socket until it closes or errors, handing each message, and then the error, to the
/// connection. It stops after a close frame, as the connection does.
let private readLoop (reader: Reader) (sender: Channels.ChannelWriter<Received>) (wake: Wake) (ct: CancellationToken) : Task =
    task {
        try
            let mutable reading = true
            while reading do
                let! message = reader.Next ct
                let last =
                    match message with
                    | Ok(Close _)
                    | Error _ -> true
                    | _ -> false
                do! sender.WriteAsync(message, ct)
                wake.Set()
                if last then reading <- false
        with _ ->
            ()
        sender.TryComplete() |> ignore
        wake.Set()
    }

/// Sends a normal close (1000, no reason, as `ClientSocket#close` defaults) and waits briefly for the
/// client to finish the handshake.
let private closeSocket (sink: Writer) (incoming: Channels.ChannelReader<Received>) (timeout: TimeSpan) : Task =
    task {
        let! sent = attempt (sink.Close 1000us)
        if sent then
            use limit = new CancellationTokenSource(timeout)
            try
                let mutable waiting = true
                while waiting do
                    let! available = incoming.WaitToReadAsync limit.Token
                    if not available then
                        waiting <- false
                    else
                        let mutable item = tryRead incoming
                        while waiting && item.IsSome do
                            match item.Value with
                            | Ok(Close _)
                            | Error _ -> waiting <- false
                            | _ -> item <- tryRead incoming
            with :? OperationCanceledException ->
                ()
    }

/// `Connection::Base#respond_to_invalid_request` for an unauthorized connection: tell the client not
/// to reconnect, and close.
let private rejectUnauthorized (server: Server<'U>) (sink: Writer) (incoming: Channels.ChannelReader<Received>) : Task =
    task {
        server.Logger.LogError "An unauthorized connection attempt was rejected"
        let frame = Frame.OfString(Protocol.disconnect (Some Unauthorized) (Value.Bool false))
        let! _ = attempt ((sink.Send [| frame |]).AsTask())
        do! closeSocket sink incoming server.Config.CloseTimeout
    }

/// Runs one WebSocket connection on the upgraded `stream` until it ends. `abort` cuts the underlying
/// connection (a write that times out calls it).
let run (server: Server<'U>) (stream: Stream) (deflate: bool) (request: ConnectRequest) (abort: unit -> unit) : Task =
    task {
        let config = server.Config
        let logger = server.Logger
        use stop = new CancellationTokenSource()
        let wake = Wake()
        let channel = Channels.Channel.CreateBounded<Received>(Channels.BoundedChannelOptions(IncomingCapacity, SingleReader = true, SingleWriter = true))
        let incoming = channel.Reader
        let sink = Writer(stream, deflate, WriteTimeout, abort)
        let readerTask = readLoop (Reader(stream, deflate)) channel.Writer wake stop.Token

        // handle_open: connect, subscribe to the internal channel, welcome, then process whatever
        // arrived meanwhile (the socket buffers it for us, like MessageBuffer).
        let! authenticated = server.Authenticate request
        let! admitted =
            task {
                match authenticated with
                | None -> return ValueNone
                | Some user ->
                    // The internal channel carries raw payloads; every subscription stream carries frames.
                    let identifier = server.Identify user
                    if identifier = "" then
                        return ValueSome(struct (user, ValueNone))
                    else
                        let internal' = server.Hub.Subscribe(ServerNames.internalChannel identifier, null, wake)
                        // A ban or sign-out that disconnected this user between the check above and that
                        // subscription went unheard, so check again now that it would be heard (Rails has
                        // this gap).
                        match! server.Authenticate request with
                        | None ->
                            (internal' :> IDisposable).Dispose()
                            return ValueNone
                        | Some _ -> return ValueSome(struct (user, ValueSome internal'))
            }

        match admitted with
        | ValueNone ->
            do! rejectUnauthorized server sink incoming
            stop.Cancel()
            do! readerTask
        | ValueSome(struct (user, internal')) ->
            let connection = Connection<'U>(server, user, wake)
            let pending = connection.Pending
            let readers = List<Subscriber>()
            let beacon = server.Register wake
            let mutable closing: Closing voption = ValueNone
            try
                try
                    let mutable running = true
                    try
                        do! sink.Send [| Frame.OfString(Protocol.welcome ()) |]
                    with _ ->
                        running <- false
                    while running do
                        let! _ = wake.WaitAsync()

                        // One command, or why the socket stopped; the rest wait for another turn.
                        match tryRead incoming with
                        | ValueSome message ->
                            wake.Set()
                            match message with
                            | Ok(Text text) -> do! connection.Dispatch text
                            | Ok Binary -> logger.LogError "Couldn't handle non-string message: Array"
                            | Ok(Ping payload) ->
                                let! ponged = attempt (sink.Pong payload)
                                if not ponged then running <- false
                            | Ok Pong -> ()
                            // Complete the closing handshake (RFC 6455 section 5.5.1) before letting the socket go.
                            | Ok(Close code) ->
                                let! _ = attempt (sink.CloseReply code)
                                running <- false
                            | Error(ProtocolViolation code) ->
                                let! _ = attempt (sink.Close code)
                                running <- false
                            | Error(Io _) -> running <- false
                        | ValueNone -> if incoming.Completion.IsCompleted then running <- false

                        if running && closing.IsNone then
                            // Whatever is ready goes out in the same write.
                            match drain readers pending config.MaxWriteBatch with
                            | Drained.Lagged -> closing <- ValueSome lagged
                            | Drained.Full -> wake.Set()
                            | _ -> ()

                            // The internal channel carries raw payloads.
                            match internal' with
                            | ValueSome internal' ->
                                match drainInternal internal' with
                                | ValueSome remote -> closing <- ValueSome remote
                                | ValueNone -> ()
                            | ValueNone -> ()

                            // One ping per beat, shared by every connection.
                            if beacon.BeatSeen <> server.Beat then
                                beacon.BeatSeen <- server.Beat
                                pending.Add server.HeartbeatFrame
                            if beacon.RestartSeen <> server.Restarts then
                                beacon.RestartSeen <- server.Restarts
                                closing <- ValueSome { Reason = Some ServerRestart; Reconnect = Value.Bool true }

                        if running then
                            // The streams a command started are read from now that its own frames are queued
                            // ahead of them.
                            if connection.Started.Count > 0 then
                                readers.AddRange connection.Started
                                connection.Started.Clear()
                                wake.Set()
                            if pending.Count > 0 then
                                try
                                    do! sink.Send pending
                                with _ ->
                                    running <- false
                                pending.Clear()
                            if running && closing.IsSome then
                                let { Reason = reason; Reconnect = reconnect } = closing.Value
                                closing <- ValueNone
                                let! _ = attempt ((sink.Send [| Frame.OfString(Protocol.disconnect reason reconnect) |]).AsTask())
                                do! closeSocket sink incoming config.CloseTimeout
                                running <- false
                finally
                    server.Unregister beacon
            with e ->
                logger.LogError(e, "Cable connection failed")
            stop.Cancel()
            do! readerTask
            do! connection.HandleClose()
            for reader in readers do
                (reader :> IDisposable).Dispose()
            for started in connection.Started do
                (started :> IDisposable).Dispose()
            match internal' with
            | ValueSome internal' -> (internal' :> IDisposable).Dispose()
            | ValueNone -> ()
    }
