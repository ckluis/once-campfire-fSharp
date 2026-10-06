// What `Campfire.App` does between the views and the kit (the contract Phase 4 left in
// `Campfire.Views.Tests.KitIntegrationTests`): a page rendered with `Render.page` is a text and the
// fragments it recorded, the kit's `PageParts.Splice` takes those, and the kit's SHA and pieces for each
// fragment hang off the view fragment and are counted by the fragment cache. `Campfire.Views` doesn't
// reference `Campfire.Kit` (nor does rust/crates/views reference the kit); this is where they meet, as
// `Ctx::render_spliced` and `Body::spliced` are in Rust.
namespace Campfire.App

open System
open Campfire.Kit
open Campfire.Views

/// The kit's side of a view fragment: what `Fragment.Attach` holds, found again for every page.
[<Sealed>]
type KitLink(fragment: Campfire.Kit.Fragment) =
    member _.Fragment = fragment

    interface IFragmentAttachment with
        member _.HeldBytes = fragment.HeldBytes

module FragmentGlue =
    /// The kit fragment of a view fragment, made the first time a page splices it.
    let kitFragment (fragment: Campfire.Views.Fragment) : Campfire.Kit.Fragment =
        match fragment.Attachment with
        | :? KitLink as link -> link.Fragment
        | _ ->
            match fragment.Attach(KitLink(Campfire.Kit.Fragment fragment.Bytes)) with
            | :? KitLink as link -> link.Fragment
            | _ -> failwith "only the app attaches to a fragment"

    /// The page's body split at its fragments, or that body whole when none is big enough for a part.
    let splice (page: RecordedPage) : Result<PageParts, ReadOnlyMemory<byte>> =
        let fragments =
            page.Fragments |> Array.map (fun struct (offset, fragment) -> struct (offset, kitFragment fragment))
        PageParts.Splice(ReadOnlyMemory<byte> page.Text, fragments)

    /// `Ctx::render_spliced` for a recorded page: its text with its cached fragments spliced in, which
    /// the kit keeps as parts rather than joining them.
    let renderRecorded (c: Ctx) (status: int) (template: Format) (page: RecordedPage) : Response =
        if page.Fragments.Length = 0 then
            c.Render(status, template, ReadOnlyMemory<byte> page.Text)
        else
            match splice page with
            | Ok parts -> c.RenderParts(status, template, parts)
            | Error whole -> c.Render(status, template, whole)
