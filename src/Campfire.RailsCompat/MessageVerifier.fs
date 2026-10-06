// Port of rust/crates/rails_compat/src/message_verifier.rs
namespace Campfire.RailsCompat

open System
open System.Security.Cryptography
open System.Text

[<RequireQualifiedAccess>]
type Digest =
    | Sha1
    | Sha256

/// How the payload is Base64-encoded when generating. Reading is lenient in every case:
/// `MessageVerifier#decode` retries with the other alphabet, and `GlobalID::Verifier` uses
/// `urlsafe_decode64`, which accepts both alphabets.
[<RequireQualifiedAccess>]
type Encoding =
    /// `Base64.strict_encode64` (the default, `url_safe: false`).
    | Strict
    /// `url_safe: true`: URL-safe alphabet without padding.
    | UrlSafe
    /// `GlobalID::Verifier`: URL-safe alphabet *with* padding.
    | UrlSafePadded

/// `ActiveSupport::MessageVerifier`: `<base64 payload>--<hex HMAC of the base64 payload>`.
/// Each use in Rails configures it differently; the constructors in the other modules name them.
type MessageVerifier =
    { Secret: byte[]
      Digest: Digest
      Encoding: Encoding
      Serializer: Serializer
      /// `rotate`/`fall_back_to`: tried in order when this verifier can't read a message.
      Rotations: MessageVerifier list }

module MessageVerifier =
    let create (secret: byte[]) (digest: Digest) (encoding: Encoding) (serializer: Serializer) : MessageVerifier =
        { Secret = secret
          Digest = digest
          Encoding = encoding
          Serializer = serializer
          Rotations = [] }

    let fallBackTo (rotation: MessageVerifier) (verifier: MessageVerifier) : MessageVerifier =
        { verifier with Rotations = verifier.Rotations @ [ rotation ] }

    let private hexLength (verifier: MessageVerifier) : int =
        match verifier.Digest with
        | Digest.Sha1 -> 40
        | Digest.Sha256 -> 64

    /// What a thread signs and verifies in: the UTF-8 of the text being MACed and the MAC. Never held across a call into other code.
    [<Sealed; AbstractClass>]
    type private Scratch =
        [<ThreadStatic; DefaultValue>]
        static val mutable private text: byte[] | null

        [<ThreadStatic; DefaultValue>]
        static val mutable private mac: byte[] | null

        static member Mac: byte[] =
            match Scratch.mac with
            | null ->
                let made = Array.zeroCreate<byte> 64
                Scratch.mac <- made
                made
            | made -> made

        /// A buffer of at least `size` bytes; one over 64 KiB is not kept.
        static member Text(size: int) : byte[] =
            match Scratch.text with
            | null ->
                let made = Array.zeroCreate<byte> (max size 1024)
                if made.Length <= 65536 then Scratch.text <- made
                made
            | kept when kept.Length >= size -> kept
            | _ ->
                let made = Array.zeroCreate<byte> (max size 1024)
                if made.Length <= 65536 then Scratch.text <- made
                made

    /// The HMAC of the UTF-8 of `data` (`OpenSSL::HMAC.hexdigest` before the hex), in `Scratch.Mac`; its length.
    let private mac (verifier: MessageVerifier) (data: ReadOnlySpan<char>) : int =
        let buffer = Scratch.Text(System.Text.Encoding.UTF8.GetMaxByteCount data.Length)
        let length = System.Text.Encoding.UTF8.GetBytes(data, Span buffer)
        let keyed = KeyedCrypto.keyed verifier.Secret
        let hmac =
            match verifier.Digest with
            | Digest.Sha1 -> keyed.Sha1
            | Digest.Sha256 -> keyed.Sha256
        hmac.Compute(ReadOnlySpan(buffer, 0, length), Span Scratch.Mac)

    let private hexDigits = "0123456789abcdef"

    /// `digest` is the lowercase hex of the MAC, compared in constant time (a length difference leaks, the contents don't).
    let private digestMatches (macLength: int) (digest: ReadOnlySpan<char>) : bool =
        if digest.Length <> macLength * 2 then
            false
        else
            let mac = Scratch.Mac
            let mutable diff = 0
            for i in 0 .. macLength - 1 do
                let b = int mac[i]
                diff <- diff ||| (int digest[2 * i] ^^^ int hexDigits[b >>> 4]) ||| (int digest[2 * i + 1] ^^^ int hexDigits[b &&& 15])
            diff = 0

    let private isBlankSpan (s: ReadOnlySpan<char>) : bool =
        let mutable blank = true
        let mutable i = 0
        while blank && i < s.Length do
            let c = s[i]
            blank <-
                (c >= '\t' && c <= '\r')
                || c = ' '
                || c = '\u0085'
                || c = '\u00a0'
                || c = '\u1680'
                || (c >= '\u2000' && c <= '\u200a')
                || c = '\u2028'
                || c = '\u2029'
                || c = '\u202f'
                || c = '\u205f'
                || c = '\u3000'
            i <- i + 1
        blank

    /// `extract_encoded`: the digest is the last `2 * digest_length` characters, preceded by `--`. The length of the
    /// encoded part when the digest is there and checks out, otherwise -1.
    let private extractEncoded (verifier: MessageVerifier) (signed: string) : int =
        let index = signed.Length - (hexLength verifier + 2)
        if index < 0 || signed[index] <> '-' || signed[index + 1] <> '-' then
            -1
        else
            let encoded = signed.AsSpan(0, index)
            let digest = signed.AsSpan(index + 2)
            // `data.present? && digest.present?`
            if isBlankSpan encoded || isBlankSpan digest then -1
            elif digestMatches (mac verifier encoded) digest then index
            else -1

    let private sign (verifier: MessageVerifier) (serialized: byte[]) : string =
        let encoded =
            match verifier.Encoding with
            | Encoding.Strict -> RailsEncoding.strictEncode serialized
            | Encoding.UrlSafe -> RailsEncoding.urlsafeEncodeUnpadded serialized
            | Encoding.UrlSafePadded -> RailsEncoding.urlsafeEncodePadded serialized
        let length = mac verifier (encoded.AsSpan())
        String.Concat(encoded, "--", Convert.ToHexStringLower(ReadOnlySpan(Scratch.Mac, 0, length)))

    let generate (verifier: MessageVerifier) (value: Value) (purpose: string option) (expiresAt: Timestamp option) : string =
        sign verifier (Metadata.serializeWithMetadata verifier.Serializer value purpose expiresAt)

    /// `generate` for data the caller already encoded as JSON (in this verifier's serializer, e.g.
    /// `ActiveSupport::JSON` escaping for app verifiers), so key order is under the caller's
    /// control: `{"_rails":{"data":<data_json>,"exp":..,"pur":..}}`.
    let generateRaw (verifier: MessageVerifier) (dataJson: string) (purpose: string option) (expiresAt: Timestamp option) : string =
        sign verifier (Metadata.serializeDumpedWithMetadata verifier.Serializer (System.Text.Encoding.UTF8.GetBytes dataJson) purpose expiresAt)

    let private readMessage (verifier: MessageVerifier) (message: string) (purpose: string option) (now: Timestamp) : Result<Value, Error> =
        let length = extractEncoded verifier message
        if length < 0 then
            Error InvalidSignature
        else
            match RailsEncoding.urlsafeDecodeSpan (message.AsSpan(0, length)) with
            | ValueNone -> Error InvalidSignature
            | ValueSome decoded -> Metadata.deserializeWithMetadata verifier.Serializer decoded purpose now RailsEncoding.urlsafeDecode

    /// `verified`/`verify`: the value, or why it couldn't be read.
    let verify (verifier: MessageVerifier) (message: string) (purpose: string option) (now: Timestamp) : Result<Value, Error> =
        match readMessage verifier message purpose now with
        | Error error when Error.rotates error ->
            let rec tryRotations (rotations: MessageVerifier list) =
                match rotations with
                | [] -> Error error
                | rotation :: rest ->
                    match readMessage rotation message purpose now with
                    | Error e when Error.rotates e -> tryRotations rest
                    | result -> result
            tryRotations verifier.Rotations
        | result -> result

    /// `verified`, returning the data re-encoded as JSON in this verifier's serializer. Key order
    /// is kept; escapes and number formatting are normalized, which only matters if the caller
    /// re-signs the returned string.
    let verifyRaw (verifier: MessageVerifier) (message: string) (purpose: string option) (now: Timestamp) : Result<string, Error> =
        verify verifier message purpose now |> Result.map (Serializer.encodeJson verifier.Serializer)
