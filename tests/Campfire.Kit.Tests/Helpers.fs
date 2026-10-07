/// Small helpers the kit's tests share.
module Campfire.Kit.Tests.Helpers

open System
open System.Text
open Campfire.RailsCompat
open Campfire.Kit

/// A JSON literal as a `Value`, key order and number kinds intact (`serde_json::json!`).
let json (text: string) : Value =
    match Json.parse (Encoding.UTF8.GetBytes text) with
    | Some value -> value
    | None -> failwith $"not JSON: {text}"

let ts (s: string) : Timestamp = (Timestamps.tryParse s).Value

let bytesOf (s: string) : ReadOnlyMemory<byte> = ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes s)

let textOf (memory: ReadOnlyMemory<byte>) : string = Encoding.UTF8.GetString memory.Span

/// The text of a response's buffered body.
let bodyText (response: Response) : string =
    match response.BodyBytes with
    | ValueSome bytes -> textOf bytes
    | ValueNone -> failwith "the response has no buffered body"

let secrets = lazy (Secrets.create "test-secret")
