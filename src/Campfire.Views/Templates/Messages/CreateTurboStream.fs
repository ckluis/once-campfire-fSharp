// Port of rust/crates/views/templates/messages/create.turbo_stream.html (reference/app/views/messages/create.turbo_stream.erb)
/// `messages/create.turbo_stream.erb`: appends the new message to its room's list. Also what `Message#broadcast_create` sends.
module Campfire.Views.Templates.Messages.CreateTurboStream

open Campfire.Views
open Campfire.Views.Messages

let private messagesPrefix = Utf8.lit "messages_"

let private t0 = Utf8.lit "<turbo-stream action=\"append\" target=\""
let private t1 = Utf8.lit "\"><template>"
let private t2 = Utf8.lit "</template></turbo-stream>"

let render (w: Out) (ctx: ViewContext) (message: MessageItem) (roomKind: RoomKind) : unit =
    w.Lit t0
    writeRoomDomId w messagesPrefix roomKind message.RoomId
    w.Lit t1
    MessagesCached.cachedMessageItem w ctx message
    w.Lit t2
