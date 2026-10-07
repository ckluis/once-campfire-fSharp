// Port of rust/crates/campfire/src/integrations/web_push/vapid.rs
//
// VAPID identification (RFC 8292) as `WebPush::Request#build_vapid_header` writes it: `vapid t=<ES256 JWT>,k=<public
// key>`, the JWT carrying `aud`, `exp` (12 hours out) and `sub`.
namespace Campfire.App.Integrations

open System
open System.Security.Cryptography
open System.Text
open Campfire.App
open Campfire.RailsCompat

type VapidError =
    | Missing
    | InvalidPublicKey
    | InvalidPrivateKey
    | Mismatched

module VapidError =
    let message (error: VapidError) : string =
        match error with
        | Missing -> "VAPID_PUBLIC_KEY and VAPID_PRIVATE_KEY aren't set"
        | InvalidPublicKey -> "VAPID_PUBLIC_KEY isn't a Base64 P-256 public key"
        | InvalidPrivateKey -> "VAPID_PRIVATE_KEY isn't a Base64 P-256 private key"
        | Mismatched -> "VAPID_PUBLIC_KEY isn't the public key of VAPID_PRIVATE_KEY"

/// `WebPush::Notification#vapid_identification`: the subject and `Rails.configuration.x.vapid`
/// (`VAPID_PUBLIC_KEY`/`VAPID_PRIVATE_KEY`), parsed once at boot so that a bad key turns Web Push off rather than
/// failing (and being blamed on) each subscription.
[<Sealed>]
type VapidConfig private (subject: string, signingKey: ECDsa, publicKey: byte[]) =
    /// `WebPush::Request#expiration`
    static let expirationSeconds = 12L * 60L * 60L

    /// P-256's group order: a private scalar is valid from 1 up to one below it.
    static let order =
        [| 0xFFuy; 0xFFuy; 0xFFuy; 0xFFuy; 0x00uy; 0x00uy; 0x00uy; 0x00uy; 0xFFuy; 0xFFuy; 0xFFuy; 0xFFuy; 0xFFuy; 0xFFuy; 0xFFuy; 0xFFuy
           0xBCuy; 0xE6uy; 0xFAuy; 0xADuy; 0xA7uy; 0x17uy; 0x9Euy; 0x84uy; 0xF3uy; 0xB9uy; 0xCAuy; 0xC2uy; 0xFCuy; 0x63uy; 0x25uy; 0x51uy |]

    static member private ScalarInRange(scalar: byte[]) : bool =
        scalar |> Array.exists (fun b -> b <> 0uy) && ReadOnlySpan<byte>(scalar).SequenceCompareTo(ReadOnlySpan<byte> order) < 0

    /// `VapidKey.from_keys(public_key, private_key)`: the private scalar signs; the public key is sent as given, so
    /// it must be the private key's.
    static member Create(subject: string, publicKey: string, privateKey: string) : Result<VapidConfig, VapidError> =
        match WebPushEncoding.decode64 publicKey with
        | Error _ -> Error InvalidPublicKey
        | Ok publicKey ->
            match P256.parsePoint publicKey with
            | Error _ -> Error InvalidPublicKey
            | Ok point ->
                match WebPushEncoding.decode64 privateKey with
                | Error _ -> Error InvalidPrivateKey
                | Ok privateKey when privateKey.Length > 32 -> Error InvalidPrivateKey
                | Ok privateKey ->
                    let scalar = Array.append (Array.zeroCreate (32 - privateKey.Length)) privateKey
                    if not (VapidConfig.ScalarInRange scalar) then
                        Error InvalidPrivateKey
                    else
                        try
                            let signingKey = ECDsa.Create(ECParameters(Curve = ECCurve.NamedCurves.nistP256, D = scalar, Q = point))
                            // The point has to be the scalar's: a signature the public key alone can check says so (and
                            // some platforms refuse the pair when they import it).
                            use verifying = ECDsa.Create(ECParameters(Curve = ECCurve.NamedCurves.nistP256, Q = point))
                            let probe = Encoding.ASCII.GetBytes "vapid"
                            let signature = signingKey.SignData(probe, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                            if verifying.VerifyData(probe, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation) then
                                Ok(VapidConfig(subject, signingKey, publicKey))
                            else
                                signingKey.Dispose()
                                Error Mismatched
                        with :? CryptographicException ->
                            Error Mismatched

    static member FromConfig(config: AppConfig) : Result<VapidConfig, VapidError> =
        match config.VapidPublicKey, config.VapidPrivateKey with
        | Some publicKey, Some privateKey -> VapidConfig.Create(config.VapidSubject, publicKey, privateKey)
        | _ -> Error Missing

    /// The `Authorization` header for a push service at `audience` (`scheme://host`).
    member _.Authorization(audience: string, now: int64) : string =
        let header = """{"typ":"JWT","alg":"ES256"}"""
        let claims =
            Json.generate (
                Value.Object [ "aud", Value.String audience; "exp", Value.Int(now + expirationSeconds); "sub", Value.String subject ]
            )
        let signingInput =
            $"{WebPushEncoding.encode64Nopad (Encoding.UTF8.GetBytes header)}.{WebPushEncoding.encode64Nopad (Encoding.UTF8.GetBytes claims)}"
        let signature =
            lock signingKey (fun () ->
                signingKey.SignData(Encoding.UTF8.GetBytes signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        $"vapid t={signingInput}.{WebPushEncoding.encode64Nopad signature},k={WebPushEncoding.encode64Nopad publicKey}"
