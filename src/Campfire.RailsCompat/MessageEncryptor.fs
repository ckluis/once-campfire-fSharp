// Port of rust/crates/rails_compat/src/message_encryptor.rs
namespace Campfire.RailsCompat

open System
open System.Security.Cryptography

/// `ActiveSupport::MessageEncryptor` with `aes-256-gcm`, as the encrypted cookie jar builds it:
/// `<base64 ciphertext>--<base64 12-byte IV>--<base64 16-byte auth tag>`, strict Base64, empty
/// auth data, no separate signature.
type MessageEncryptor =
    private
        { Secret: byte[]
          Serializer: Serializer }

module MessageEncryptor =
    [<Literal>]
    let private IvLength = 12

    [<Literal>]
    let private AuthTagLength = 16

    /// Strict Base64 lengths of the IV and auth tag (with padding).
    [<Literal>]
    let private EncodedIvLength = 16

    [<Literal>]
    let private EncodedAuthTagLength = 24

    /// `secret` must be 32 bytes (`keyGenerator.GenerateKey(salt, 32)`).
    let create (secret: byte[]) (serializer: Serializer) : MessageEncryptor =
        if secret.Length <> 32 then
            invalidArg (nameof secret) "aes-256-gcm needs a 32-byte key"
        { Secret = Array.copy secret
          Serializer = serializer }

    /// `create` for a key nobody else writes to (`KeyGenerator.SharedKey`): no copy, and the expanded key is found by the array.
    let internal createShared (secret: byte[]) (serializer: Serializer) : MessageEncryptor =
        if secret.Length <> 32 then
            invalidArg (nameof secret) "aes-256-gcm needs a 32-byte key"
        { Secret = secret
          Serializer = serializer }

    let internal encryptWithIv (encryptor: MessageEncryptor) (plaintext: byte[]) (iv: byte[]) : string =
        let cipher = (KeyedCrypto.keyed encryptor.Secret).Aes.Instance
        let ciphertext = Array.zeroCreate<byte> plaintext.Length
        let tag = Array.zeroCreate<byte> AuthTagLength
        cipher.Encrypt(iv, plaintext, ciphertext, tag, System.ReadOnlySpan<byte>.Empty)
        [ ciphertext; iv; tag ] |> List.map RailsEncoding.strictEncode |> String.concat "--"

    let encryptAndSign (encryptor: MessageEncryptor) (value: Value) (purpose: string option) (expiresAt: Timestamp option) : string =
        let plaintext = Metadata.serializeWithMetadata encryptor.Serializer value purpose expiresAt
        encryptWithIv encryptor plaintext (RandomNumberGenerator.GetBytes IvLength)

    /// The decrypted bytes, before any envelope handling. `extract_parts`: fixed-length IV and tag at the end, each preceded by `--`.
    let decrypt (encryptor: MessageEncryptor) (message: string) : byte[] option =
        let tagStart = message.Length - EncodedAuthTagLength
        let ivStart = tagStart - (2 + EncodedIvLength)
        let ciphertextEnd = ivStart - 2
        if tagStart < 0 || ivStart < 0 || ciphertextEnd < 0 then
            None
        elif not (message.AsSpan(tagStart - 2, 2).SequenceEqual "--") || not (message.AsSpan(ciphertextEnd, 2).SequenceEqual "--") then
            None
        else
            match
                RailsEncoding.strictDecodeSpan (message.AsSpan(0, ciphertextEnd)),
                RailsEncoding.strictDecodeSpan (message.AsSpan(ivStart, tagStart - 2 - ivStart)),
                RailsEncoding.strictDecodeSpan (message.AsSpan tagStart)
            with
            | ValueSome ciphertext, ValueSome iv, ValueSome tag when iv.Length = IvLength && tag.Length = AuthTagLength ->
                try
                    let cipher = (KeyedCrypto.keyed encryptor.Secret).Aes.Instance
                    let plaintext = Array.zeroCreate<byte> ciphertext.Length
                    cipher.Decrypt(iv, ciphertext, tag, plaintext, System.ReadOnlySpan<byte>.Empty)
                    Some plaintext
                with :? CryptographicException ->
                    None
            | _ -> None

    let decryptAndVerify (encryptor: MessageEncryptor) (message: string) (purpose: string option) (now: Timestamp) : Result<Value, Error> =
        match decrypt encryptor message with
        | None -> Error InvalidSignature
        | Some plaintext -> Metadata.deserializeWithMetadata encryptor.Serializer plaintext purpose now RailsEncoding.strictDecode
