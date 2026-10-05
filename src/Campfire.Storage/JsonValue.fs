// Port of rust/crates/storage/src/json.rs
//
// Rust's `Json` is an owned, order-preserving value with an integer/float split. That is what
// `Campfire.RailsCompat.Value` is (the `serde_json::Value` counterpart `rails_compat::json::encode`
// writes), so this module holds the `Json` methods over it instead of a second type: blob metadata
// and the Active Storage verifier payloads are Ruby hashes, so key order is part of the stored
// bytes and the signed messages.
namespace Campfire.Storage

open System.Text
open Campfire.RailsCompat

module JsonValue =
    /// `Json::object()`
    let object : Value = Value.Object []

    let rec private fitsInt64 (value: Value) : bool =
        match value with
        | Value.UInt _ -> false
        | Value.Array items -> List.forall fitsInt64 items
        | Value.Object entries -> entries |> List.forall (snd >> fitsInt64)
        | _ -> true

    /// `Json::parse`. As Rust's `visit_u64`, an integer past i64::MAX is an error rather than a
    /// number of its own; a repeated key keeps its last value at its first position, as Ruby's
    /// `JSON.parse` does (`Json.parse` already reads objects that way).
    let parse (text: string) : Value option =
        match Json.parse (Encoding.UTF8.GetBytes text) with
        | Some value when fitsInt64 value -> Some value
        | _ -> None

    /// `Json::get`
    let get (key: string) (json: Value) : Value option = json.TryGet key

    /// `Json::as_str`
    let asStr (json: Value) : string option = json.AsString

    /// `Json::as_i64`
    let asInt64 (json: Value) : int64 option = json.AsInt64

    /// `Hash#[]=`: replaces an existing key in place, or appends a new one. Any other value is
    /// left as it is.
    let set (key: string) (value: Value) (json: Value) : Value =
        match json with
        | Value.Object entries ->
            if entries |> List.exists (fun (k, _) -> k = key) then
                Value.Object(entries |> List.map (fun (k, v) -> if k = key then (k, value) else (k, v)))
            else
                Value.Object(entries @ [ key, value ])
        | other -> other

    /// `Hash#merge`: keys of `other` overwrite in place, new keys are appended in order.
    let merge (other: Value) (json: Value) : Value =
        match other with
        | Value.Object entries -> entries |> List.fold (fun acc (key, value) -> set key value acc) json
        | _ -> json

    /// `ActiveSupport::JSON.encode`.
    let encode (json: Value) : string = Json.encode json
