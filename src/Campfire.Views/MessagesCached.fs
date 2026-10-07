// Port of the functions of rust/crates/views/src/messages.rs that render `messages/_message` into the fragment cache
/// `messages/_message` for a message, whose body is `cache [ message, "presentation-v3" ]` (and whose collection renders
/// are `cached: true`), so a message version renders once.
module Campfire.Views.MessagesCached

open Campfire.Views.Messages
open Campfire.Views.Templates.Messages

/// `message(ctx, message)`.
let message (ctx: ViewContext) (message: MessageView) : Fragment =
    FragmentCache.fetch (fun key -> messageFragmentKey key message.Id message.UpdatedAt) (fun w -> _Message.render w ctx message)

/// `cached_message(ctx, message)`: `message` where a template renders the partial. Copied into the page, not recorded.
let cachedMessage (w: Out) (ctx: ViewContext) (view: MessageView) : unit = w.Lit((message ctx view).Bytes)

/// `cached_message_item(ctx, item)`: `cachedMessage` for a `MessageItem`: a fragment found up front goes out as it
/// is. A page being recorded notes where the fragment goes instead of copying it in.
let cachedMessageItem (w: Out) (ctx: ViewContext) (item: MessageItem) : unit =
    match item with
    | MessageItem.Cached(_, _, html) -> w.Fragment html
    | MessageItem.View view -> w.Fragment(message ctx view)
