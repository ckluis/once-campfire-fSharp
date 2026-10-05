// Port of rust/crates/storage/src/variation.rs
namespace Campfire.Storage

open System
open System.Security.Cryptography
open Campfire.RailsCompat

/// `ActiveStorage::Variation`: an ordered transformations hash, its Marshal-based digest (the
/// `variation_digest` column of `active_storage_variant_records`) and its signed URL key.
type Variation = { Transformations: (string * Marshal.Value) list }

module Variation =
    /// Transformations in Ruby's insertion order, e.g.
    /// `[ "resize_to_limit", Array [ Int 512L; Int 512L ]; "format", Symbol "webp" ]`.
    let create (transformations: (string * Marshal.Value) list) : Variation = { Transformations = transformations }

    /// `resize_to_limit: [width, height]` plus an optional `format:` symbol, the only shape the
    /// app's named variants and previews use (`Message::Attachment`, `User::Avatar`, `Account`).
    let resizeToLimit (width: int64) (height: int64) (format: string option) : Variation =
        let resize = "resize_to_limit", Marshal.Value.Array [ Marshal.Value.Int width; Marshal.Value.Int height ]
        match format with
        | Some format -> { Transformations = [ resize; "format", Marshal.Value.Symbol format ] }
        | None -> { Transformations = [ resize ] }

    /// `preview(format: :webp)` and friends.
    let formatOnly (format: string) : Variation = { Transformations = [ "format", Marshal.Value.Symbol format ] }

    let transformations (variation: Variation) : (string * Marshal.Value) list = variation.Transformations

    let get (name: string) (variation: Variation) : Marshal.Value option =
        variation.Transformations |> List.tryFind (fun (k, _) -> k = name) |> Option.map snd

    let isEmpty (variation: Variation) : bool = List.isEmpty variation.Transformations

    /// `default_to(defaults)`: `transformations.reverse_merge(defaults)`, i.e. `defaults.merge(self)`,
    /// so default keys come first and keep their position when overridden.
    let defaultTo (defaults: (string * Marshal.Value) list) (variation: Variation) : Variation =
        let merged =
            variation.Transformations
            |> List.fold
                (fun (acc: (string * Marshal.Value) list) (key, value) ->
                    if acc |> List.exists (fun (k, _) -> k = key) then
                        acc |> List.map (fun (k, v) -> if k = key then (k, value) else (k, v))
                    else
                        acc @ [ key, value ])
                defaults
        { Transformations = merged }

    let marshal (variation: Variation) : byte[] = Marshal.dump (Marshal.Value.Hash variation.Transformations)

    /// `OpenSSL::Digest::SHA1.base64digest Marshal.dump(transformations)`.
    let digest (variation: Variation) : string = Convert.ToBase64String(SHA1.HashData(marshal variation))

    /// `transformations.fetch(:format, :png)`, validated against Marcel's extension table.
    let format (variation: Variation) : StorageResult<string> =
        let format =
            match get "format" variation with
            | None -> Ok "png"
            | Some(Marshal.Value.Symbol s)
            | Some(Marshal.Value.Str s) -> Ok s
            | Some other -> Error(StorageError.InvalidVariation $"invalid format {other}")
        match format with
        | Ok format when (Marcel.byExtension format).IsNone -> Error(StorageError.InvalidVariation $"invalid variant format ({format})")
        | other -> other

    /// `Marcel::MimeType.for(extension: format)`.
    let contentType (variation: Variation) : StorageResult<string> =
        format variation |> Result.map Marcel.forExtension

    let rec private valueToJson (value: Marshal.Value) : Value =
        match value with
        | Marshal.Value.Nil -> Value.Null
        | Marshal.Value.Bool b -> Value.Bool b
        | Marshal.Value.Int i -> Value.Int i
        | Marshal.Value.Symbol s
        | Marshal.Value.Str s -> Value.String s
        | Marshal.Value.Array items -> Value.Array(List.map valueToJson items)
        | Marshal.Value.Hash entries -> Value.Object(entries |> List.map (fun (k, v) -> k, valueToJson v))

    let rec private valueOfJson (json: Value) : StorageResult<Marshal.Value> =
        match json with
        | Value.Null -> Ok Marshal.Value.Nil
        | Value.Bool b -> Ok(Marshal.Value.Bool b)
        | Value.Int i -> Ok(Marshal.Value.Int i)
        | Value.UInt _ -> Error(StorageError.InvalidVariation "integer out of range")
        | Value.Float _ -> Error(StorageError.InvalidVariation "float transformation arguments are unsupported")
        | Value.String s -> Ok(Marshal.Value.Str s)
        | Value.Array items ->
            items
            |> List.fold
                (fun acc item ->
                    result {
                        let! done' = acc
                        let! converted = valueOfJson item
                        return converted :: done'
                    })
                (Ok [])
            |> Result.map (List.rev >> Marshal.Value.Array)
        | Value.Object entries ->
            entries
            |> List.fold
                (fun acc (key, item) ->
                    result {
                        let! done' = acc
                        let! converted = valueOfJson item
                        return (key, converted) :: done'
                    })
                (Ok [])
            |> Result.map (List.rev >> Marshal.Value.Hash)

    /// The transformations as JSON, the way the verifier serializes them (symbols become strings).
    let toJson (variation: Variation) : Value = valueToJson (Marshal.Value.Hash variation.Transformations)

    /// `Variation.decode`: symbolized keys, but values stay strings.
    let fromJson (json: Value) : StorageResult<Variation> =
        match valueOfJson json with
        | Ok(Marshal.Value.Hash transformations) -> Ok { Transformations = transformations }
        | Ok _ -> Error(StorageError.InvalidVariation "transformations must be a hash")
        | Error e -> Error e

    /// `Variation#key`: `ActiveStorage.verifier.generate(transformations, purpose: :variation)`.
    let key (verifier: MessageVerifier) (variation: Variation) : string =
        MessageVerifier.generateRaw verifier (Json.encode (toJson variation)) (Some "variation") None

    let decode (verifier: MessageVerifier) (key: string) (now: Timestamp) : StorageResult<Variation> =
        match MessageVerifier.verifyRaw verifier key (Some "variation") now with
        | Error _ -> Error StorageError.InvalidSignature
        | Ok data ->
            match JsonValue.parse data with
            | None -> Error StorageError.InvalidSignature
            | Some json -> fromJson json
