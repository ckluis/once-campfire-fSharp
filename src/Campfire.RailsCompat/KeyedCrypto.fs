// No Rust counterpart: Rust's `hmac` and `aes-gcm` crates clone a keyed state; the CLR's one-shot `HMACSHA1.HashData` sets up an EVP
// context and hashes the key's pads on every call (about 0.8 us of the 1 us a signature costs), and `new AesGcm` expands the key (0.6 us
// of a 1 us decrypt), so a keyed state is kept per thread.
namespace Campfire.RailsCompat

open System
open System.Runtime.CompilerServices
open System.Security.Cryptography
open System.Threading

/// An HMAC with one key and one digest, whose keyed state is reused: `IncrementalHash.GetHashAndReset` returns to the state
/// after the key, so a call is the message's compression and nothing else. The state isn't thread-safe, so each thread has its own.
[<Sealed>]
type internal KeyedHmac(algorithm: HashAlgorithmName, key: byte[]) =
    let local = new ThreadLocal<IncrementalHash>(fun () -> IncrementalHash.CreateHMAC(algorithm, key))

    /// Writes the MAC of `data` to `destination` (at least 32 bytes for SHA-256, 20 for SHA-1) and returns its length.
    member _.Compute(data: ReadOnlySpan<byte>, destination: Span<byte>) : int =
        let hash = local.Value
        hash.AppendData data
        hash.GetHashAndReset destination

/// An AES-256-GCM key whose expanded state is reused. `AesGcm` isn't safe to share between threads, so each thread has its own.
[<Sealed>]
type internal KeyedAes(key: byte[]) =
    let local = new ThreadLocal<AesGcm>(fun () -> new AesGcm(key, 16))
    member _.Instance: AesGcm = local.Value

[<Sealed>]
type internal KeyedHmacs(key: byte[]) =
    // The per-thread states are made on first use, so building both costs two small objects.
    let sha1 = KeyedHmac(HashAlgorithmName.SHA1, key)
    let sha256 = KeyedHmac(HashAlgorithmName.SHA256, key)

    member _.Matches(other: byte[]) : bool = ReadOnlySpan(key).SequenceEqual(ReadOnlySpan other)
    member _.Sha1: KeyedHmac = sha1
    member _.Sha256: KeyedHmac = sha256
    member val Aes = KeyedAes(key)

module internal KeyedCrypto =
    // Keyed by the secret's array, which `KeyGenerator.SharedKey` hands out as one instance per (salt, length); a copy of the key is
    // kept and compared, so an array someone overwrites later gets a new state rather than a stale one.
    let private states = ConditionalWeakTable<byte[], KeyedHmacs>()

    let keyed (secret: byte[]) : KeyedHmacs =
        match states.TryGetValue secret with
        | true, found when found.Matches secret -> found
        | _ ->
            let made = KeyedHmacs(Array.copy secret)
            states.AddOrUpdate(secret, made)
            made
