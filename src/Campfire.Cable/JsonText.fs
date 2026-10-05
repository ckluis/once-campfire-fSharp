// Not in the Rust crate. Rust hands `serde_json` a `&str` and gets a `String` back per broadcast; this
// writes the same Active Support JSON (`Json.encode (Value.String s)`: quote, backslash and control
// characters escaped as serde_json does, plus `<`, `>` and `&` as <, > and &) straight into
// a pooled UTF-8 buffer, in pieces, so a Turbo Stream tag and the HTML inside it are escaped as they are
// appended and never exist as one string.
namespace Campfire.Cable

open System
open System.Buffers
open System.Text

/// A JSON string being written as UTF-8 into a pooled buffer. Dispose it to return the buffer.
[<Sealed; AllowNullLiteral>]
type JsonStringWriter(initialCapacity: int) =
    static let escapes =
        let specials = Array.init 32 char |> Array.append [| '"'; '\\'; '<'; '>'; '&' |]
        SearchValues.Create(ReadOnlySpan specials)

    static let escapeBytes =
        let specials = Array.init 32 byte |> Array.append [| byte '"'; byte '\\'; byte '<'; byte '>'; byte '&' |]
        SearchValues.Create(ReadOnlySpan specials)

    static let hex = "0123456789abcdef"

    let mutable buffer: byte[] = ArrayPool<byte>.Shared.Rent(max initialCapacity 64)
    let mutable length = 0

    new() = new JsonStringWriter(1024)

    member private _.Ensure(extra: int) =
        if buffer.Length - length < extra then
            let grown = ArrayPool<byte>.Shared.Rent(max (buffer.Length * 2) (length + extra))
            Buffer.BlockCopy(buffer, 0, grown, 0, length)
            ArrayPool<byte>.Shared.Return buffer
            buffer <- grown

    /// The opening quote.
    member this.Begin() : unit =
        this.Ensure 1
        buffer[length] <- byte '"'
        length <- length + 1

    /// The closing quote.
    member this.End() : unit = this.Begin()

    member private this.Escaped(c: char) =
        this.Ensure 6
        let put (b: char) =
            buffer[length] <- byte b
            length <- length + 1
        match c with
        | '"' -> put '\\'; put '"'
        | '\\' -> put '\\'; put '\\'
        | '\b' -> put '\\'; put 'b'
        | '\012' -> put '\\'; put 'f'
        | '\n' -> put '\\'; put 'n'
        | '\r' -> put '\\'; put 'r'
        | '\t' -> put '\\'; put 't'
        | c ->
            // Control characters, and `<`, `>` and `&` for ActiveSupport.
            put '\\'
            put 'u'
            put '0'
            put '0'
            put hex[(int c >>> 4) &&& 0xf]
            put hex[int c &&& 0xf]

    /// Appends `text`, escaped, between the quotes.
    member this.Append(text: ReadOnlySpan<char>) : unit =
        let mutable rest = text
        while not rest.IsEmpty do
            match rest.IndexOfAny escapes with
            | -1 ->
                this.Ensure(Encoding.UTF8.GetMaxByteCount rest.Length)
                length <- length + Encoding.UTF8.GetBytes(rest, Span<byte>(buffer, length, buffer.Length - length))
                rest <- ReadOnlySpan()
            | at ->
                if at > 0 then
                    let run = rest.Slice(0, at)
                    this.Ensure(Encoding.UTF8.GetMaxByteCount run.Length)
                    length <- length + Encoding.UTF8.GetBytes(run, Span<byte>(buffer, length, buffer.Length - length))
                this.Escaped rest[at]
                rest <- rest.Slice(at + 1)

    member this.Append(text: string) : unit = this.Append(text.AsSpan())

    /// Appends UTF-8 text (valid, as a view renders it), escaped, between the quotes: the bytes that
    /// need escaping are all ASCII, so everything else is copied as it is.
    member this.AppendUtf8(text: ReadOnlySpan<byte>) : unit =
        let mutable rest = text
        while not rest.IsEmpty do
            let at = rest.IndexOfAny escapeBytes
            let run = if at < 0 then rest.Length else at
            if run > 0 then
                this.Ensure run
                rest.Slice(0, run).CopyTo(Span(buffer, length, run))
                length <- length + run
            if at >= 0 then
                this.Escaped(char rest[at])
                rest <- rest.Slice(at + 1)
            else
                rest <- ReadOnlySpan()

    /// Appends text that needs no escaping (ASCII markup with none of the escaped characters).
    member this.AppendSafe(text: string) : unit =
        this.Ensure text.Length
        for c in text do
            buffer[length] <- byte c
            length <- length + 1

    member _.Length = length
    member _.Span = ReadOnlySpan<byte>(buffer, 0, length)
    member _.ToArray() : byte[] = buffer.AsSpan(0, length).ToArray()

    interface IDisposable with
        member _.Dispose() =
            if buffer.Length > 0 then
                ArrayPool<byte>.Shared.Return buffer
                buffer <- Array.Empty<byte>()
                length <- 0

module JsonText =
    /// `Json.encode (Value.String s)` as UTF-8.
    let encodeString (s: string) : byte[] =
        use w = new JsonStringWriter(s.Length + 16)
        w.Begin()
        w.Append s
        w.End()
        w.ToArray()
