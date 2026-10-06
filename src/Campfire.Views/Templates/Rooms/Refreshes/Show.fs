// Port of rust/crates/views/templates/rooms/refreshes/show.turbo_stream.html (reference/app/views/rooms/refreshes/show.turbo_stream.erb)
/// `rooms/refreshes/show.turbo_stream.erb`, byte for byte as Erubi renders it: the append (when there are new
/// messages), then the blank line between the two blocks (so an empty refresh is "\n", which Rack::ETag digests),
/// then one indented line per replace.
module Campfire.Views.Templates.Rooms.Refreshes.Show

open Campfire.Views
open Campfire.Views.Messages
open Campfire.Views.Rooms

let private messagesPrefix = Utf8.lit "messages_"
let private indent = Utf8.lit "  "
let private messageId = Utf8.lit "message_"

let private t0 = Utf8.lit "<turbo-stream action=\"append\" target=\""
let private t1 = Utf8.lit "\"><template>\n  "
let private t2 = Utf8.lit "\n</template></turbo-stream>"
let private t3 = Utf8.lit "\n"
let private t4 = Utf8.lit "<turbo-stream action=\"replace\" target=\""
let private t5 = Utf8.lit "\"><template>"
let private t6 = Utf8.lit "</template></turbo-stream>\n"

let render (w: Out) (ctx: ViewContext) (refresh: RefreshView) : unit =
    if not refresh.NewMessages.IsEmpty then
        w.Lit t0
        writeRoomDomId w messagesPrefix refresh.RoomKind refresh.RoomId
        w.Lit t1
        for message in refresh.NewMessages do
            MessagesCached.cachedMessageItem w ctx message
        w.Lit t2
    w.Lit t3
    for message in refresh.UpdatedMessages do
        w.Lit indent
        w.Lit t4
        w.Lit messageId
        w.Text message.ClientMessageId
        w.Lit t5
        MessagesCached.cachedMessageItem w ctx message
        w.Lit t6
