/// What Campfire.Kit makes of the inputs `bin/kit-differential` generates: the same operations, and the
/// same JSON answers, as `../src/main.rs` gives for the Rust kit. Shared by the tool (which compares live)
/// and by Campfire.Kit.Tests (which replays answers the Rust kit gave, kept in differential.jsonl).
module Campfire.Kit.Differential.Operations

open System
open System.IO
open System.Net
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open Campfire.RailsCompat
open Campfire.Kit

let private text (e: JsonElement) (key: string) : string | null =
    match e.TryGetProperty key with
    | true, p when p.ValueKind = JsonValueKind.String -> p.GetString()
    | _ -> null

let private errorKind (e: ParamError) : string =
    match e with
    | ParamError.Type _ -> "type"
    | ParamError.Invalid _ -> "invalid"
    | ParamError.TooDeep -> "deep"
    | ParamError.Limit _ -> "limit"
    | ParamError.Parse -> "parse"

let private symbols (formats: Format list) : Value = Value.Array [ for f in formats -> Value.String f.Symbol ]

/// Params as JSON, with an uploaded file shown by what the kit records of it.
let rec private dump (param: Param) : Value =
    match param with
    | Param.File f ->
        Value.Object
            [ "file",
              Value.Object
                  [ "name", Value.String f.OriginalFilename
                    "ct", (match f.ContentType with null -> Value.Null | c -> Value.String c)
                    "headers", Value.String f.Headers
                    "size", Value.Int f.Size
                    "hex", Value.String(Convert.ToHexStringLower(f.Read())) ] ]
    | Param.Array items -> Value.Array [ for item in items -> dump item ]
    | Param.Hash map -> Value.Object [ for i in 0 .. map.Count - 1 -> map.KeyAt i, dump (map.ValueAt i) ]
    | other -> other.ToJson()

let rec private toPermit (filter: JsonElement) : Permit =
    let name = nonNull (text filter "n")
    match nonNull (text filter "k") with
    | "key" -> Permit.Key name
    | "array" -> Permit.ScalarArray name
    | "hash" -> Permit.AnyHash name
    | _ -> Permit.Nested(name, [ for c in filter.GetProperty("c").EnumerateArray() -> toPermit c ])

let private answer (v: JsonElement) : Value =
    match nonNull (text v "op") with
    | "accept" ->
        match Format.parseAccept (nonNull (text v "input")) with
        | Ok formats -> Value.Object [ "ok", symbols formats ]
        | Error _ -> Value.Object [ "err", Value.Bool true ]
    | "formats" ->
        let input: Format.NegotiationInput =
            { FormatParam = text v "format_param"
              Accept = text v "accept"
              ContentType = text v "content_type"
              Path = nonNull (text v "path")
              Xhr = v.GetProperty("xhr").GetBoolean() }
        match Format.formats input with
        | Ok formats -> Value.Object [ "ok", symbols formats; "vary", Value.Bool(Format.shouldApplyVaryHeader input) ]
        | Error _ -> Value.Object [ "err", Value.Bool true ]
    | "cookie" ->
        let pairs = CookieJar.parseCookieHeader (nonNull (text v "input"))
        Value.Object [ "ok", Value.Array [ for (k, x) in pairs -> Value.Array [ Value.String k; Value.String x ] ] ]
    | "request" ->
        let headers = HeaderDictionary()
        for h in v.GetProperty("headers").EnumerateArray() do
            let name: string = nonNull (h[0].GetString())
            let value: string = nonNull (h[1].GetString())
            headers.Append(name, StringValues value)
        let peer = match text v "peer" with null -> null | p -> IPAddress.Parse p
        let uri = nonNull (text v "uri")
        let path, (query: string | null) =
            match uri.IndexOf '?' with
            | -1 -> uri, null
            | q -> uri.Substring(0, q), uri.Substring(q + 1)
        let proxy = { ProxyConfig.Default with AssumeSsl = v.GetProperty("assume_ssl").GetBoolean() }
        let r = Request.Create("GET", "GET", path, query, null, null, headers, peer, ReadOnlyMemory.Empty, proxy)
        let remote, ok =
            match r.RemoteIp() with
            | Ok ip -> ip, true
            | Error _ -> "spoof", false
        Value.Object
            [ "host", Value.String r.Host
              "port", Value.Int(int64 r.Port)
              "base_url", Value.String r.BaseUrl
              "url", Value.String r.Url
              "ssl", Value.Bool r.IsSsl
              "remote_ip", Value.String remote
              "remote_ok", Value.Bool ok
              "media_type", (match r.MediaType with null -> Value.Null | m -> Value.String m) ]
    | "json_body" ->
        match Params.fromJsonBody (ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(nonNull (text v "input")))) with
        | Ok map -> Value.Object [ "ok", map.ToJson() ]
        | Error e -> Value.Object [ "err", Value.String(errorKind e) ]
    | "form" ->
        let body = Convert.FromHexString(nonNull (text v "hex"))
        match Params.formPairs (ReadOnlySpan<byte> body) |> Result.bind Params.fromPairs with
        | Ok map -> Value.Object [ "ok", map.ToJson() ]
        | Error e -> Value.Object [ "err", Value.String(errorKind e) ]
    | "permit" ->
        match Params.fromQueryString (nonNull (text v "input")) with
        | Error e -> Value.Object [ "err", Value.String(errorKind e) ]
        | Ok map ->
            let filters = [ for f in v.GetProperty("filters").EnumerateArray() -> toPermit f ]
            let required =
                match map.Require(nonNull (text v "require")) with
                | Ok p -> p.ToJson()
                | Error _ -> Value.String "missing"
            Value.Object [ "ok", (map.Permit filters).ToJson(); "require", required ]
    | "multipart" ->
        let body = Convert.FromHexString(nonNull (text v "hex"))
        let headers = HeaderDictionary()
        headers.Append("content-type", StringValues(nonNull (text v "content_type")))
        let limit =
            match v.TryGetProperty "limit" with
            | true, l when l.ValueKind = JsonValueKind.Number -> ValueSome(l.GetInt32())
            | _ -> ValueNone
        use stream = new MemoryStream(body)
        match (RequestBody.parse "POST" headers (ValueSome(int64 body.Length)) stream limit).GetAwaiter().GetResult() with
        | Error TooLarge -> Value.Object [ "err", Value.String "toolarge" ]
        | Error _ -> Value.Object [ "err", Value.String "read" ]
        | Ok parsed ->
            let result =
                match parsed.Params with
                | Error e -> Value.Object [ "perr", Value.String(errorKind e) ]
                | Ok map -> Value.Object [ "ok", dump (Param.Hash map); "raw_len", Value.Int(int64 parsed.Raw.Length) ]
            for file in parsed.Files do
                file.Delete()
            result
    | "boundary" ->
        let boundary =
            match Multipart.parseBoundary (nonNull (text v "input")) with
            | ValueSome b -> Value.String b
            | ValueNone -> Value.Null
        Value.Object [ "boundary", boundary ]
    | other -> failwith $"unknown op {other}"

/// The answer to one input line, as one JSON line.
let run (line: string) : string =
    use document = JsonDocument.Parse line
    Json.generate (answer document.RootElement)
