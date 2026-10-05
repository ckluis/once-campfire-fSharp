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

    /// `ActiveSupport::SecurityUtils.secure_compare`-style comparison (length leaks, contents don't).
    let internal constantTimeEq (a: byte[]) (b: byte[]) : bool = CryptographicOperations.FixedTimeEquals(ReadOnlySpan a, ReadOnlySpan b)

    let private hexLength (verifier: MessageVerifier) : int =
        match verifier.Digest with
        | Digest.Sha1 -> 40
        | Digest.Sha256 -> 64

    let private mac (verifier: MessageVerifier) (data: string) : byte[] =
        let bytes = System.Text.Encoding.UTF8.GetBytes data
        match verifier.Digest with
        | Digest.Sha1 -> HMACSHA1.HashData(verifier.Secret, bytes)
        | Digest.Sha256 -> HMACSHA256.HashData(verifier.Secret, bytes)

    let private hexDigest (verifier: MessageVerifier) (data: string) : string = Convert.ToHexStringLower(mac verifier data)

    let private digestMatches (verifier: MessageVerifier) (data: string) (digest: string) : bool =
        constantTimeEq (System.Text.Encoding.UTF8.GetBytes digest) (System.Text.Encoding.UTF8.GetBytes(hexDigest verifier data))

    /// Rust's `str::trim().is_empty()`: only Unicode White_Space.
    let private isBlank (s: string) : bool =
        s
        |> Seq.forall (fun c ->
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
            || c = '\u3000')

    /// `extract_encoded`: the digest is the last `2 * digest_length` characters, preceded by `--`.
    let private extractEncoded (verifier: MessageVerifier) (signed: string) : string option =
        let index = signed.Length - (hexLength verifier + 2)
        if index < 0 || signed[index] <> '-' || signed[index + 1] <> '-' then
            None
        else
            let encoded = signed.Substring(0, index)
            let digest = signed.Substring(index + 2)
            // `data.present? && digest.present?`
            if isBlank encoded || isBlank digest then None
            elif digestMatches verifier encoded digest then Some encoded
            else None

    let private sign (verifier: MessageVerifier) (serialized: byte[]) : string =
        let encoded =
            match verifier.Encoding with
            | Encoding.Strict -> RailsEncoding.strictEncode serialized
            | Encoding.UrlSafe -> RailsEncoding.urlsafeEncodeUnpadded serialized
            | Encoding.UrlSafePadded -> RailsEncoding.urlsafeEncodePadded serialized
        encoded + "--" + hexDigest verifier encoded

    let generate (verifier: MessageVerifier) (value: Value) (purpose: string option) (expiresAt: Timestamp option) : string =
        sign verifier (Metadata.serializeWithMetadata verifier.Serializer value purpose expiresAt)

    /// `generate` for data the caller already encoded as JSON (in this verifier's serializer, e.g.
    /// `ActiveSupport::JSON` escaping for app verifiers), so key order is under the caller's
    /// control: `{"_rails":{"data":<data_json>,"exp":..,"pur":..}}`.
    let generateRaw (verifier: MessageVerifier) (dataJson: string) (purpose: string option) (expiresAt: Timestamp option) : string =
        sign verifier (Metadata.serializeDumpedWithMetadata verifier.Serializer (System.Text.Encoding.UTF8.GetBytes dataJson) purpose expiresAt)

    let private readMessage (verifier: MessageVerifier) (message: string) (purpose: string option) (now: Timestamp) : Result<Value, Error> =
        match extractEncoded verifier message |> Option.bind (fun encoded -> RailsEncoding.urlsafeDecode encoded) with
        | None -> Error InvalidSignature
        | Some decoded -> Metadata.deserializeWithMetadata verifier.Serializer decoded purpose now RailsEncoding.urlsafeDecode

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
