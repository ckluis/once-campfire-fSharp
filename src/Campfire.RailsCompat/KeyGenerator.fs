// Port of rust/crates/rails_compat/src/key_generator.rs
namespace Campfire.RailsCompat

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text

/// `Rails.application.key_generator`: an `ActiveSupport::CachingKeyGenerator` over PBKDF2-HMAC.
/// Rails builds it with 1000 iterations (railties `Rails::Application#key_generator`), and
/// `load_defaults` 7.0+ sets its digest to SHA256 (`key_generator_hash_digest_class`). The
/// default key length is 64 bytes; encrypted cookies ask for 32.
type KeyGenerator(secretKeyBase: string) =
    static let iterations = 1000
    let secret = Encoding.UTF8.GetBytes secretKeyBase
    let cache = ConcurrentDictionary<struct (string * int), byte[]>()
    let derive =
        Func<struct (string * int), byte[]>(fun (struct (salt, length)) ->
            Rfc2898DeriveBytes.Pbkdf2(secret, Encoding.UTF8.GetBytes salt, iterations, HashAlgorithmName.SHA256, length))

    static member Iterations = iterations
    static member DefaultKeyLength = 64

    /// The key itself, one array per (salt, length) for the life of the generator: callers must not write to it. The
    /// verifiers and encryptors the request path builds take this one, so they allocate no copy and their keyed HMAC state
    /// (found by this array) is found at once.
    member _.SharedKey(salt: string, length: int) : byte[] = cache.GetOrAdd(struct (salt, length), derive)

    member this.GenerateKey(salt: string, length: int) : byte[] = Array.copy (this.SharedKey(salt, length))
