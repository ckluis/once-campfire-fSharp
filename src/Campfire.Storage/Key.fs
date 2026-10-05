// Port of rust/crates/storage/src/key.rs
/// Blob keys and checksums.
module Campfire.Storage.Key

open System
open System.IO
open System.Security.Cryptography

/// `ActiveStorage::Blob::MINIMUM_TOKEN_LENGTH`.
[<Literal>]
let KeyLength = 28

let private base36Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz"

/// `SecureRandom.base36(28)` (`has_secure_token :key, length: 28`).
let generateKey () : string =
    String(Array.init KeyLength (fun _ -> base36Alphabet[RandomNumberGenerator.GetInt32 36]))

/// `OpenSSL::Digest::MD5.base64digest` of the whole content (`compute_checksum_in_chunks`).
let checksum (data: byte[]) : string = Convert.ToBase64String(MD5.HashData data)

/// Streaming variant of [`checksum`] for files on disk. Raises `IOException` as `io::Result` fails.
let checksumFile (path: string) : string =
    use file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete, 1 <<< 20)
    Convert.ToBase64String(MD5.HashData file)
