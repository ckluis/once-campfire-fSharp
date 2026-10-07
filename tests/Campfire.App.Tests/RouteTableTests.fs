// Port of the tests of rust/crates/campfire/src/controllers.rs
module Campfire.App.Tests.RouteTableTests

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.RegularExpressions
open Xunit
open Campfire.App
open Campfire.Kit
open Campfire.Tests

type private RailsRoute =
    { Verb: string
      Path: string
      Endpoint: string
      Defaults: Map<string, string> }

type private Recognition =
    { Verb: string
      Path: string
      Endpoint: string option
      Params: Map<string, string> }

let private str (element: JsonElement) : string =
    match element.GetString() with
    | null -> failwith "not a string"
    | text -> text

let private stringMap (element: JsonElement) : Map<string, string> =
    Map.ofList [ for property in element.EnumerateObject() -> property.Name, str property.Value ]

let private vectors =
    lazy
        (let root = (Repo.vector "campfire_routes").RootElement
         let routes =
             [ for r in root.GetProperty("routes").EnumerateArray() ->
                   { RailsRoute.Verb = str (r.GetProperty "verb")
                     Path = str (r.GetProperty "path")
                     Endpoint = str (r.GetProperty "endpoint")
                     Defaults = stringMap (r.GetProperty "defaults") } ]
         let recognitions =
             [ for r in root.GetProperty("recognitions").EnumerateArray() ->
                   { Recognition.Verb = str (r.GetProperty "verb")
                     Path = str (r.GetProperty "path")
                     Endpoint =
                       (match r.GetProperty("endpoint").ValueKind with
                        | JsonValueKind.Null -> None
                        | _ -> Some(str (r.GetProperty "endpoint")))
                     Params = stringMap (r.GetProperty "params") } ]
         routes, recognitions)

/// Controllers the reference routes to but doesn't define (recognize_path raises for them).
let private missingControllers = [ "rooms/settings" ]

[<Fact>]
let ``the table is rails routes in order`` () =
    let rails, _ = vectors.Value
    let ours = RouteTable.routes.Value
    for i in 0 .. (min rails.Length ours.Length) - 1 do
        let rails, ours = rails[i], ours[i]
        let defaults = Map.ofList ours.Defaults
        Assert.True(
            ((rails.Verb, rails.Path, rails.Endpoint, rails.Defaults) = (ours.Verb, ours.Pattern, ours.Endpoint, defaults)),
            $"route #{i}"
        )
    Assert.True((rails.Length = ours.Length), "every Rails route is in the table, and nothing else")
    Assert.Equal(177, ours.Length)

/// Params of a recognized route as strings, without `controller` and `action`.
let private paramStrings (pathParams: ParamMap) : Map<string, string> =
    Map.ofList
        [ for struct (k, v) in pathParams.Iter do
              if k <> "controller" && k <> "action" then
                  k, (match v.AsStr with null -> failwith "a path param is a string" | s -> s) ]

[<Fact>]
let ``recognizes paths like rails`` () =
    let _, recognitions = vectors.Value
    // The 111 `recognitions` of vectors/campfire_routes.json, deferred from Phase 1: every one is recognized.
    Assert.Equal(111, recognitions.Length)
    for sample in recognitions do
        let path = RouteTable.normalizePath sample.Path
        let label = $"{sample.Verb} {sample.Path}"
        match sample.Endpoint, RouteTable.recognize sample.Verb path with
        | Some endpoint, Ok(Some(route, pathParams)) ->
            Assert.True((route.Endpoint = endpoint), label)
            Assert.True((paramStrings pathParams = sample.Params), label)
        | None, Ok(Some(route, _)) -> Assert.True(List.contains route.Controller missingControllers, $"{label} matched {route.Endpoint}")
        | None, Ok None -> ()
        | Some endpoint, Ok None -> failwith $"{label} should be {endpoint}"
        | _, Error e -> failwith $"{label}: {Error.display e}"

/// Recognition the straightforward way: try the verb's routes in table order and take the first whose
/// regex matches. `recognize` must agree with it on every path.
let private recognizeByScanning (meth: string) (path: string) : Result<(Route * ParamMap) option, Error> =
    let verb = if meth = "HEAD" then "GET" else meth
    let mutable found = None
    for route in RouteTable.routes.Value do
        if found.IsNone && route.Verb = verb then
            let m = route.Regex.Match path
            if m.Success then found <- Some(route, m)
    match found with
    | None -> Ok None
    | Some(route, m) -> RouteTable.pathParams route m |> Result.map (fun pathParams -> Some(route, pathParams))

/// The matched row's index and params, or the error, in a form that compares.
type private Outcome = Result<(int * (string * string) list) option, string>

let private outcome (recognized: Result<(Route * ParamMap) option, Error>) : Outcome =
    match recognized with
    | Ok(Some(route, pathParams)) ->
        let row = RouteTable.routes.Value |> Array.findIndex (fun r -> obj.ReferenceEquals(r, route))
        let pairs = [ for struct (k, v) in pathParams.Iter -> k, (match v.ToS() with null -> "" | text -> text) ]
        Ok(Some(row, pairs))
    | Ok None -> Ok None
    | Error e -> Error(Error.display e)

/// A route pattern's paths: with and without the format, and with its params filled in with ids,
/// words, percent-escapes and raw UTF-8 (which the server passes through unescaped, and which the
/// patterns' Unicode classes match as multi-byte sequences).
let private filledIn (pattern: string) : string list =
    let param = Regex(@"([:*])(\w+)")
    [ for format in [ ""; ".:format" ] do
          let pattern = pattern.Replace("(.:format)", format)
          for (value, glob) in [ "1", "photo"; "opens", "dir/photo.tar.gz"; "caf%C3%A9", "a%2Fb/c%20d"; "café", "😀/dir/é.tar" ] do
              yield
                  param.Replace(
                      pattern,
                      MatchEvaluator(fun m ->
                          match m.Groups[1].Value, m.Groups[2].Value with
                          | ":", "format" -> "json"
                          | ":", _ -> value
                          | _ -> glob)
                  ) ]

/// `path` and its near misses: other formats, trailing and doubled slashes, a character or a segment
/// more or less, other case, a query, escapes that aren't UTF-8, and raw UTF-8.
let private variants (path: string) : string list =
    [ yield path
      yield path.ToUpperInvariant()
      let doubled = Regex("/").Replace(path, "//", 2)
      yield doubled
      yield path.TrimStart '/'
      for suffix in [ ".json"; ".turbo_stream"; ".1.2"; "."; "/"; "//"; "x"; "/x"; "/new"; "/edit"; "?q=1"; "%FF"; "%2F"; "é"; "😀.json" ] do
          yield path + suffix
      if path.Length > 0 then
          // Without its last character (a whole one: Rust's `char_indices().last()`).
          let last = if Char.IsLowSurrogate path[path.Length - 1] then path.Length - 2 else path.Length - 1
          yield path.Substring(0, last)
      match path.LastIndexOf '/' with
      | -1 -> ()
      | slash ->
          let parent = path.Substring(0, slash)
          yield parent
          yield parent + "/%E9"
          yield parent + "/@42"
          yield parent + "/opens" ]

/// Every path the Rails vectors recognize and every route pattern filled in, as given and normalized,
/// with their near misses.
let private corpus () : Set<string> =
    let routes, recognitions = vectors.Value
    let seeds = [ for sample in recognitions -> sample.Path ] @ [ for route in routes do yield! filledIn route.Path ]
    seeds
    |> List.collect (fun seed -> [ RouteTable.normalizePath seed; seed ])
    |> List.collect variants
    |> Set.ofList

[<Fact>]
let ``recognizes like a first match scan`` () =
    let corpus = corpus ()
    let verbs = [ "GET"; "HEAD"; "POST"; "PATCH"; "PUT"; "DELETE"; "OPTIONS" ]
    let rows = HashSet<int>()
    let mutable missed = 0
    let mutable failed = 0
    for verb in verbs do
        for path in corpus do
            let expected = outcome (recognizeByScanning verb path)
            let actual = outcome (RouteTable.recognize verb path)
            Assert.True((actual = expected), $"{verb} {path}")
            match expected with
            | Ok(Some(row, _)) -> rows.Add row |> ignore
            | Ok None -> missed <- missed + 1
            | Error _ -> failed <- failed + 1
    // Every row answers some path, except the three that `GET /rooms/:id`, drawn first, shadows.
    let unmatched =
        [ for i in 0 .. RouteTable.routes.Value.Length - 1 do
              if not (rows.Contains i) then
                  let route = RouteTable.routes.Value[i]
                  $"{route.Verb} {route.Pattern}" ]
    Assert.Equal<string list>([ "GET /rooms/opens(.:format)"; "GET /rooms/closeds(.:format)"; "GET /rooms/directs(.:format)" ], unmatched)
    Assert.True(missed > 10_000 && failed > 1_000, $"{missed} paths missed, {failed} failed")

[<Fact>]
let ``normalizes paths`` () =
    Assert.Equal("/", RouteTable.normalizePath "")
    Assert.Equal("/rooms/1", RouteTable.normalizePath "//rooms//1//")
    Assert.Equal("/a%2Fb", RouteTable.normalizePath "/a%2fb")

/// Times `recognize` on the paths the page benchmarks request, `/up`, Active Storage URLs and a 404. Set
/// `CAMPFIRE_TIMING=1` (and run it in Release): the numbers are in bench/results/campfire-app-boot.md, Rust's
/// in its bench/results/routing-20260930.
[<Fact>]
let ``times recognition`` () =
    if Environment.GetEnvironmentVariable "CAMPFIRE_TIMING" = "1" then
        let longFilename = "/rails/active_storage/blobs/redirect/abc--def/" + String.replicate 160 "dir/café.tar/" + "photo.png"
        let paths =
            [ "room_show", "/rooms/12"
              "messages_page", "/rooms/12/messages"
              "search", "/searches"
              "up", "/up"
              "representation",
              "/rails/active_storage/representations/redirect/eyJfcmFpbHMiOnsiZGF0YSI6NDIsInB1ciI6ImJsb2JfaWQifX0=--4c2a1f0e9b8d7c6a5f4e3d2c1b0a9f8e7d6c5b4a/eyJfcmFpbHMiOnsiZGF0YSI6eyJmb3JtYXQiOiJ3ZWJwIiwicmVzaXplX3RvX2xpbWl0IjpbMTIwMCwxMjAwXX0sInB1ciI6InZhcmlhdGlvbiJ9fQ==--0f1e2d3c4b5a69788796a5b4c3d2e1f0a1b2c3d4/photo.png"
              "long filename", longFilename
              "404", "/wp-login.php" ]
        // The first call builds the table: 177 patterns compiled to regexes.
        let first = Diagnostics.Stopwatch.GetTimestamp()
        RouteTable.recognize "GET" "/up" |> ignore
        Console.Error.WriteLine $"the table, built on first use: {Diagnostics.Stopwatch.GetElapsedTime(first).TotalMilliseconds:F0} ms"
        for (_, path) in paths do
            RouteTable.recognize "GET" path |> ignore
        let samples = 9
        let iterations = 200_000
        for (name, path) in paths do
            let runs =
                [ for _ in 1..samples do
                      let started = Diagnostics.Stopwatch.GetTimestamp()
                      for _ in 1..iterations do
                          RouteTable.recognize "GET" path |> ignore
                      Diagnostics.Stopwatch.GetElapsedTime(started).TotalNanoseconds / float iterations ]
                |> List.sort
            Console.Error.WriteLine $"{name,-15} median {runs[samples / 2],6:F0} ns  (min {runs[0]:F0}, max {runs[samples - 1]:F0})"
