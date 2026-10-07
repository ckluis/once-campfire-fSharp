// Port of rust/crates/views/src/helpers/url.rs
/// URL building that `Campfire.Routes` leaves to the caller: query strings (`Hash#to_query`) and
/// format extensions (`path(format: :json)`).
module Campfire.Views.Helpers.Url

open System
open Campfire.Ruby
open Campfire.Routes

/// `CGI.escape`, for templates too.
let cgiEscape (s: string) : string = Ruby.cgiEscape s

/// A query parameter value: a scalar or an array (`key[]=a&key[]=b`).
type Param =
    | One of string
    | Many of string list

/// `url_for`'s extra params: `Hash#to_query`, which sorts by key.
let withQuery (path: string) (parameters: (string * Param) list) : string =
    // `sort_by` is stable in Rust (`sort_by` on a slice is); so is List.sortWith.
    let sorted = parameters |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
    let pairs = ResizeArray<string>()
    for (key, value) in sorted do
        match value with
        | One value -> pairs.Add(cgiEscape key + "=" + cgiEscape value)
        | Many values ->
            let key = cgiEscape (key + "[]")
            for value in values do
                pairs.Add(key + "=" + cgiEscape value)
    if pairs.Count = 0 then path else path + "?" + String.Join("&", pairs)

/// `rooms_directs_path(user_ids: [ id ])`.
let roomsDirectsWithUsers (userIds: int64 list) : string =
    withQuery (Routes.roomsDirects ()) [ "user_ids", Many(userIds |> List.map string) ]

/// `rooms_directs_path(user_ids: [ user.id ])`.
let roomsDirectsWithUser (userId: int64) : string = roomsDirectsWithUsers [ userId ]
