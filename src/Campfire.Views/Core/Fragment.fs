// Port of `Fragment` in rust/crates/views/src/fragment_cache.rs (`Arc<String>`)
namespace Campfire.Views

open System.Threading

/// What the layer that splices pages (the kit's page parts) remembers about a fragment and keeps
/// alive with it: its SHA-256 and the compressed pieces made for the parts it followed. Rust keeps
/// those in a bounded map in the kit, keyed by the address of the fragment's `Arc`; a .NET object
/// can't be found by address, so they hang off the fragment (`Fragment.Attach`), and the
/// fragment cache counts them (`HeldBytes`) in what an entry costs, so dropping the fragment
/// from the cache drops them.
///
/// `Campfire.Views` does not reference `Campfire.Kit` (nor does `rust/crates/views` reference the
/// kit), so the kit's side of this is an object `Campfire.App` attaches to a fragment the first
/// time a page splices it.
[<AllowNullLiteral>]
type IFragmentAttachment =
    /// The bytes the attachment holds for its fragment at this moment (its pieces, SHA and
    /// bookkeeping): they grow as pages with different neighbours splice the fragment.
    abstract HeldBytes: int

/// A rendered HTML fragment, as the fragment cache hands it out: exactly its bytes (no spare
/// capacity, as Rust's `shrink_to_fit` leaves a stored `String`) and an identity: a fragment
/// rendered again is a new object with its own attachment, as a new `Arc` is a new address.
[<Sealed; AllowNullLiteral>]
type Fragment(bytes: byte[]) =
    [<VolatileField>]
    let mutable attachment: IFragmentAttachment = null

    new(text: string) = Fragment(System.Text.Encoding.UTF8.GetBytes text)

    member _.Bytes: byte[] = bytes
    member _.Length: int = bytes.Length
    member _.Memory: System.ReadOnlyMemory<byte> = System.ReadOnlyMemory<byte> bytes
    member _.Span: System.ReadOnlySpan<byte> = System.ReadOnlySpan<byte> bytes

    /// What the splicing layer remembers about this fragment, if anything yet.
    member _.Attachment: IFragmentAttachment = attachment

    /// Remembers `value` unless something is remembered already (two pages may splice a new
    /// fragment at once; either answer is the same), and returns what the fragment now holds.
    member _.Attach(value: IFragmentAttachment) : IFragmentAttachment =
        match Interlocked.CompareExchange(&attachment, value, null) with
        | null -> value
        | existing -> existing

    /// What the fragment keeps alive: its bytes and what is attached to it.
    member _.HeldBytes: int =
        match attachment with
        | null -> bytes.Length
        | held -> bytes.Length + held.HeldBytes

    override this.ToString() = System.Text.Encoding.UTF8.GetString bytes
