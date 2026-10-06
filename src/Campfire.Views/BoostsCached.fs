// Port of the functions of rust/crates/views/src/messages.rs that render `messages/boosts/_boost` into the fragment cache
/// `messages/boosts/_boost` for a boost, whose body is `cache boost`.
module Campfire.Views.BoostsCached

open Campfire.Views.Messages
open Campfire.Views.Templates.Messages.Boosts

/// `boost(ctx, boost)`.
let boost (ctx: ViewContext) (boost: BoostView) : Fragment =
    FragmentCache.fetch (fun key -> boostFragmentKey key boost.Id boost.UpdatedAt) (fun w -> _Boost.render w ctx boost)

/// `cached_boost(ctx, boost)`: `boost` where a template renders the partial. Not handed over to a recorded page, as in Rust.
let cachedBoost (w: Out) (ctx: ViewContext) (boostView: BoostView) : unit = w.Lit((boost ctx boostView).Bytes)
