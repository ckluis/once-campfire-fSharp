// Port of rust/crates/campfire/src/integrations/web_push/encryption.rs
//
// `WebPush::Encryption.encrypt` (web-push 3.1.0): RFC 8291 message encryption with the RFC 8188 `aes128gcm` content
// coding, as the gem lays it out: a single record whose size field is the ciphertext length, and a plaintext padded
// with the 0x02 delimiter and one zero.
namespace Campfire.App.Integrations

open System
open System.Numerics
open System.Security.Cryptography
open Campfire.RailsCompat

type EncryptionError =
    /// `ArgumentError`: a blank argument, bad Base64, or a payload over 4096 bytes.
    | Argument of string
    /// `OpenSSL::PKey::EC::Point::Error` and friends: the subscription's key isn't a P-256 point.
    | InvalidKey of string

module EncryptionError =
    let message (error: EncryptionError) : string =
        match error with
        | Argument message
        | InvalidKey message -> message

module WebPushEncoding =
    /// `WebPush.decode64` (`Base64.urlsafe_decode64`): either alphabet, padding optional.
    let decode64 (value: string) : Result<byte[], EncryptionError> =
        match RailsEncoding.urlsafeDecode value with
        | Some bytes -> Ok bytes
        | None -> Error(Argument "invalid base64")

    /// `trim_encode64`: urlsafe Base64 without padding.
    let encode64Nopad (bytes: byte[]) : string = RailsEncoding.urlsafeEncodeUnpadded bytes

module P256 =
    let private p = BigInteger.Parse("115792089210356248762697446949407573530086143415290314195533631308867097853951")
    let private b = BigInteger.Parse("41058363725152142129326129780047268409114441015993725554835256314039467401291")

    let private modPow (value: BigInteger) (exponent: BigInteger) = BigInteger.ModPow(value, exponent, p)

    let private fixed32 (value: BigInteger) : byte[] =
        let bytes = value.ToByteArray(true, true)
        Array.append (Array.zeroCreate (32 - bytes.Length)) bytes

    /// `PublicKey::from_sec1_bytes`: an uncompressed (65 bytes) or compressed (33 bytes) point on the curve.
    let parsePoint (bytes: byte[]) : Result<ECPoint, string> =
        let fail = Error "invalid encoding"
        if bytes.Length = 65 && bytes[0] = 4uy then
            Ok(ECPoint(X = bytes[1..32], Y = bytes[33..64]))
        elif bytes.Length = 33 && (bytes[0] = 2uy || bytes[0] = 3uy) then
            let x = BigInteger(ReadOnlySpan bytes[1..], true, true)
            if x >= p then
                fail
            else
                let rhs = (modPow x (BigInteger 3) - BigInteger 3 * x + b) % p
                let rhs = if rhs.Sign < 0 then rhs + p else rhs
                let y = modPow rhs ((p + BigInteger.One) / BigInteger 4)
                if modPow y (BigInteger 2) <> rhs then
                    fail
                else
                    let wantOdd = bytes[0] = 3uy
                    let y = if y.IsEven = wantOdd then p - y else y
                    Ok(ECPoint(X = bytes[1..], Y = fixed32 y))
        else
            fail

    /// A key pair as an ECDH key, from the private scalar and the public point it belongs to.
    let keyOf (scalar: byte[]) (q: ECPoint) : ECDiffieHellman =
        ECDiffieHellman.Create(ECParameters(Curve = ECCurve.NamedCurves.nistP256, D = scalar, Q = q))

    /// The key's public point as an uncompressed SEC1 point.
    let uncompressed (key: ECDiffieHellman) : byte[] =
        let q = key.ExportParameters(false).Q
        Array.concat [ [| 4uy |]; nonNull q.X; nonNull q.Y ]

module Encryption =
    /// How the record is framed: the gem's, or RFC 8291's example (for its test vector).
    type Layout =
        {
            /// `None`: the ciphertext length, as the gem writes it.
            RecordSize: uint32 option
            Padding: byte[]
        }

    let gemLayout: Layout = { RecordSize = None; Padding = [| 2uy; 0uy |] }

    let private hkdf (salt: byte[]) (ikm: byte[]) (info: byte[]) (length: int) : byte[] =
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length, salt, info)

    let private stripLeadingZeros (bytes: byte[]) : byte[] =
        let zeros = bytes |> Array.takeWhile (fun b -> b = 0uy) |> Array.length
        bytes[zeros..]

    let private blank (value: string option) : bool = value |> Option.forall (fun v -> v = "")

    let encryptWith
        (message: byte[])
        (p256dh: string option)
        (auth: string option)
        (serverKey: ECDiffieHellman)
        (salt: byte[])
        (layout: Layout)
        : Result<byte[], EncryptionError> =
        if message.Length = 0 then
            Error(Argument "message cannot be blank")
        elif blank p256dh then
            Error(Argument "p256dh cannot be blank")
        elif blank auth then
            Error(Argument "auth cannot be blank")
        else
            match WebPushEncoding.decode64 p256dh.Value, WebPushEncoding.decode64 auth.Value with
            | Error error, _
            | _, Error error -> Error error
            | Ok clientPublic, Ok auth ->
                // OpenSSL::BN.new(bytes, 2) drops leading zero bytes before the point is decoded
                let clientPublicBytes = stripLeadingZeros clientPublic
                match P256.parsePoint clientPublicBytes with
                | Error message -> Error(InvalidKey message)
                | Ok point ->
                    try
                        use clientKey = ECDiffieHellman.Create(ECParameters(Curve = ECCurve.NamedCurves.nistP256, Q = point))
                        let sharedSecret = serverKey.DeriveRawSecretAgreement clientKey.PublicKey
                        let serverPublic = P256.uncompressed serverKey
                        let info = Array.concat [ Text.Encoding.ASCII.GetBytes "WebPush: info\000"; clientPublicBytes; serverPublic ]
                        let prk = hkdf auth sharedSecret info 32
                        let contentEncryptionKey = hkdf salt prk (Text.Encoding.ASCII.GetBytes "Content-Encoding: aes128gcm\000") 16
                        let nonce = hkdf salt prk (Text.Encoding.ASCII.GetBytes "Content-Encoding: nonce\000") 12
                        let plaintext = Array.append message layout.Padding
                        let ciphertext = Array.zeroCreate<byte> plaintext.Length
                        let tag = Array.zeroCreate<byte> 16
                        use cipher = new AesGcm(contentEncryptionKey, 16)
                        cipher.Encrypt(nonce, plaintext, ciphertext, tag)
                        let sealed' = Array.append ciphertext tag
                        let recordSize = sealed'.Length
                        if recordSize > 4096 then
                            Error(Argument "encrypted payload is too big")
                        else
                            let size = defaultArg layout.RecordSize (uint32 recordSize)
                            Ok(
                                Array.concat
                                    [ salt
                                      [| byte (size >>> 24); byte (size >>> 16); byte (size >>> 8); byte size |]
                                      [| byte serverPublic.Length |]
                                      serverPublic
                                      sealed' ]
                            )
                    with :? CryptographicException ->
                        Error(InvalidKey "invalid encoding")

    /// Encrypts `message` for the subscription's `p256dh` key and `auth` secret (both urlsafe Base64), with a fresh
    /// server key and salt.
    let encrypt (message: byte[]) (p256dh: string option) (auth: string option) : Result<byte[], EncryptionError> =
        use serverKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)
        encryptWith message p256dh auth serverKey (RandomNumberGenerator.GetBytes 16) gemLayout
