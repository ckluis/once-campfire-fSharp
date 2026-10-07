// Tier 1 micro-benchmark of signed cookies, tokens and key derivation (Phase 7, unit 7.3).
//
//   dotnet run -c Release --project bench/crypto-micro -- [SECONDS_PER_SCENARIO]
//
// Each scenario is one call a page request makes: reading `cookies.signed[:session_token]`, decrypting the session cookie, the Turbo
// stream names of the room page and sidebar, and an avatar token (one per user a page shows). It prints best-of-5 ns per call and bytes
// allocated per call after a warm-up long enough for tiered compilation, and a checksum of every produced string so two builds can be
// checked to have produced the same bytes. The numbers compare two builds on this machine and are never reported as results.
module CryptoMicro

open System
open System.Diagnostics
open System.Security.Cryptography
open Campfire.RailsCompat

let private time (seconds: float) (f: unit -> unit) : float * float =
    let warm = Stopwatch.StartNew()
    while warm.Elapsed.TotalSeconds < 2.0 do
        for _ in 1..100 do f ()
    let mutable best = Double.MaxValue
    let mutable alloc = 0.0
    for _ in 1..5 do
        let a0 = GC.GetAllocatedBytesForCurrentThread()
        let sw = Stopwatch.StartNew()
        let mutable n = 0
        while sw.Elapsed.TotalSeconds < seconds / 5.0 do
            for _ in 1..50 do f ()
            n <- n + 50
        let ns = sw.Elapsed.TotalMilliseconds * 1e6 / float n
        if ns < best then
            best <- ns
            alloc <- float (GC.GetAllocatedBytesForCurrentThread() - a0) / float n
    best, alloc

[<EntryPoint>]
let main argv =
    let seconds = if argv.Length > 0 then float argv[0] else 3.0
    // An optional second argument keeps only the scenarios whose names contain it: a scenario's tiering depends on what ran before it,
    // so two builds are compared on the same filter.
    let filter = if argv.Length > 1 then argv[1] else ""
    let secrets = Secrets.create "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    let now = DateTimeOffset.Parse "2026-10-06T12:00:00Z"
    let sessionToken = "9fQ3mXx0aZ1b2c3d4e5f6g7h"
    let signedToken = Cookies.sign secrets "session_token" sessionToken (Some(Cookies.permanentExpiresAt now))
    let session =
        Value.Object [ "session_id", Value.String "0123456789abcdef0123456789abcdef"; "_csrf_token", Value.String "k3u9mXx0aZ1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6q7" ]
    let encrypted = Cookies.encrypt secrets "_campfire_session" session (Some(Cookies.permanentExpiresAt now))
    let mutable sink = 0
    let eat (s: string) = sink <- sink ^^^ s.GetHashCode()
    let roomGid = "Z2lkOi8vY2FtcGZpcmUvUm9vbS80Mj9leHBpcmVzX2luPW5ldmVy"
    let key = secrets.KeyGenerator.GenerateKey("signed cookie", 64)
    let data = Array.init 190 (fun i -> byte (65 + i % 26))
    let dest = Array.zeroCreate<byte> 32
    let sha1 = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, key)
    let sha256 = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key)
    let raw: (string * (unit -> unit)) list =
        [ "raw: HMACSHA1.HashData(key, 190 B) one-shot", (fun () -> HMACSHA1.HashData(key, data) |> ignore)
          "raw: HMACSHA1.TryHashData(key, 190 B) into a buffer", (fun () -> HMACSHA1.TryHashData(key, ReadOnlySpan data, Span dest) |> ignore)
          "raw: IncrementalHash HMAC-SHA1 reused: Append + GetHashAndReset", (fun () -> sha1.AppendData data; sha1.GetHashAndReset(Span dest) |> ignore)
          "raw: HMACSHA256.HashData(key, 190 B) one-shot", (fun () -> HMACSHA256.HashData(key, data) |> ignore)
          "raw: IncrementalHash HMAC-SHA256 reused: Append + GetHashAndReset", (fun () -> sha256.AppendData data; sha256.GetHashAndReset(Span dest) |> ignore)
          "raw: new AesGcm + Decrypt 90 B + Dispose",
          (let k = RandomNumberGenerator.GetBytes 32
           let iv = RandomNumberGenerator.GetBytes 12
           let pt = Array.zeroCreate<byte> 90
           let ct = Array.zeroCreate<byte> 90
           let tag = Array.zeroCreate<byte> 16
           (use a = new AesGcm(k, 16) in a.Encrypt(iv, pt, ct, tag))
           fun () -> use a = new AesGcm(k, 16) in a.Decrypt(iv, ct, tag, pt))
          "raw: one AesGcm reused: Decrypt 90 B",
          (let k = RandomNumberGenerator.GetBytes 32
           let iv = RandomNumberGenerator.GetBytes 12
           let pt = Array.zeroCreate<byte> 90
           let ct = Array.zeroCreate<byte> 90
           let tag = Array.zeroCreate<byte> 16
           let a = new AesGcm(k, 16)
           a.Encrypt(iv, pt, ct, tag)
           fun () -> a.Decrypt(iv, ct, tag, pt))
          "raw: SHA1.HashData 190 B (the compression cost alone)", (fun () -> SHA1.HashData data |> ignore) ]
    let envelope = Text.Encoding.UTF8.GetBytes """{"_rails":{"message":"IjlmUTNtWHgwYVoxYjJjM2Q0ZTVmNmc3aCI=","exp":"2046-10-06T12:00:00.000Z","pur":"cookie.session_token"}}"""
    let parts: (string * (unit -> unit)) list =
        [ "part: Timestamps.tryParse of an exp", (fun () -> Timestamps.tryParse "2046-10-06T12:00:00.000Z" |> ignore)
          "part: Json.parse of the legacy envelope", (fun () -> Json.parse envelope |> ignore)
          "part: RailsEncoding.urlsafeDecode of 190 chars", (fun () -> RailsEncoding.urlsafeDecode (String('A', 188)) |> ignore) ]
    let main: (string * (unit -> unit)) list =
        [ "KeyGenerator.GenerateKey (cached)", (fun () -> secrets.KeyGenerator.GenerateKey("signed cookie", 64) |> ignore)
          "cookies.signed[:session_token] (verify)",
          (fun () -> match Cookies.verifySigned secrets "session_token" signedToken now with Some s -> eat s | None -> failwith "bad")
          "cookies.signed[:session_token]= (sign)", (fun () -> eat (Cookies.sign secrets "session_token" sessionToken (Some(Cookies.permanentExpiresAt now))))
          "cookies.encrypted[:_campfire_session] (decrypt)",
          (fun () -> match Cookies.decrypt secrets "_campfire_session" encrypted now with Some _ -> () | None -> failwith "bad")
          "cookies.encrypted[:_campfire_session]= (encrypt)", (fun () -> eat (Cookies.encrypt secrets "_campfire_session" session (Some(Cookies.permanentExpiresAt now))))
          "Turbo.signedStreamName room messages", (fun () -> eat (Turbo.signedStreamName secrets [ roomGid; "messages" ]))
          "Turbo.signedStreamName rooms", (fun () -> eat (Turbo.signedStreamName secrets [ "rooms" ]))
          "SignedId.generate User avatar (one avatar token)", (fun () -> eat (SignedId.generate secrets "User" 7L (Some "avatar") None))
          "SignedId.generate x 40 distinct users (a room page's avatars)",
          (fun () -> for id in 1L .. 40L do eat (SignedId.generate secrets "User" id (Some "avatar") None))
          "SignedId.verify avatar",
          (let token = SignedId.generate secrets "User" 7L (Some "avatar") None
           fun () -> match SignedId.verify secrets "User" token (Some "avatar") now with Some _ -> () | None -> failwith "bad") ]
    let scenarios = raw @ parts @ main
    // The bytes each one makes, so two builds can be compared (the encrypted ones are random, so only their length).
    printfn "avatar 7: %s" (SignedId.generate secrets "User" 7L (Some "avatar") None)
    printfn "turbo: %s" (Turbo.signedStreamName secrets [ "rooms" ])
    printfn "signed: %s" (Cookies.sign secrets "session_token" sessionToken (Some(Cookies.permanentExpiresAt now)))
    printfn "%-70s %12s %12s" "scenario" "ns/call" "bytes/call"
    for (name, f) in scenarios |> List.filter (fun (name, _) -> name.Contains filter) do
        let ns, bytes = time seconds f
        printfn "%-70s %12.0f %12.0f" name ns bytes
    if sink = 42 then printfn ""
    0
