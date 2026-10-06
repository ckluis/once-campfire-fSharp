// Port of rust/crates/views/src/{recorded.rs, sized.rs, helpers/html.rs}, with askama's `Template`
// and `fmt::Write` replaced by the writer below.
//
// HOW A TEMPLATE RENDERS (read this before porting a template, units 4.2 and 4.3 follow it)
//
// A template is an F# module at the same relative path as its ERB (and its askama file in
// rust/crates/views/templates), `Campfire.Views.Templates.<Path>`: `messages/_message.html` is
// `Templates/Messages/_Message.fs`, `rooms/show.html` is `Templates/Rooms/Show.fs`,
// `index.turbo_stream.html` is `IndexTurboStream`. It has one `render` function that writes UTF-8
// straight into an `Out` and returns unit, taking the view-model the Rust struct had as arguments:
//
//     module Campfire.Views.Templates.Accounts._Invite
//     let private t0 = Utf8.lit "<div class=\"flex ...\">\n"      // static text, encoded once
//     let render (w: Out) (ctx: ViewContext) (joinCode: string) =
//         w.Lit t0                                                 // template text: bytes copied
//         w.Text ctx.Account.Name                                  // {{ expr }}: ERB-escaped
//         w.Raw importmapTags                                      // {{ expr|safe }}: as is
//         Assets.imageTag w ctx "person-add.svg" (Tag.attrs().AriaHidden().Size 20)   // a helper
//         Links.linkTo w url (Tag.attrs().Class "btn") (fun w -> ...)                  // a block
//
// Rules:
// - Template text between `{{ }}` and `{% %}` is a `Utf8.lit` bound with a module-level `let
//   private`, so it is encoded once at startup, never per request. Keep each literal exactly as the
//   askama template has it, whitespace included (`{%-`/`-%}` trim it, as askama does).
// - Every value goes through `Text` (escaped, the default of `{{ }}`) unless the Rust says `safe`/
//   `h::raw` or the value is a helper's output, which writes itself. A value that is already
//   html_safe and needs to travel (a layout's `head`/`nav` parts, a fragment) is an `Html` or a
//   `Fragment`, never a string, so it can't be escaped twice or left unescaped by mistake.
// - A block (`{% filter link_to(..) %}...{% endfilter %}`, `content_for`) is a function of the
//   writer, `fun w -> ...`; helpers that take one are `inline`, so there is no closure.
// - Cached fragments (`cache record do`) are rendered by `FragmentCache` into their own writer and
//   written with `w.Fragment`; a page rendering with `Render.page` records them instead of copying
//   them (see `RecordedPage`).
// - Nothing allocates a string for markup: no `sprintf`, `String.Format`, `+` on markup or
//   `StringBuilder`. View-model strings are read as they are and escaped as they are written.
namespace Campfire.Views

open System
open System.Buffers
open System.Buffers.Text
open System.Collections.Generic
open System.Text
open Campfire.Ruby

module Utf8 =
    /// A piece of template text as UTF-8, encoded once (bind it with a module-level `let`).
    let lit (text: string) : byte[] = Encoding.UTF8.GetBytes text

/// An html_safe string (`ActiveSupport::SafeBuffer`), as UTF-8 bytes `Out` writes without escaping:
/// what a helper that returns markup hands back to a template and a layout takes for its `head`,
/// `nav` and `content` parts. Askama's `Safe<String>`.
[<Sealed; AllowNullLiteral>]
type Html(bytes: byte[]) =
    static let empty = Html(Array.empty)

    /// `h::empty()`.
    static member Empty: Html = empty

    /// `raw` / `String#html_safe`: `html` as it is.
    static member Raw(html: string) : Html = Html(Encoding.UTF8.GetBytes html)

    /// `h(text)` as an html_safe value.
    static member Text(text: string) : Html = Html(Encoding.UTF8.GetBytes(Erb.htmlEscape text))

    member _.Bytes: byte[] = bytes
    member _.Length: int = bytes.Length
    member _.IsEmpty: bool = bytes.Length = 0
    override _.ToString() = Encoding.UTF8.GetString bytes

/// A rendered page: its text, and each cached fragment with its byte offset in the text, in order.
/// A room page is mostly its ~40 message fragments (~400 KB); copying them into one buffer only for
/// the kit to find them again (for its gzip and ETag, which reuse what it knows about each
/// fragment) costs more than the rest of assembling the page. So a template hands a fragment to
/// `Out.Fragment`, and a page rendered with `Render.page` notes the fragment at its offset in the
/// text instead of copying it. Anything else, including a fragment under `MinRecorded` bytes, is
/// copied in as text, so the page is always the bytes a plain render gives.
[<Sealed; AllowNullLiteral>]
type RecordedPage(text: byte[], fragments: struct (int * Fragment)[]) =
    /// A page rendered without recording.
    new(text: byte[]) = RecordedPage(text, Array.empty)

    /// The text, without its fragments.
    member _.Text: byte[] = text

    member _.Fragments: struct (int * Fragment)[] = fragments

    /// The page's length: its text and its fragments.
    member _.Length: int =
        let mutable length = text.Length
        for struct (_, fragment) in fragments do
            length <- length + fragment.Length
        length

    /// The whole page in one array.
    member this.ToArray() : byte[] =
        let all = GC.AllocateUninitializedArray<byte> this.Length
        let mutable at = 0
        let mutable position = 0
        for struct (offset, fragment) in fragments do
            Buffer.BlockCopy(text, position, all, at, offset - position)
            at <- at + (offset - position)
            position <- offset
            Buffer.BlockCopy(fragment.Bytes, 0, all, at, fragment.Length)
            at <- at + fragment.Length
        Buffer.BlockCopy(text, position, all, at, text.Length - position)
        all

    override this.ToString() = Encoding.UTF8.GetString(this.ToArray())

/// The writer a template renders into: a pooled UTF-8 buffer. `Out.Rent` takes one (a thread's
/// spare, so a request's renders reuse the same object) and `Out.Return` gives it back along with
/// its array; `Render` does both.
[<Sealed>]
type Out private (capacity: int) =
    /// Shorter writes are copied without being noted as a fragment: the kit keeps fragments under
    /// 1 KB in the text around them anyway (`Campfire.Kit` splice), and noting every short one would
    /// cost more than copying it.
    [<Literal>]
    static let MinRecorded = 1024

    [<ThreadStatic; DefaultValue>]
    static val mutable private spare: Out | null

    let mutable buffer: byte[] = ArrayPool<byte>.Shared.Rent(max capacity 4096)
    let mutable length = 0
    let mutable fragments: List<struct (int * Fragment)> | null = null

    static member Rent(capacity: int) : Out =
        match Out.spare with
        | null -> Out capacity
        | spare ->
            Out.spare <- null
            spare.Start capacity
            spare

    /// Gives the writer (and its array) back; it must not be used afterwards.
    static member Return(out: Out) : unit =
        out.Release()
        if isNull Out.spare then Out.spare <- out

    member private _.Start(capacity: int) =
        buffer <- ArrayPool<byte>.Shared.Rent(max capacity 4096)
        length <- 0
        fragments <- null

    member private _.Release() =
        if buffer.Length > 0 then
            ArrayPool<byte>.Shared.Return buffer
            buffer <- Array.empty
        length <- 0
        fragments <- null

    /// Records fragments handed to `Fragment` from now on (`Render.page` starts it).
    member _.StartRecording() = fragments <- List<struct (int * Fragment)>()

    member _.Length: int = length
    member _.WrittenSpan: ReadOnlySpan<byte> = ReadOnlySpan<byte>(buffer, 0, length)

    member private _.Reserve(count: int) : unit =
        if buffer.Length - length < count then
            let larger = ArrayPool<byte>.Shared.Rent(max (buffer.Length * 2) (length + count))
            Buffer.BlockCopy(buffer, 0, larger, 0, length)
            ArrayPool<byte>.Shared.Return buffer
            buffer <- larger

    /// Static template text, already UTF-8.
    member this.Lit(bytes: byte[]) : unit =
        this.Reserve bytes.Length
        Buffer.BlockCopy(bytes, 0, buffer, length, bytes.Length)
        length <- length + bytes.Length

    member this.Lit(bytes: ReadOnlySpan<byte>) : unit =
        this.Reserve bytes.Length
        bytes.CopyTo(Span<byte>(buffer, length, bytes.Length))
        length <- length + bytes.Length

    /// An ASCII character (a quote, a space, a slash); anything else goes through `Raw`.
    member this.Byte(b: byte) : unit =
        this.Reserve 1
        buffer[length] <- b
        length <- length + 1

    /// `{{ value|safe }}`, `h::raw(value)`: html_safe text, as it is.
    member this.Raw(html: string | null) : unit =
        match html with
        | null -> ()
        | html ->
            let count = Encoding.UTF8.GetByteCount html
            this.Reserve count
            Encoding.UTF8.GetBytes(html.AsSpan(), Span<byte>(buffer, length, count)) |> ignore
            length <- length + count

    member this.Raw(html: ReadOnlySpan<char>) : unit =
        let count = Encoding.UTF8.GetByteCount html
        this.Reserve count
        Encoding.UTF8.GetBytes(html, Span<byte>(buffer, length, count)) |> ignore
        length <- length + count

    member this.Raw(html: Html) : unit = this.Lit html.Bytes

    /// `{{ value }}`: ERB-escaped (`& < > " '`). `nil` is the empty string, as `nil.to_s` is.
    member this.Text(text: string | null) : unit =
        match text with
        | null -> ()
        | text -> Erb.writeHtmlEscapedUtf8 (this :> IBufferWriter<byte>) (text.AsSpan())

    member this.Text(text: ReadOnlySpan<char>) : unit = Erb.writeHtmlEscapedUtf8 (this :> IBufferWriter<byte>) text

    /// An integer, as `{{ n }}` prints it.
    member this.Int(value: int64) : unit =
        this.Reserve 20
        let mutable written = 0
        Utf8Formatter.TryFormat(value, Span<byte>(buffer, length, 20), &written) |> ignore
        length <- length + written

    /// A cached fragment, as is: noted at its offset while the page records and the fragment is
    /// big enough to be worth a part of its own, copied otherwise.
    member this.Fragment(fragment: Fragment) : unit =
        match fragments with
        | null -> this.Lit fragment.Bytes
        | recorded when fragment.Length >= MinRecorded -> recorded.Add(struct (length, fragment))
        | _ -> this.Lit fragment.Bytes

    /// A page written into another one (turbo-rails' frame layout around a page's content): its
    /// text, and its fragments handed over again, so they stay fragments.
    member this.Page(page: RecordedPage) : unit =
        let mutable position = 0
        for struct (offset, fragment) in page.Fragments do
            this.Lit(ReadOnlySpan<byte>(page.Text, position, offset - position))
            this.Fragment fragment
            position <- offset
        this.Lit(ReadOnlySpan<byte>(page.Text, position, page.Text.Length - position))

    /// Another writer's bytes (a block that had to render first, as a form's fields do).
    member this.Append(other: Out) : unit = this.Lit other.WrittenSpan

    /// What has been written, as one exact array.
    member _.ToArray() : byte[] =
        let all = GC.AllocateUninitializedArray<byte> length
        Buffer.BlockCopy(buffer, 0, all, 0, length)
        all

    /// The recorded page: exactly the text written, and the fragments noted.
    member _.ToRecordedPage() : RecordedPage =
        let noted =
            match fragments with
            | null -> Array.empty
            | recorded -> recorded.ToArray()
        RecordedPage(Array.sub buffer 0 length, noted)

    override _.ToString() = Encoding.UTF8.GetString(buffer, 0, length)

    interface IBufferWriter<byte> with
        member this.Advance(count: int) = length <- length + count

        member this.GetMemory(sizeHint: int) =
            this.Reserve(max sizeHint 1)
            Memory<byte>(buffer, length, buffer.Length - length)

        member this.GetSpan(sizeHint: int) =
            this.Reserve(max sizeHint 1)
            Span<byte>(buffer, length, buffer.Length - length)

/// Renders a template into a pooled writer and takes what it wrote.
module Render =
    /// A page, recording its fragments (`recorded::render`), into a writer with room for `capacity`
    /// bytes of text.
    let inline page (capacity: int) ([<InlineIfLambda>] template: Out -> unit) : RecordedPage =
        let out = Out.Rent capacity
        try
            out.StartRecording()
            template out
            out.ToRecordedPage()
        finally
            Out.Return out

    /// `template.render()`: the page as one exact array, fragments copied in.
    let inline plain ([<InlineIfLambda>] template: Out -> unit) : byte[] =
        let out = Out.Rent 0
        try
            template out
            out.ToArray()
        finally
            Out.Return out

    /// What a template writes, as an html_safe value.
    let inline html ([<InlineIfLambda>] template: Out -> unit) : Html = Html(plain template)

    /// What a template writes, as text (tests and tools; requests never need a string).
    let inline text ([<InlineIfLambda>] template: Out -> unit) : string = Encoding.UTF8.GetString(plain template)

/// Page renders into a buffer sized from the last render at the same call site (each page module
/// keeps one, `let private size = RenderSize()`; Rust's `render_sized!` makes one per call site).
/// A pooled writer grows by doubling, so a room page's buffer would be copied several times.
/// Only the page's text goes into the buffer: its cached fragments are recorded instead.
[<Sealed>]
type RenderSize() =
    /// The most a render reserves up front. Only the last render's length is kept, so one huge page
    /// sizes just the next render; this cap bounds even that one.
    [<Literal>]
    static let MaxReserve = 1 <<< 20

    let mutable last = 0

    /// The last length plus an eighth, so a page a little longer than the last still fits.
    member _.Capacity(sizeHint: int) : int = max (min (last + last / 8) MaxReserve) sizeHint

    /// `template` rendered and recorded, into a buffer sized from the last render here.
    member this.Render(template: Out -> unit) : RecordedPage =
        let page = Render.page (this.Capacity 0) template
        last <- page.Text.Length
        page

    static member Max = MaxReserve
