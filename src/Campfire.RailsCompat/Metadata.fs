// Port of rust/crates/rails_compat/src/metadata.rs
//
// `ActiveSupport::Messages::Metadata` and `SerializerWithFallback`: how a value and its purpose
// and expiry are packed into the bytes that get signed or encrypted.
//
// Two envelopes exist. When the serializer is one Rails trusts for metadata (JSON, or the
// `:json_allow_marshal` fallback serializer) it is `{"_rails":{"data":<value>,"exp":..,"pur":..}}`
// in that serializer. With the cookie jars' `NullSerializer` (and in Rails 7.0) it's the legacy
// "dual-serialized" one, `{"_rails":{"message":"<base64 of the dumped value>","exp":..,"pur":..}}`,
// which always carries `exp` and `pur`, as `null` when unset.
namespace Campfire.RailsCompat

open System
open System.Globalization
open System.Text

[<RequireQualifiedAccess>]
type Serializer =
    /// `ActiveSupport::MessageEncryptor::NullSerializer`: the value is a string of already
    /// serialized bytes (what the cookie jars sign and encrypt). Uses the legacy envelope.
    | Null
    /// The `::JSON` module (`JSON.dump`/`JSON.load`), as signed ids and Turbo stream names use.
    | Json
    /// `SerializerWithFallback[:json]` or `[:json_allow_marshal]` with `ActiveSupport::JSON`: the
    /// app's default `message_serializer` under `load_defaults` 7.1+.
    | JsonWithFallback of allowMarshal: bool

module internal Serializer =
    let private orElse (error: Error) (option: 'a option) : Result<'a, Error> =
        match option with
        | Some value -> Ok value
        | None -> Error error

    let private startsWith (bytes: byte[]) (prefix: byte[]) =
        bytes.Length >= prefix.Length && bytes.AsSpan(0, prefix.Length).SequenceEqual(ReadOnlySpan prefix)

    let dump (serializer: Serializer) (value: Value) : byte[] =
        match serializer with
        | Serializer.Null ->
            match value with
            | Value.String s -> Encoding.UTF8.GetBytes s
            | other -> failwith $"the null serializer only signs strings, got {Json.generate other}"
        | Serializer.Json -> Encoding.UTF8.GetBytes(Json.generate value)
        | Serializer.JsonWithFallback _ -> Encoding.UTF8.GetBytes(Json.encode value)

    let load (serializer: Serializer) (bytes: byte[]) : Result<Value, Error> =
        match serializer with
        | Serializer.Null ->
            try
                Ok(Value.String(UTF8Encoding(false, true).GetString bytes))
            with :? DecoderFallbackException ->
                Error InvalidMessage
        // JSON.load("") is nil.
        | Serializer.Json when bytes.Length = 0 -> Ok Value.Null
        | Serializer.Json -> Json.parse bytes |> orElse InvalidMessage
        | Serializer.JsonWithFallback allowMarshal ->
            if startsWith bytes Marshal.signature then
                if not allowMarshal then
                    Error InvalidMessage
                else
                    match Marshal.loadString bytes with
                    | Some string -> Ok(Value.String(Encoding.UTF8.GetString string))
                    | None -> Error InvalidMessage
            else
                Json.parse bytes |> orElse InvalidMessage

    let encodeJson (serializer: Serializer) (value: Value) : string =
        match serializer with
        | Serializer.Json -> Json.generate value
        | _ -> Json.encode value

    let usesEnvelope (serializer: Serializer) : bool =
        match serializer with
        | Serializer.Null -> false
        | _ -> true

module internal Metadata =
    /// `Time#iso8601(3)` in UTC: `2046-01-01T12:00:00.000Z` (fraction truncated, not rounded).
    let iso8601Millis (time: Timestamp) : string =
        let utc = time.UtcDateTime
        let millis = int (utc.Ticks % TimeSpan.TicksPerSecond / TimeSpan.TicksPerMillisecond)
        utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "." + millis.ToString("D3", CultureInfo.InvariantCulture) + "Z"

    /// Like `serializeWithMetadata` for a value the caller already dumped with `serializer`
    /// (so the caller controls key order and escaping).
    let serializeDumpedWithMetadata (serializer: Serializer) (dumped: byte[]) (purpose: string option) (expiresAt: Timestamp option) : byte[] =
        if purpose.IsNone && expiresAt.IsNone then
            dumped
        else
            let expiry = expiresAt |> Option.map (fun t -> Value.String(iso8601Millis t))
            let purpose = purpose |> Option.map Value.String
            if Serializer.usesEnvelope serializer then
                let out = StringBuilder()
                out.Append("""{"_rails":{"data":""").Append(Encoding.UTF8.GetString dumped) |> ignore
                match expiry with
                | Some expiry -> out.Append(",\"exp\":").Append(Serializer.encodeJson serializer expiry) |> ignore
                | None -> ()
                match purpose with
                | Some purpose -> out.Append(",\"pur\":").Append(Serializer.encodeJson serializer purpose) |> ignore
                | None -> ()
                out.Append "}}" |> ignore
                Encoding.UTF8.GetBytes(out.ToString())
            else
                let message = Value.String(RailsEncoding.strictEncode dumped)
                let json = Json.encode
                Encoding.UTF8.GetBytes(
                    "{\"_rails\":{\"message\":"
                    + json message
                    + ",\"exp\":"
                    + json (defaultArg expiry Value.Null)
                    + ",\"pur\":"
                    + json (defaultArg purpose Value.Null)
                    + "}}"
                )

    let serializeWithMetadata (serializer: Serializer) (value: Value) (purpose: string option) (expiresAt: Timestamp option) : byte[] =
        serializeDumpedWithMetadata serializer (Serializer.dump serializer value) purpose expiresAt

    /// `Object#to_s` for the JSON values a purpose could hold.
    let rubyToS (value: Value option) : string =
        match value with
        | None
        | Some Value.Null -> ""
        | Some(Value.String s) -> s
        | Some(Value.Bool b) -> if b then "true" else "false"
        | Some(Value.Int _ | Value.UInt _ | Value.Float _ as number) -> Json.generate number
        // Arrays and hashes to_s as their #inspect, which no purpose we compare against looks like.
        | Some other -> "\000" + Json.generate other

    /// `extract_from_metadata_envelope`: `exp` is expired when `now >= exp`; purposes compare with
    /// `to_s`, so a missing `pur` matches no purpose.
    let private extract (envelope: Value) (purpose: string option) (now: Timestamp) : Result<(string * Value) list, Error> =
        match envelope.TryGet "_rails" |> Option.bind (fun v -> v.AsObject) with
        | None -> Error InvalidMessage
        | Some rails ->
            let get key = rails |> List.tryFind (fun (k, _) -> k = key) |> Option.map snd
            let expiry: Result<unit, Error> =
                match get "exp" with
                | None
                | Some Value.Null -> Ok()
                | Some(Value.String exp) ->
                    match Timestamps.tryParse exp with
                    | None -> Error InvalidMessage
                    | Some exp -> if now >= exp then Error Expired else Ok()
                | Some _ -> Error InvalidMessage
            expiry
            |> Result.bind (fun () ->
                if rubyToS (get "pur") <> defaultArg purpose "" then Error PurposeMismatch else Ok rails)

    let private legacyPrefix = Encoding.UTF8.GetBytes "{\"_rails\":{\"message\":\""

    /// `deserialize_with_metadata`. `decodeLegacyMessage` decodes the base64 inside a legacy
    /// envelope: the verifier accepts either alphabet, the encryptor only strict Base64.
    let deserializeWithMetadata
        (serializer: Serializer)
        (bytes: byte[])
        (purpose: string option)
        (now: Timestamp)
        (decodeLegacyMessage: string -> byte[] option)
        : Result<Value, Error> =
        if bytes.AsSpan().StartsWith(ReadOnlySpan legacyPrefix) then
            match Json.parse bytes with
            | None -> Error InvalidSignature
            | Some envelope ->
                extract envelope purpose now
                |> Result.bind (fun rails ->
                    match rails |> List.tryFind (fun (k, _) -> k = "message") |> Option.bind (fun (_, v) -> v.AsString) with
                    | None -> Error InvalidSignature
                    | Some message ->
                        match decodeLegacyMessage message with
                        | None -> Error InvalidSignature
                        | Some dumped -> Serializer.load serializer dumped)
        else
            Serializer.load serializer bytes
            |> Result.bind (fun value ->
                if (value.TryGet "_rails").IsSome && value.IsObject then
                    extract value purpose now
                    |> Result.map (fun rails ->
                        rails |> List.tryFind (fun (k, _) -> k = "data") |> Option.map snd |> Option.defaultValue Value.Null)
                elif purpose.IsNone then
                    Ok value
                else
                    Error PurposeMismatch)
