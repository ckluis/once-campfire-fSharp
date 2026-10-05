/// Helpers the richtext tests share.
module Campfire.RichText.Tests.Support

open Campfire.RichText

/// The value of an `Ok`, or a failed assertion saying what the `Error` was.
let ok (r: Result<'a, 'e>) : 'a =
    match r with
    | Ok v -> v
    | Error e -> failwithf "expected Ok, got Error %A" e

let okP (r: Result<'a, ParseError>) : 'a =
    match r with
    | Ok v -> v
    | Error e -> failwithf "expected Ok, got Error %s" e.Message

let isError (r: Result<'a, 'e>) : bool =
    match r with
    | Error _ -> true
    | Ok _ -> false
