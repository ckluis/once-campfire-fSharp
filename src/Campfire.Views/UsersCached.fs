// Port of the functions of rust/crates/views/src/users.rs that render templates into the fragment cache
/// `users/sidebars/rooms/_direct` for a membership, whose body is `cache membership` (and which `users/sidebars/show`
/// renders with `cached: true`): the first rendering of a membership version is what later renders reuse.
module Campfire.Views.UsersCached

open Campfire.Views.Users
open Campfire.Views.Templates.Users.Sidebars.Rooms

/// `direct_room(ctx, membership)`.
let directRoom (ctx: ViewContext) (membership: SidebarDirect) : Fragment =
    FragmentCache.fetch
        (fun key -> directRoomFragmentKey key membership.MembershipId membership.MembershipUpdatedAt)
        (fun w -> _Direct.render w ctx membership)

/// `cached_direct_room(ctx, item)`: `directRoom` where a template renders the partial. The fragment is copied into
/// the page, not recorded: Rust hands over only the message fragments (`cached_message_item`).
let cachedDirectRoom (w: Out) (ctx: ViewContext) (item: SidebarDirectItem) : unit =
    let fragment =
        match item with
        | Cached html -> html
        | View membership -> directRoom ctx membership
    w.Lit fragment.Bytes
