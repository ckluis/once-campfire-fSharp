// Port of rust/crates/kit/src/deflater/splice.rs
//
// gzip and ETags for pages made mostly of cached fragments (a room's messages), without
// recompressing or rehashing the whole page on every request.
//
// A page arrives in parts that cover it end to end: its cached fragments (each with the few bytes
// of text before it, when that follows another fragment) and the text in between (the layout). The
// template recorded where each fragment went as it rendered, so the page's bytes are never joined
// into one buffer, nor searched for its fragments: a plain response sends the parts one after
// another. Each part is compressed once, against the part before it as a preset dictionary, and
// kept with the part's CRC-32: deflate back-references can reach anything in the last 32 KB of
// output, so a piece is valid wherever the same predecessor comes right before it. Pages render the
// same until what they show changes (there are no per-request CSRF tokens), so from one request to
// the next a page is a run of stored pieces: gzip costs some copying and combining their CRCs, and
// the ETag a hash of the parts' digests instead of the whole body. Compressing each part on its own
// would lose what consecutive messages share and make a room page ~4x larger; chained like this it's
// within 1% of compressing the page whole.
//
// A stored piece is found by its part (a text by its SHA-256, a fragment by the `Fragment` object
// the fragment cache hands out) and by the SHA-256 of the part before it: those bytes are all it
// depends on besides its own, so it's valid after any part with the same ones. The digests are
// remembered, so pages don't hash their parts on every request: a fragment's with the fragment, a
// text's by the text's bytes.
//
// Where Rust keys a fragment's SHA-256 and pieces by the address of its `Arc<String>` in a bounded
// map (and pins the address with a `Weak`), a .NET object can't be found by address, so they hang
// off the `Fragment` itself and live as long as the fragment does: the fragment cache bounds the
// fragments, and `PiecesPerFragment` bounds what each keeps.
namespace Campfire.Kit

open System
open System.Buffers
open System.Buffers.Binary
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Threading
open Campfire.Kit

module internal SpliceLimits =
    /// Smaller fragments aren't worth a part of their own; they stay in the text around them.
    [<Literal>]
    let MinFragment = 1024

    /// At most this much text between two fragments travels with the second; more is a text part.
    [<Literal>]
    let MaxGlue = 256

    /// Deflate's window.
    [<Literal>]
    let Window = 32768

    /// Pieces kept per fragment, for the predecessors it's seen with: a message follows the same one
    /// in its room and on a page of older messages, and other ones in search results.
    [<Literal>]
    let PiecesPerFragment = 4

    /// A bound on the bytes of stored text pieces (a room page's layout is ~10 KB compressed).
    [<Literal>]
    let MaxTextPieceBytes = 16777216

    /// Larger pieces aren't stored: one would take a good part of a generation, and rotating
    /// generations to fit it would push out the pieces pages keep using.
    [<Literal>]
    let MaxStoredTextPiece = 262144

    /// A bound on the bytes of texts remembered with their SHA-256. A room page's texts (the layout
    /// before and after its messages) are ~34 KB for each person and room, so a generation holds those
    /// of ~240 rooms as people see them: every page in use at a small install. A text that isn't
    /// remembered is simply hashed again.
    [<Literal>]
    let MaxTextBytes = 16777216

    /// Larger texts are hashed every time, for the same reason larger pieces aren't stored.
    [<Literal>]
    let MaxStoredText = 262144

    /// How many of the most recent texts are kept at hand (a power of two), and the largest one kept
    /// there: at most 2 MB beyond what `MaxTextBytes` allows.
    [<Literal>]
    let RecentSlots = 64

    [<Literal>]
    let MaxRecentText = 32768

// --- CRC-32 from the pieces' --------------------------------------------------------------------

/// gzip's CRC-32 of a part, kept so a page's comes from its parts' without reading the body: the CRC
/// of `a || b` is the CRC of `a` times x^(8*|b|), plus the CRC of `b`, as polynomials over GF(2)
/// modulo the CRC's (zlib's `crc32_combine_op`). A part keeps its x^(8*|b|) too, so each step is one
/// multiplication.
[<Struct>]
type Crc = { Value: uint32; Shift: uint32 }

module internal Crc32 =
    /// The CRC-32 polynomial, in the reflected bit order gzip's CRC uses (bit 31 is x^0).
    [<Literal>]
    let private Polynomial = 0xedb88320u

    /// `a` times `b` modulo the polynomial (zlib's `multmodp`), without branching on the bits.
    let multiplySlowly (a: uint32) (b: uint32) : uint32 =
        let mutable product = 0u
        let mutable b = b
        let mutable bit = 32
        while bit > 0 do
            bit <- bit - 1
            product <- product ^^^ (b &&& (0u - ((a >>> bit) &&& 1u)))
            b <- (b >>> 1) ^^^ (Polynomial &&& (0u - (b &&& 1u)))
        product

    let private reverseBits (v: uint32) : uint32 =
        if System.Runtime.Intrinsics.Arm.ArmBase.IsSupported then
            System.Runtime.Intrinsics.Arm.ArmBase.ReverseElementBits v
        else
            let mutable v = v
            v <- ((v >>> 1) &&& 0x55555555u) ||| ((v &&& 0x55555555u) <<< 1)
            v <- ((v >>> 2) &&& 0x33333333u) ||| ((v &&& 0x33333333u) <<< 2)
            v <- ((v >>> 4) &&& 0x0F0F0F0Fu) ||| ((v &&& 0x0F0F0F0Fu) <<< 4)
            System.Buffers.Binary.BinaryPrimitives.ReverseEndianness v

    /// The product of two polynomials over GF(2), each of degree under 64, as the carry-less multiply
    /// instruction gives it (the low 64 bits of it: callers keep degrees where that's all of it).
    let inline private clmul (a: uint64) (b: uint64) : uint64 =
        if System.Runtime.Intrinsics.Arm.Aes.IsSupported then
            System.Runtime.Intrinsics.Vector128.GetElement(
                System.Runtime.Intrinsics.Arm.Aes.PolynomialMultiplyWideningLower(
                    System.Runtime.Intrinsics.Vector64.CreateScalar a,
                    System.Runtime.Intrinsics.Vector64.CreateScalar b
                ),
                0
            )
        else
            System.Runtime.Intrinsics.Vector128.GetElement(
                System.Runtime.Intrinsics.X86.Pclmulqdq.CarrylessMultiply(
                    System.Runtime.Intrinsics.Vector128.CreateScalar a,
                    System.Runtime.Intrinsics.Vector128.CreateScalar b,
                    0uy
                ),
                0
            )

    /// Whether the CPU has a carry-less multiply for `multiply`.
    let hasClmul = System.Runtime.Intrinsics.Arm.Aes.IsSupported || System.Runtime.Intrinsics.X86.Pclmulqdq.IsSupported

    /// floor(x^64 / P(x)), the constant of Barrett's reduction, by long division.
    let private mu: uint64 =
        let p = UInt128(0UL, 0x1_04C1_1DB7UL)
        let mutable remainder = UInt128.One <<< 64
        let mutable quotient = 0UL
        for shift in 32 .. -1 .. 0 do
            if ((remainder >>> (shift + 32)) &&& UInt128.One) = UInt128.One then
                remainder <- remainder ^^^ (p <<< shift)
                quotient <- quotient ||| (1UL <<< shift)
        quotient

    /// `a` times `b` modulo the polynomial, by carry-less multiplication and Barrett's reduction on
    /// the polynomials in natural order (bit i is the coefficient of x^i, which is gzip's bit order
    /// reversed): a few instructions where `multiplySlowly` takes 32 dependent steps.
    let multiplyFast (a: uint32) (b: uint32) : uint32 =
        let product = clmul (uint64 (reverseBits a)) (uint64 (reverseBits b))
        let quotient = (clmul (product >>> 32) mu) >>> 32
        reverseBits (uint32 product ^^^ uint32 (clmul quotient 0x04C11DB7UL))

    /// `a` times `b` modulo the polynomial.
    let multiply (a: uint32) (b: uint32) : uint32 = if hasClmul then multiplyFast a b else multiplySlowly a b

    /// x^(2^k) modulo the polynomial, for k in 0..31 (zlib's `x2n_table`).
    let private xToThe2ToThe: uint32[] =
        let table = Array.zeroCreate<uint32> 32
        let mutable power = 1u <<< 30 // x^1
        for k in 0..31 do
            table[k] <- power
            power <- multiplySlowly power power
        table

    /// x^(8*n) modulo the polynomial (zlib's `x2nmodp(n, 3)`). x's order divides 2^32 - 1, so
    /// x^(2^32) is x^(2^0).
    let xToThe8 (n: uint64) : uint32 =
        let mutable n = n
        let mutable power = 1u <<< 31 // x^0
        let mutable k = 3
        while n <> 0UL do
            if n &&& 1UL = 1UL then power <- multiplySlowly xToThe2ToThe[k % 32] power
            n <- n >>> 1
            k <- k + 1
        power

    /// The CRC (and shift) of `first` followed by `second`.
    let ofPair (first: ReadOnlySpan<byte>) (second: ReadOnlySpan<byte>) : Crc =
        let crc = Deflater.crc32Update (Deflater.crc32Update 0u first) second
        { Value = crc; Shift = xToThe8 (uint64 first.Length + uint64 second.Length) }

    let ofBytes (bytes: ReadOnlySpan<byte>) : Crc = ofPair bytes ReadOnlySpan<byte>.Empty

    /// The CRC of `crcs`' parts, one after the other.
    let concatenated (crcs: ReadOnlySpan<Crc>) : uint32 =
        let mutable value = 0u
        for crc in crcs do
            value <- multiply value crc.Shift ^^^ crc.Value
        value

// --- What's remembered -------------------------------------------------------------------------

module internal Digests =
    let inline same (a: BodyDigest) (b: BodyDigest) : bool = a.A = b.A && a.B = b.B && a.C = b.C && a.D = b.D

/// What comes right before a part, which its piece may refer back into: the SHA-256 of exactly the
/// bytes it may use (a fragment's own bytes, never its glue, or a text).
[<Struct>]
type internal Before =
    { Kind: int
      Sha: BodyDigest }

module internal Before =
    [<Literal>]
    let NothingKind = 0

    [<Literal>]
    let FragmentKind = 1

    [<Literal>]
    let TextKind = 2

    let nothing: Before = { Kind = NothingKind; Sha = Unchecked.defaultof<BodyDigest> }

/// A part compressed: raw deflate ending on a sync flush, and the part's CRC.
[<Sealed; AllowNullLiteral>]
type internal Piece(deflated: byte[], crc: Crc) =
    member _.Deflated = deflated
    member _.Crc = crc

/// A fragment's piece for one predecessor: the glue and the fragment, compressed.
[<Sealed; AllowNullLiteral>]
type internal FragmentPiece(before: Before, glue: byte[], piece: Piece) =
    member _.Before = before
    member _.Glue = glue
    member _.Piece = piece

/// A fragment seen in a page: its SHA-256, and its pieces for the predecessors it's followed, most
/// recently stored last.
[<Sealed; AllowNullLiteral>]
type internal KnownFragment(sha: BodyDigest, pieces: FragmentPiece[]) =
    member _.Sha = sha
    member _.Pieces = pieces

    member _.PieceAfter(before: Before, glue: ReadOnlySpan<byte>) : Piece =
        let mutable found: Piece = null
        let mutable i = 0
        while isNull found && i < pieces.Length do
            let stored = pieces[i]
            if stored.Before.Kind = before.Kind && Digests.same stored.Before.Sha before.Sha && glue.SequenceEqual(ReadOnlySpan<byte> stored.Glue) then
                found <- stored.Piece
            i <- i + 1
        found

/// A cached HTML fragment (the rendered bytes of one message, say) as the fragment cache hands it
/// out. Its identity is what stored pieces are found by, so a fragment rendered again into a new
/// `Fragment` with the same bytes gets pieces of its own.
[<Sealed; AllowNullLiteral>]
type Fragment(bytes: byte[]) =
    [<VolatileField>]
    let mutable known: KnownFragment = null

    new(text: string) = Fragment(Text.Encoding.UTF8.GetBytes text)

    member _.Bytes: byte[] = bytes
    member _.Memory: ReadOnlyMemory<byte> = ReadOnlyMemory<byte> bytes
    member _.Length = bytes.Length

    member internal _.Known
        with get () = known
        and set (value: KnownFragment) = known <- value

    /// Remembers `value` unless something is remembered already (another request may have hashed the
    /// fragment meanwhile; either answer is the same).
    member internal _.Remember(value: KnownFragment) : unit =
        Interlocked.CompareExchange(&known, value, null) |> ignore

/// A map key that borrows the bytes it was made from, compared and hashed by content: how a page's
/// text finds the SHA-256 remembered for it. Stored keys own a copy.
[<Struct; CustomEquality; NoComparison>]
type internal TextKey =
    { Memory: ReadOnlyMemory<byte> }

    override this.Equals(other: obj | null) =
        match other with
        | :? TextKey as other -> this.Memory.Span.SequenceEqual other.Memory.Span
        | _ -> false

    override this.GetHashCode() = TextKey.Hash this.Memory.Span

    /// A fast, non-cryptographic hash of tens of KB (SHA-256 is what stored pieces are found by; this
    /// only finds the text that was hashed): the CRC-32C instructions on four interleaved lanes where the
    /// CPU has them, four lanes of multiply-and-fold, a word at a time, where it doesn't.
    static member Hash(bytes: ReadOnlySpan<byte>) : int =
        let mutable i = 0
        let mutable tail = 0UL
        let mutable shift = 0
        let mutable result = 0UL
        if System.Runtime.Intrinsics.Arm.Crc32.Arm64.IsSupported then
            let mutable c0 = uint32 bytes.Length
            let mutable c1 = 0x9E3779B9u
            let mutable c2 = 0x85EBCA6Bu
            let mutable c3 = 0xC2B2AE35u
            while i + 32 <= bytes.Length do
                c0 <- System.Runtime.Intrinsics.Arm.Crc32.Arm64.ComputeCrc32C(c0, BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice i))
                c1 <- System.Runtime.Intrinsics.Arm.Crc32.Arm64.ComputeCrc32C(c1, BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(i + 8)))
                c2 <- System.Runtime.Intrinsics.Arm.Crc32.Arm64.ComputeCrc32C(c2, BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(i + 16)))
                c3 <- System.Runtime.Intrinsics.Arm.Crc32.Arm64.ComputeCrc32C(c3, BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(i + 24)))
                i <- i + 32
            while i + 8 <= bytes.Length do
                c0 <- System.Runtime.Intrinsics.Arm.Crc32.Arm64.ComputeCrc32C(c0, BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice i))
                i <- i + 8
            while i < bytes.Length do
                c1 <- System.Runtime.Intrinsics.Arm.Crc32.ComputeCrc32C(c1, bytes[i])
                i <- i + 1
            result <- (uint64 (c0 ^^^ (c2 <<< 13 ||| c2 >>> 19)) <<< 32) ||| uint64 (c1 ^^^ (c3 <<< 7 ||| c3 >>> 25))
        elif System.Runtime.Intrinsics.X86.Sse42.X64.IsSupported then
            let mutable c0 = uint64 bytes.Length
            let mutable c1 = 0x9E3779B9UL
            let mutable c2 = 0x85EBCA6BUL
            let mutable c3 = 0xC2B2AE35UL
            while i + 32 <= bytes.Length do
                c0 <- System.Runtime.Intrinsics.X86.Sse42.X64.Crc32(c0, BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice i))
                c1 <- System.Runtime.Intrinsics.X86.Sse42.X64.Crc32(c1, BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(i + 8)))
                c2 <- System.Runtime.Intrinsics.X86.Sse42.X64.Crc32(c2, BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(i + 16)))
                c3 <- System.Runtime.Intrinsics.X86.Sse42.X64.Crc32(c3, BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(i + 24)))
                i <- i + 32
            while i + 8 <= bytes.Length do
                c0 <- System.Runtime.Intrinsics.X86.Sse42.X64.Crc32(c0, BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice i))
                i <- i + 8
            while i < bytes.Length do
                c1 <- uint64 (System.Runtime.Intrinsics.X86.Sse42.Crc32(uint32 c1, bytes[i]))
                i <- i + 1
            let m0, m1, m2, m3 = uint32 c0, uint32 c1, uint32 c2, uint32 c3
            result <- (uint64 (m0 ^^^ (m2 <<< 13 ||| m2 >>> 19)) <<< 32) ||| uint64 (m1 ^^^ (m3 <<< 7 ||| m3 >>> 25))
        else
            let mutable h0 = 0x9E3779B97F4A7C15UL ^^^ uint64 bytes.Length
            let mutable h1 = 0xC2B2AE3D27D4EB4FUL
            let mutable h2 = 0x165667B19E3779F9UL
            let mutable h3 = 0x85EBCA77C2B2AE63UL
            let inline mix (h: uint64) (w: uint64) =
                let m = (h ^^^ w) * 0x9FB21C651E98DF25UL
                m ^^^ (m >>> 29)
            while i + 32 <= bytes.Length do
                h0 <- mix h0 (BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice i))
                h1 <- mix h1 (BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(i + 8)))
                h2 <- mix h2 (BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(i + 16)))
                h3 <- mix h3 (BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(i + 24)))
                i <- i + 32
            while i + 8 <= bytes.Length do
                h0 <- mix h0 (BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice i))
                i <- i + 8
            while i < bytes.Length do
                tail <- tail ||| (uint64 bytes[i] <<< shift)
                shift <- shift + 8
                i <- i + 1
            h0 <- mix h0 tail
            result <- mix (mix h0 h1) (mix h2 h3)
        int (result ^^^ (result >>> 32))

/// What a remembered text keeps: its SHA-256, and the key that owns the text's bytes.
type internal TextSha = { Sha: BodyDigest; Owned: TextKey }

/// The identity of a text piece: the text, and what comes before it.
[<Struct>]
type internal TextPieceKey = { Text: BodyDigest; Before: Before }

[<Sealed>]
type internal TextPieceKeyComparer() =
    interface IEqualityComparer<TextPieceKey> with
        member _.Equals(a, b) = Digests.same a.Text b.Text && a.Before.Kind = b.Before.Kind && Digests.same a.Before.Sha b.Before.Sha

        member _.GetHashCode key =
            int (key.Text.A ^^^ (key.Before.Sha.A * 31UL) ^^^ uint64 key.Before.Kind)

/// Text bytes as a map key, compared by content.
[<Sealed>]
type internal TextKeyComparer() =
    interface IEqualityComparer<TextKey> with
        member _.Equals(a, b) = a.Memory.Span.SequenceEqual b.Memory.Span
        member _.GetHashCode key = TextKey.Hash key.Memory.Span

module internal SpliceCaches =
    open SpliceLimits

    let textPiecesGate = obj ()

    let textPieces =
        Generations<TextPieceKey, Piece>(
            MaxTextPieceBytes,
            (fun _ piece -> piece.Deflated.Length + CacheCosts.EntryOverhead),
            TextPieceKeyComparer()
        )

    let textShasGate = obj ()

    let textShas =
        Generations<TextKey, TextSha>(
            MaxTextBytes,
            (fun key _ -> key.Memory.Length + CacheCosts.EntryOverhead),
            TextKeyComparer(),
            (fun _ stored -> stored.Owned)
        )

    let private readSha (stored: TextSha) : BodyDigest = stored.Sha

    let private sha256 (bytes: ReadOnlySpan<byte>) : BodyDigest =
        let hash = Span<byte>(Array.zeroCreate<byte> 32)
        SHA256.HashData(bytes, hash) |> ignore
        { A = BinaryPrimitives.ReadUInt64LittleEndian(hash.Slice(0, 8))
          B = BinaryPrimitives.ReadUInt64LittleEndian(hash.Slice(8, 8))
          C = BinaryPrimitives.ReadUInt64LittleEndian(hash.Slice(16, 8))
          D = BinaryPrimitives.ReadUInt64LittleEndian(hash.Slice(24, 8)) }

    /// The texts a page used most recently, in slots picked by a handful of their bytes: a page's texts
    /// are the same from one request to the next, so the text in its slot is nearly always the one asked
    /// about, and comparing them (a vectorized compare, no hashing of tens of KB) is all it takes. A slot
    /// is only a shortcut: a text that isn't in its slot is found in (or added to) `shas` as before.
    /// Bounded by `RecentSlots` and `MaxRecentText`.
    let private recent: TextSha[] = Array.zeroCreate RecentSlots

    let private slotOf (text: ReadOnlySpan<byte>) : int =
        if text.Length < 16 then
            text.Length &&& (RecentSlots - 1)
        else
            let head = BinaryPrimitives.ReadUInt64LittleEndian text
            let tail = BinaryPrimitives.ReadUInt64LittleEndian(text.Slice(text.Length - 8))
            let mixed = (uint64 text.Length * 0x9E3779B97F4A7C15UL) ^^^ (head * 0xC2B2AE3D27D4EB4FUL) ^^^ (tail * 0x165667B19E3779F9UL)
            int (mixed >>> 40) &&& (RecentSlots - 1)

    /// The SHA-256 of `text`, remembered by its bytes in `shas`: a page repeats its texts (a room's
    /// layout) from one request to the next, and finding one again costs a compare with the text in its
    /// slot (or, a fast hash and a compare), a small part of hashing it. The compare is what makes this
    /// sound, since the SHA-256 is what stored pieces are found by: two texts whose fast hashes collide
    /// still get their own.
    let textShaIn (gate: obj) (shas: Generations<TextKey, TextSha>) (text: ReadOnlyMemory<byte>) : BodyDigest =
        let slot = if text.Length <= MaxRecentText && obj.ReferenceEquals(shas, textShas) then slotOf text.Span else -1
        let remembered = if slot >= 0 then recent[slot] else Unchecked.defaultof<TextSha>
        if slot >= 0 && not (isNull (box remembered)) && remembered.Owned.Memory.Span.SequenceEqual text.Span then
            remembered.Sha
        else
            let key = { Memory = text }
            let mutable known = ValueNone
            Threading.Monitor.Enter gate
            try
                known <- shas.Get(key, readSha)
            finally
                Threading.Monitor.Exit gate
            match known with
            | ValueSome sha -> sha
            | ValueNone ->
                let sha = sha256 text.Span
                if text.Length <= MaxStoredText then
                    let owned = { Memory = ReadOnlyMemory<byte>(text.ToArray()) }
                    let entry = { Sha = sha; Owned = owned }
                    lock gate (fun () -> shas.Insert(owned, entry))
                    if slot >= 0 then recent[slot] <- entry
                sha

    let textSha (text: ReadOnlyMemory<byte>) : BodyDigest = textShaIn textShasGate textShas text

    let fragmentSha (fragment: Fragment) : BodyDigest =
        match fragment.Known with
        | null ->
            let sha = sha256 (ReadOnlySpan<byte> fragment.Bytes)
            let known = KnownFragment(sha, Array.empty)
            fragment.Remember known
            sha
        | known -> known.Sha

    /// Stores `piece` for `fragment`, dropping the oldest when there are already `PiecesPerFragment`.
    let storePiece (fragment: Fragment) (piece: FragmentPiece) : unit =
        lock fragment (fun () ->
            let known =
                match fragment.Known with
                | null -> KnownFragment(fragmentSha fragment, Array.empty)
                | known -> known
            let kept =
                known.Pieces
                |> Array.filter (fun stored ->
                    not (stored.Before.Kind = piece.Before.Kind && Digests.same stored.Before.Sha piece.Before.Sha)
                    || not (ReadOnlySpan<byte>(stored.Glue).SequenceEqual(ReadOnlySpan<byte> piece.Glue)))
            let kept = if kept.Length = PiecesPerFragment then kept[1..] else kept
            fragment.Known <- KnownFragment(known.Sha, Array.append kept [| piece |]))

/// Body bytes gathered piece by piece (text, and fragments too small for a part of their own),
/// joined only when there's more than one piece.
[<Struct; NoEquality; NoComparison>]
type internal Gathered =
    val mutable private first: ReadOnlyMemory<byte>
    val mutable private count: int
    val mutable private rest: ResizeArray<ReadOnlyMemory<byte>> | null

    member this.Push(bytes: ReadOnlyMemory<byte>) : unit =
        if not bytes.IsEmpty then
            if this.count = 0 then
                this.first <- bytes
            else
                match this.rest with
                | null ->
                    let list = ResizeArray<ReadOnlyMemory<byte>>(4)
                    list.Add bytes
                    this.rest <- list
                | list -> list.Add bytes
            this.count <- this.count + 1

    member this.Take() : ReadOnlyMemory<byte> =
        let taken =
            match this.count with
            | 0 -> ReadOnlyMemory<byte>.Empty
            | 1 -> this.first
            | _ ->
                let list = nonNull this.rest
                let mutable total = this.first.Length
                for piece in list do
                    total <- total + piece.Length
                let joined = Array.zeroCreate<byte> total
                this.first.Span.CopyTo(Span<byte> joined)
                let mutable at = this.first.Length
                for piece in list do
                    piece.Span.CopyTo(Span<byte>(joined, at, piece.Length))
                    at <- at + piece.Length
                ReadOnlyMemory<byte> joined
        this.first <- ReadOnlyMemory<byte>.Empty
        this.count <- 0
        this.rest <- null
        taken

/// A page's body split at its cached fragments, with each part's identity.
[<Struct; NoEquality; NoComparison>]
type internal SplicePart =
    {
        /// The fragment, or null for a text part.
        Fragment: Fragment
        /// A text part's text, or the text since the previous fragment, which goes out just before this
        /// one.
        Bytes: ReadOnlyMemory<byte>
        Sha: BodyDigest
    }

module internal Compress =
    open SpliceLimits

    /// Raw deflate of `glue` and `text` at level 6 with `dictionary` (its last 32 KB) as what came
    /// before, sync-flushed so it ends on a byte boundary with no final block. .NET's `DeflateStream`
    /// takes no preset dictionary, so the dictionary goes through the stream first (flushed, and its
    /// output dropped): the window then holds it as it would after `deflateSetDictionary`, and the
    /// text's matches reach into it.
    let deflate (dictionary: ReadOnlySpan<byte>) (glue: ReadOnlySpan<byte>) (text: ReadOnlySpan<byte>) : byte[] =
        use output = new MemoryStream(glue.Length / 4 + text.Length / 4 + 64)
        use stream = new DeflateStream(output, CompressionLevel.Optimal, true)
        let mutable skip = 0L
        if dictionary.Length > 0 then
            stream.Write(dictionary.Slice(max 0 (dictionary.Length - Window)))
            stream.Flush()
            skip <- output.Length
        if glue.Length > 0 then stream.Write glue
        stream.Write text
        stream.Flush()
        // Stored pieces live on, so they keep exactly what they use.
        let length = int (output.Length - skip)
        let piece = Array.zeroCreate<byte> length
        Buffer.BlockCopy(output.GetBuffer(), int skip, piece, 0, length)
        piece

/// A page's body split at its cached fragments, with each part's identity.
[<Sealed>]
type PageParts internal (length: int, parts: SplicePart[]) =
    static let isFragment (part: SplicePart) = not (isNull (box part.Fragment))

    [<ThreadStatic; DefaultValue>]
    static val mutable private hashBuffer: byte[] | null

    /// A SHA-256's worth of room for the thread to hash into.
    static member private HashBuffer: byte[] =
        match PageParts.hashBuffer with
        | null ->
            let created = Array.zeroCreate<byte> 32
            PageParts.hashBuffer <- created
            created
        | existing -> existing

    /// The body `text` makes with each of `fragments` (cached HTML, in order) spliced in at its byte
    /// offset in `text`, split into parts; or that body whole (`Error`), when no fragment is big enough
    /// for a part of its own. Raises `ArgumentOutOfRangeException` if the offsets go backwards or past
    /// the end of `text`.
    static member Splice(text: ReadOnlyMemory<byte>, fragments: IReadOnlyList<struct (int * Fragment)>) : Result<PageParts, ReadOnlyMemory<byte>> =
        let count = fragments.Count
        let mutable length = text.Length
        for i in 0 .. count - 1 do
            let struct (_, fragment) = fragments[i]
            length <- length + fragment.Length
        // The text before each fragment big enough for a part, and the fragments themselves.
        let gaps = Array.zeroCreate<ReadOnlyMemory<byte>> count
        let bigs = Array.zeroCreate<Fragment> count
        let mutable runs = 0
        let mutable between = Gathered()
        let mutable position = 0
        for i in 0 .. count - 1 do
            let struct (offset, fragment) = fragments[i]
            between.Push(text.Slice(position, offset - position))
            position <- offset
            if fragment.Length < SpliceLimits.MinFragment then
                between.Push fragment.Memory
            else
                gaps[runs] <- between.Take()
                bigs[runs] <- fragment
                runs <- runs + 1
        between.Push(text.Slice position)
        let rest = between.Take()
        if runs = 0 then
            Error rest
        else
            // A gap is a text part of its own unless it follows a fragment and is short enough to travel
            // with the next: the first gap never follows one.
            let mutable total = runs + (if rest.IsEmpty then 0 else 1)
            for i in 0 .. runs - 1 do
                let gap = gaps[i]
                if not gap.IsEmpty && (i = 0 || gap.Length > SpliceLimits.MaxGlue) then total <- total + 1
            let parts = Array.zeroCreate<SplicePart> total
            let mutable made = 0
            for i in 0 .. runs - 1 do
                let gap = gaps[i]
                let fragment = bigs[i]
                let sha = SpliceCaches.fragmentSha fragment
                let glue =
                    if i > 0 && gap.Length <= SpliceLimits.MaxGlue then
                        gap
                    else
                        if not gap.IsEmpty then
                            parts[made] <- PageParts.TextPart gap
                            made <- made + 1
                        ReadOnlyMemory<byte>.Empty
                parts[made] <- { Fragment = fragment; Bytes = glue; Sha = sha }
                made <- made + 1
            if not rest.IsEmpty then parts[made] <- PageParts.TextPart rest
            Ok(PageParts(length, parts))

    static member private TextPart(text: ReadOnlyMemory<byte>) : SplicePart =
        { Fragment = null
          Bytes = text
          Sha = SpliceCaches.textSha text }

    /// The body's length.
    member _.BodyLength = length

    /// What comes right before part `i`, which its piece may refer back into: the SHA-256 of exactly the
    /// bytes it may use (a fragment's own bytes, never its glue, or a text).
    member private _.BeforeOf(i: int) : Before =
        if i = 0 then
            Before.nothing
        else
            let part = &parts[i - 1]
            { Kind = (if isNull (box part.Fragment) then Before.TextKind else Before.FragmentKind)
              Sha = part.Sha }

    /// The bytes part `i`'s piece may use as a dictionary: what comes before it, a fragment's own bytes
    /// (never its glue) or the text.
    member private _.DictionaryOf(i: int) : ReadOnlyMemory<byte> =
        if i = 0 then
            ReadOnlyMemory<byte>.Empty
        else
            let part = &parts[i - 1]
            match part.Fragment with
            | null -> part.Bytes
            | fragment -> fragment.Memory

    /// The weak ETag's value: 32 hex digits of a SHA-256 over the parts (the same body split the same
    /// way always gets the same one), as `Rack::ETag`'s is of the body.
    member _.Etag() : string =
        let mutable size = 0
        for i in 0 .. parts.Length - 1 do
            let part = &parts[i]
            size <- size + (if isFragment part then 1 + 8 + part.Bytes.Length + 8 + 32 else 1 + 8 + 32)
        let buffer = ArrayPool<byte>.Shared.Rent size
        try
            let mutable at = 0
            for i in 0 .. parts.Length - 1 do
                let part = &parts[i]
                let fragment = part.Fragment
                buffer[at] <- (if isNull (box fragment) then byte 'T' else byte 'F')
                at <- at + 1
                BinaryPrimitives.WriteUInt64LittleEndian(Span<byte>(buffer, at, 8), uint64 part.Bytes.Length)
                at <- at + 8
                if not (isNull (box fragment)) then
                    part.Bytes.Span.CopyTo(Span<byte>(buffer, at, part.Bytes.Length))
                    at <- at + part.Bytes.Length
                    BinaryPrimitives.WriteUInt64LittleEndian(Span<byte>(buffer, at, 8), uint64 fragment.Length)
                    at <- at + 8
                let sha = part.Sha
                BinaryPrimitives.WriteUInt64LittleEndian(Span<byte>(buffer, at, 8), sha.A)
                BinaryPrimitives.WriteUInt64LittleEndian(Span<byte>(buffer, at + 8, 8), sha.B)
                BinaryPrimitives.WriteUInt64LittleEndian(Span<byte>(buffer, at + 16, 8), sha.C)
                BinaryPrimitives.WriteUInt64LittleEndian(Span<byte>(buffer, at + 24, 8), sha.D)
                at <- at + 32
            let hash = PageParts.HashBuffer
            SHA256.HashData(ReadOnlySpan<byte>(buffer, 0, at), Span<byte>(hash)) |> ignore
            Convert.ToHexStringLower(ReadOnlySpan<byte>(hash, 0, 16))
        finally
            ArrayPool<byte>.Shared.Return buffer

    /// Each part's piece: stored ones where they fit, the rest compressed and stored.
    member private this.Pieces() : Piece[] =
        let pieces = Array.zeroCreate<Piece> parts.Length
        let mutable missing = 0
        // Only look up under the lock; compressing happens outside it.
        Threading.Monitor.Enter SpliceCaches.textPiecesGate
        try
            for i in 0 .. parts.Length - 1 do
                let part = &parts[i]
                if isNull (box part.Fragment) then
                    match SpliceCaches.textPieces.Get({ Text = part.Sha; Before = this.BeforeOf i }, id) with
                    | ValueSome piece -> pieces[i] <- piece
                    | ValueNone -> ()
        finally
            Threading.Monitor.Exit SpliceCaches.textPiecesGate
        for i in 0 .. parts.Length - 1 do
            let part = &parts[i]
            match part.Fragment with
            | null -> ()
            | fragment ->
                match fragment.Known with
                | null -> ()
                | known -> pieces[i] <- known.PieceAfter(this.BeforeOf i, part.Bytes.Span)
        for i in 0 .. pieces.Length - 1 do
            if isNull pieces[i] then missing <- missing + 1
        if missing > 0 then
            let newTexts = ResizeArray<struct (TextPieceKey * Piece)>()
            for i in 0 .. parts.Length - 1 do
                if isNull pieces[i] then
                    let part = parts[i]
                    let before = this.BeforeOf i
                    let dictionary = this.DictionaryOf i
                    match part.Fragment with
                    | null ->
                        let deflated = Compress.deflate dictionary.Span ReadOnlySpan<byte>.Empty part.Bytes.Span
                        let piece = Piece(deflated, Crc32.ofBytes part.Bytes.Span)
                        if deflated.Length <= SpliceLimits.MaxStoredTextPiece then
                            newTexts.Add(struct ({ Text = part.Sha; Before = before }, piece))
                        pieces[i] <- piece
                    | fragment ->
                        let deflated = Compress.deflate dictionary.Span part.Bytes.Span (ReadOnlySpan<byte> fragment.Bytes)
                        let piece = Piece(deflated, Crc32.ofPair part.Bytes.Span (ReadOnlySpan<byte> fragment.Bytes))
                        SpliceCaches.storePiece fragment (FragmentPiece(before, part.Bytes.ToArray(), piece))
                        pieces[i] <- piece
            if newTexts.Count > 0 then
                lock SpliceCaches.textPiecesGate (fun () ->
                    for struct (key, piece) in newTexts do
                        SpliceCaches.textPieces.Insert(key, piece))
        pieces

    /// The whole gzip member for the body. `mtime` and the Unix OS code go in the header, as
    /// `Zlib::GzipWriter` writes them.
    member this.Gzip(mtime: uint32) : byte[] =
        let pieces = this.Pieces()
        let mutable size = 20
        for piece in pieces do
            size <- size + piece.Deflated.Length
        let out = Array.zeroCreate<byte> size
        out[0] <- 0x1fuy
        out[1] <- 0x8buy
        out[2] <- 8uy
        BinaryPrimitives.WriteUInt32LittleEndian(Span<byte>(out, 4, 4), mtime)
        out[9] <- 3uy
        let mutable at = 10
        let crcs = Array.zeroCreate<Crc> pieces.Length
        for i in 0 .. pieces.Length - 1 do
            let piece = pieces[i]
            Buffer.BlockCopy(piece.Deflated, 0, out, at, piece.Deflated.Length)
            at <- at + piece.Deflated.Length
            crcs[i] <- piece.Crc
        // An empty final block (fixed Huffman), after the sync flushes that ended every piece.
        out[at] <- 0x03uy
        out[at + 1] <- 0x00uy
        BinaryPrimitives.WriteUInt32LittleEndian(Span<byte>(out, at + 2, 4), Crc32.concatenated (ReadOnlySpan<Crc> crcs))
        BinaryPrimitives.WriteUInt32LittleEndian(Span<byte>(out, at + 6, 4), uint32 length)
        out

    /// The body, part after part, never joined into one buffer.
    member _.WritePlain(writer: IBufferWriter<byte>) : unit =
        for i in 0 .. parts.Length - 1 do
            let part = &parts[i]
            match part.Fragment with
            | null -> writer.Write part.Bytes.Span
            | fragment ->
                if not part.Bytes.IsEmpty then writer.Write part.Bytes.Span
                writer.Write(ReadOnlySpan<byte> fragment.Bytes)

    /// The number of parts, for tests.
    member internal _.PartCount = parts.Length

    /// What a part's piece is stored under, for tests: whether it is a fragment, its glue and its text.
    member internal _.Part(i: int) : SplicePart = parts[i]

    interface IPageParts with
        member this.BodyLength = this.BodyLength
        member this.Etag() = this.Etag()
        member this.WritePlain writer = this.WritePlain writer
        member this.Gzip mtime = ReadOnlyMemory<byte>(this.Gzip mtime)
