// Unit 7.3: the fast paths of the signed-cookie and token code each have a slower general path they must agree with on every input
// (the regex timestamp parser, `Json.parse` on an envelope, the strict Base64 decoder over a string), plus the per-thread keyed states
// and the token memo, which must not change a byte of what is signed or ever hand one secret's token to another.
module Campfire.RailsCompat.Tests.FastPathTests

open System
open System.Text
open System.Threading.Tasks
open Xunit
open Campfire.RailsCompat

let private random = Random 7_03

let private pick (alphabet: string) : char = alphabet[random.Next alphabet.Length]

// --- timestamps

[<Fact>]
let ``the millisecond timestamp fast path agrees with the pattern on generated strings`` () =
    let shape = "dddd-dd-ddTdd:dd:dd.dddZ"
    let mutable parsed = 0
    for _ in 1..60_000 do
        let chars =
            shape
            |> Seq.map (fun c ->
                match c with
                | 'd' -> if random.Next 40 = 0 then pick "aZ -+:./T,0123456789" else pick "0123456789"
                | other -> if random.Next 60 = 0 then pick "-:.TZz +/" else other)
            |> Array.ofSeq
        // dates the calendar refuses (month 00, day 31 in a 30-day month, hour 24) are most of what digits alone make
        let text = String chars
        let fast = Timestamps.tryParse text
        let slow = Timestamps.parseWithPattern text
        Assert.Equal(slow, fast)
        if fast.IsSome then parsed <- parsed + 1
    Assert.True(parsed > 100, $"only {parsed} of the generated strings were timestamps")
    // real instants, formatted as the cookies write them, some with a character changed
    for _ in 1..20_000 do
        let instant = DateTimeOffset.FromUnixTimeMilliseconds(random.NextInt64(-62_135_596_800_000L, 253_402_300_799_999L))
        let text = Metadata.iso8601Millis instant
        let text = if random.Next 5 = 0 then (let i = random.Next text.Length in text.Remove(i, 1).Insert(i, string (pick "0189:-TZ."))) else text
        let fast = Timestamps.tryParse text
        Assert.Equal(Timestamps.parseWithPattern text, fast)
        if fast.IsSome then parsed <- parsed + 1
    Assert.True(parsed > 10_000)

[<Theory>]
[<InlineData("2046-01-01T12:00:00.000Z")>]
[<InlineData("2026-02-29T12:00:00.000Z")>]
[<InlineData("2024-02-29T23:59:60.999Z")>]
[<InlineData("2024-02-29T24:00:00.000Z")>]
[<InlineData("0000-01-01T00:00:00.000Z")>]
[<InlineData("0001-01-01T00:00:00.000Z")>]
[<InlineData("9999-12-31T23:59:59.999Z")>]
[<InlineData("2024-13-01T00:00:00.000Z")>]
[<InlineData("2024-04-31T00:00:00.000Z")>]
[<InlineData("2024-04-30T00:60:00.000Z")>]
[<InlineData("2024-04-30t00:00:00.000Z")>]
[<InlineData("2024-04-30 00:00:00.000Z")>]
[<InlineData("2024-04-30T00:00:00.000z")>]
[<InlineData("2024-04-30T00:00:00.000+00:00")>]
[<InlineData("2024-04-30T00:00:00.00Z")>]
[<InlineData("2024-04-30T00:00:00Z")>]
[<InlineData("")>]
[<InlineData("2024-04-30T00:00:00.000Z ")>]
let ``the millisecond timestamp fast path agrees with the pattern on edge cases`` (text: string) =
    Assert.Equal(Timestamps.parseWithPattern text, Timestamps.tryParse text)

// --- Base64

/// The decoder before the span rewrite: the symbols into an array, then the strict rules.
let private referenceStrictDecode (encoded: string) : byte[] option =
    let symbol (c: char) =
        if c >= 'A' && c <= 'Z' then int c - int 'A'
        elif c >= 'a' && c <= 'z' then int c - int 'a' + 26
        elif c >= '0' && c <= '9' then int c - int '0' + 52
        elif c = '+' then 62
        elif c = '/' then 63
        else -1
    let n = encoded.Length
    if n % 4 <> 0 then
        None
    else
        let padding = if n >= 2 && encoded[n - 2] = '=' then 2 elif n >= 1 && encoded[n - 1] = '=' then 1 else 0
        let dataLength = n - padding
        let symbols = Array.init dataLength (fun i -> symbol encoded[i])
        if symbols |> Array.exists (fun s -> s < 0) then
            None
        else
            let outLength = dataLength / 4 * 3 + (match dataLength % 4 with 0 -> 0 | 2 -> 1 | 3 -> 2 | _ -> -1)
            let spareBits =
                match padding with
                | 2 -> symbols[dataLength - 1] &&& 0x0f
                | 1 -> symbols[dataLength - 1] &&& 0x03
                | _ -> 0
            if outLength < 0 || spareBits <> 0 then
                None
            else
                let out = Array.zeroCreate<byte> outLength
                let mutable o = 0
                let mutable i = 0
                while i < dataLength do
                    let remaining = dataLength - i
                    out[o] <- byte ((symbols[i] <<< 2) ||| (symbols[i + 1] >>> 4))
                    o <- o + 1
                    if remaining >= 3 then
                        out[o] <- byte (((symbols[i + 1] &&& 0x0f) <<< 4) ||| (symbols[i + 2] >>> 2))
                        o <- o + 1
                        if remaining >= 4 then
                            out[o] <- byte (((symbols[i + 2] &&& 0x03) <<< 6) ||| symbols[i + 3])
                            o <- o + 1
                    i <- i + 4
                Some out

let private referenceUrlsafeDecode (encoded: string) : byte[] option =
    let translated = encoded.Replace('-', '+').Replace('_', '/')
    let padded =
        if not (encoded.EndsWith '=') && encoded.Length % 4 <> 0 then
            translated.PadRight(translated.Length + (4 - translated.Length % 4), '=')
        else
            translated
    referenceStrictDecode padded

[<Fact>]
let ``both Base64 decoders agree with the array decoder they replaced`` () =
    let mutable decoded = 0
    for _ in 1..80_000 do
        let length = random.Next 14
        let alphabet =
            match random.Next 4 with
            | 0 -> "ABCDQRgw=="
            | 1 -> "AZaz09+/-_="
            | 2 -> "AQgw0Aaz+/-_=="
            | _ -> "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=-_ .é"
        let text = String(Array.init length (fun _ -> pick alphabet))
        Assert.Equal<byte[] option>(referenceStrictDecode text, RailsEncoding.strictDecode text)
        Assert.Equal<byte[] option>(referenceUrlsafeDecode text, RailsEncoding.urlsafeDecode text)
        if (RailsEncoding.urlsafeDecode text).IsSome then decoded <- decoded + 1
    Assert.True(decoded > 1000, $"only {decoded} strings decoded")

[<Fact>]
let ``url safe encoders match the replace based ones`` () =
    for length in 0..40 do
        let data = Array.init length (fun _ -> byte (random.Next 256))
        let standard = Convert.ToBase64String data
        Assert.Equal(standard.TrimEnd('=').Replace('+', '-').Replace('/', '_'), RailsEncoding.urlsafeEncodeUnpadded data)
        Assert.Equal(standard.Replace('+', '-').Replace('/', '_'), RailsEncoding.urlsafeEncodePadded data)

// --- the metadata envelope

let private envelope (message: string) (exp: string) (pur: string) : byte[] =
    Encoding.UTF8.GetBytes $"{{\"_rails\":{{\"message\":\"{message}\",\"exp\":{exp},\"pur\":{pur}}}}}"

[<Fact>]
let ``the legacy envelope fast path agrees with the JSON parser`` () =
    let now = (Timestamps.tryParse "2026-10-06T12:00:00Z").Value
    let message = RailsEncoding.strictEncode (Encoding.UTF8.GetBytes "\"hello\"")
    let exps = [ "null"; "\"2046-10-06T12:00:00.000Z\""; "\"2020-01-01T00:00:00.000Z\""; "\"2046-10-06T12:00:00Z\""; "\"nonsense\""; "\"2046-10-06T12:00:00.000\\u005a\""; "5"; "true" ]
    let purs = [ "null"; "\"cookie.x\""; "\"cookie.y\""; "\"\""; "\"café\""; "\"cookie\\u002ex\""; "\"a\\\\b\""; "7" ]
    let mutable matched = 0
    let check (bytes: byte[]) =
        for purpose in [ None; Some "cookie.x"; Some "" ] do
            let general = Metadata.deserializeWithMetadataGeneric Serializer.Null bytes purpose now RailsEncoding.strictDecode
            let fast = Metadata.deserializeWithMetadata Serializer.Null bytes purpose now RailsEncoding.strictDecode
            Assert.Equal(general, fast)
            if Result.isOk fast then matched <- matched + 1
    for exp in exps do
        for pur in purs do
            check (envelope message exp pur)
    // whitespace, other key orders, a different message, trailing bytes, truncation, and random damage to a good one
    check (Encoding.UTF8.GetBytes $"{{\"_rails\":{{\"message\":\"{message}\", \"exp\":null,\"pur\":null}}}}")
    check (Encoding.UTF8.GetBytes $"{{\"_rails\":{{\"message\":\"{message}\",\"pur\":null,\"exp\":null}}}}")
    check (Encoding.UTF8.GetBytes $"{{\"_rails\":{{\"message\":\"{message}\",\"exp\":null,\"pur\":null}}}} ")
    check (Encoding.UTF8.GetBytes $"{{\"_rails\":{{\"message\":\"{message}\",\"exp\":null,\"pur\":null}}")
    check (Encoding.UTF8.GetBytes $"{{\"_rails\":{{\"message\":\"%%%%\",\"exp\":null,\"pur\":null}}}}")
    check (Encoding.UTF8.GetBytes $"{{\"_rails\":{{\"message\":\"{message}\",\"exp\":null,\"pur\":\"cookie.x\"}}}}")
    let good = envelope message "\"2046-10-06T12:00:00.000Z\"" "\"cookie.x\""
    for _ in 1..3000 do
        let copy = Array.copy good
        for _ in 1 .. 1 + random.Next 3 do
            copy[random.Next copy.Length] <- byte (random.Next 256)
        check copy
        check (Array.sub copy 0 (random.Next copy.Length))
    Assert.True(matched > 10, "no envelope was accepted")

// --- keyed states

[<Fact>]
let ``signing and verifying on many threads is the same as on one`` () =
    let secrets = Secrets.create "keyed-states-secret"
    let now = (Timestamps.tryParse "2026-10-06T12:00:00Z").Value
    let signed = Cookies.sign secrets "session_token" "the-token" (Some(Cookies.permanentExpiresAt now))
    let tampered = signed.Substring(0, signed.Length - 1) + (if signed[signed.Length - 1] = 'a' then "b" else "a")
    let encrypted = Cookies.encrypt secrets "_campfire_session" (Value.String "x") None
    let failures =
        Parallel.For(
            0,
            8,
            fun _ ->
                for _ in 1..3000 do
                    Assert.Equal(Some "the-token", Cookies.verifySigned secrets "session_token" signed now)
                    Assert.Equal(None, Cookies.verifySigned secrets "session_token" tampered now)
                    Assert.Equal(Some(Value.String "x"), Cookies.decrypt secrets "_campfire_session" encrypted now)
                    let fresh = Cookies.encrypt secrets "_campfire_session" (Value.String "y") None
                    Assert.Equal(Some(Value.String "y"), Cookies.decrypt secrets "_campfire_session" fresh now)
        )
    Assert.True failures.IsCompleted

[<Fact>]
let ``a key array that is overwritten signs with its new contents`` () =
    let key = Array.create 64 1uy
    let verifier () = MessageVerifier.create key Digest.Sha256 Encoding.Strict Serializer.Json
    let first = MessageVerifier.generate (verifier ()) (Value.String "x") None None
    key[0] <- 2uy
    let second = MessageVerifier.generate (verifier ()) (Value.String "x") None None
    let independent = MessageVerifier.generate (MessageVerifier.create (Array.copy key) Digest.Sha256 Encoding.Strict Serializer.Json) (Value.String "x") None None
    Assert.NotEqual<string>(first, second)
    Assert.Equal(independent, second)

[<Fact>]
let ``the shared key is the generated key`` () =
    let generator = KeyGenerator "secret"
    let shared = generator.SharedKey("salt", 64)
    Assert.Same(shared, generator.SharedKey("salt", 64))
    Assert.Equal<byte[]>(shared, generator.GenerateKey("salt", 64))
    Assert.NotSame(shared, generator.GenerateKey("salt", 64))
    Assert.Equal<byte[]>(Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2("secret", Encoding.UTF8.GetBytes "salt", 1000, Security.Cryptography.HashAlgorithmName.SHA256, 64), shared)

// --- the token memo

[<Fact>]
let ``remembered tokens are the tokens a fresh signature makes`` () =
    let secrets = Secrets.create "memo-secret"
    let other = Secrets.create "another-secret"
    let direct (s: Secrets) (id: int64) =
        MessageVerifier.generate (SignedId.verifier s) (Value.Int id) (Some(SignedId.combinePurposes "User" (Some "avatar"))) None
    for id in 1L .. 50L do
        let first = SignedId.generate secrets "User" id (Some "avatar") None
        Assert.Equal(direct secrets id, first)
        Assert.Same(first, SignedId.generate secrets "User" id (Some "avatar") None)
        Assert.NotEqual<string>(first, SignedId.generate other "User" id (Some "avatar") None)
        Assert.NotEqual<string>(first, SignedId.generate secrets "User" id (Some "transfer") None)
        Assert.NotEqual<string>(first, SignedId.generate secrets "Room" id (Some "avatar") None)
    // an expiry is part of the token: never remembered
    let expiry = Some((Timestamps.tryParse "2046-10-06T12:00:00Z").Value)
    let a = SignedId.generate secrets "User" 1L (Some "transfer") expiry
    let b = SignedId.generate secrets "User" 1L (Some "transfer") (Some(expiry.Value.AddHours 1.0))
    Assert.NotEqual<string>(a, b)
    // stream names
    let name = Turbo.signedStreamName secrets [ "Z2lk"; "messages" ]
    Assert.Equal(MessageVerifier.generate (Turbo.verifier secrets) (Value.String "Z2lk:messages") None None, name)
    Assert.Equal(Some "Z2lk:messages", Turbo.verifiedStreamName secrets name)
    Assert.NotEqual<string>(name, Turbo.signedStreamName other [ "Z2lk"; "messages" ])

[<Fact>]
let ``a full memo is emptied and goes on making the same tokens`` () =
    let secrets = Secrets.create "memo-full"
    let before = SignedId.generate secrets "User" 1L (Some "avatar") None
    for id in 2L .. 40_000L do
        SignedId.generate secrets "User" id (Some "avatar") None |> ignore
    Assert.Equal(before, SignedId.generate secrets "User" 1L (Some "avatar") None)
