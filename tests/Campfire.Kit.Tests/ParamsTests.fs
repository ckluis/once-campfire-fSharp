// Port of the #[cfg(test)] module in rust/crates/kit/src/params.rs
module Campfire.Kit.Tests.ParamsTests

open System
open System.Diagnostics
open System.Text
open Xunit
open Campfire.RailsCompat
open Campfire.Kit
open Campfire.Kit.Tests.Helpers

let private parse (qs: string) : Value =
    match Params.tryParseNested qs with
    | Ok value -> value
    | Error e -> failwith $"{qs}: {e}"

let private fails (qs: string) : ParamError =
    match Params.tryParseNested qs with
    | Ok value -> failwith $"{qs} parsed as {value}"
    | Error e -> e

let private isType (e: ParamError) = match e with ParamError.Type _ -> true | _ -> false
let private isInvalid (e: ParamError) = match e with ParamError.Invalid _ -> true | _ -> false

let private fromQueryString (qs: string) : ParamMap =
    match Params.fromQueryString qs with
    | Ok map -> map
    | Error e -> failwith $"{qs}: {e}"

[<Fact>]
let ``flat pairs`` () =
    Assert.Equal(json """{"a": "1", "b": "2"}""", parse "a=1&b=2")
    Assert.Equal(json "{}", parse "")
    Assert.Equal(json """{"a": null}""", parse "a")
    Assert.Equal(json """{"a": ""}""", parse "a=")
    Assert.Equal(json """{"a": "2"}""", parse "a=1&a=2")
    Assert.Equal(json """{"a": "1"}""", parse "&&a=1&")
    Assert.Equal(json """{"a": "1", "b": "2"}""", parse "a=1&  b=2")
    Assert.Equal(json "{}", parse "=1")
    Assert.Equal(json """{"a": "b=c"}""", parse "a=b=c")
    Assert.Equal(json """{"a;b": "1"}""", parse "a;b=1")

[<Fact>]
let ``decoding`` () =
    Assert.Equal(json """{"a": "x y z"}""", parse "a=x+y%20z")
    Assert.Equal(json """{"a": {"b": "1"}}""", parse "a%5Bb%5D=1")
    Assert.Equal(json """{"café": "✓"}""", parse "caf%C3%A9=%E2%9C%93")
    Assert.True(isInvalid (fails "a=%"))
    Assert.True(isInvalid (fails "a=%zz"))
    Assert.True(isInvalid (fails "a=%FF"))
    // An empty top-level key is skipped before the encoding check.
    Assert.Equal(json "{}", parse "=%FF")

[<Fact>]
let ``nested hashes`` () =
    Assert.Equal(json """{"a": {"b": "1"}}""", parse "a[b]=1")
    Assert.Equal(json """{"a": {"b": {"c": "1", "d": "2"}}}""", parse "a[b][c]=1&a[b][d]=2")
    Assert.Equal(json """{"a": {"b": {"c": "1"}}}""", parse "a[b]c=1")
    Assert.Equal(json """{"[a]": "1"}""", parse "[a]=1")
    Assert.Equal(json """{"a[": "1"}""", parse "a[=1")
    Assert.Equal(json """{"a": {"[b": "1"}}""", parse "a[b=1")
    Assert.Equal(json """{"a]": "1"}""", parse "a]=1")
    Assert.Equal(json """{"a": [{"]": "1"}]}""", parse "a[]]=1")
    Assert.Equal(json """{"a": {"[": {"]": "1"}}}""", parse "a[[]]=1")

[<Fact>]
let ``arrays`` () =
    Assert.Equal(json """{"a": ["1", "2"]}""", parse "a[]=1&a[]=2")
    Assert.Equal(json """{"a": []}""", parse "a[]")
    Assert.Equal(json """{"a": [""]}""", parse "a[]=&a[]")
    Assert.Equal(json """{"a": [["1"]]}""", parse "a[][]=1")
    Assert.Equal(json """{"a": [[]]}""", parse "a[][]")
    Assert.Equal(json """{"a": {"b": ["1", "2"]}}""", parse "a[b][]=1&a[b][]=2")

[<Fact>]
let ``hashes inside arrays`` () =
    Assert.Equal(json """{"a": [{"b": "1", "c": "2"}, {"b": "3"}]}""", parse "a[][b]=1&a[][c]=2&a[][b]=3")
    Assert.Equal(json """{"a": [{"b": {"c": "1", "d": "2"}}]}""", parse "a[][b][c]=1&a[][b][d]=2")
    Assert.Equal(json """{"a": [{"b": {"c": "1"}}, {"b": {"c": "2"}}]}""", parse "a[][b][c]=1&a[][b][c]=2")
    Assert.Equal(json """{"a": [{"b": ["1", "2"]}]}""", parse "a[][b][]=1&a[][b][]=2")
    Assert.Equal(json """{"a": [{"b": "1"}]}""", parse "a[]b=1")
    Assert.Equal(json """{"a": [{"b": null}]}""", parse "a[][b]")

[<Fact>]
let ``nil replaced by structure`` () =
    // `||=` treats nil as unset.
    Assert.Equal(json """{"a": ["1"]}""", parse "a&a[]=1")
    Assert.Equal(json """{"a": {"b": "1"}}""", parse "a&a[b]=1")

[<Fact>]
let ``type conflicts`` () =
    Assert.True(isType (fails "a=1&a[]=2"))
    Assert.True(isType (fails "a=1&a[b]=2"))
    Assert.True(isType (fails "a[]=1&a[b]=2"))
    Assert.True(isType (fails "a[b]=1&a[]=2"))
    Assert.True(isType (fails "a[b]=1&a[b][c]=2"))
    Assert.Equal(Value.Null, Params.parseNested "a=1&a[]=2")

[<Fact>]
let ``depth limit`` () =
    let deep = "a" + String.replicate (Params.DepthLimit - 1) "[b]" + "=1"
    Assert.True((Params.tryParseNested deep).IsOk)
    let tooDeep = "a" + String.replicate Params.DepthLimit "[b]" + "=1"
    match Params.tryParseNested tooDeep with
    | Error ParamError.TooDeep -> ()
    | other -> failwith $"{other}"

[<Fact>]
let ``form body limits`` () =
    let pairs = (Params.formPairs (ReadOnlySpan<byte>(Encoding.UTF8.GetBytes "a=1\000"))) |> Result.defaultWith (fun e -> failwith $"{e}")
    Assert.Equal(json """{"a": "1"}""", (Params.fromPairs pairs |> Result.defaultWith (fun e -> failwith $"{e}")).ToJson())
    let many = String.Join("&", Array.create (Params.FormParamsLimit + 1) "a=1")
    match Params.formPairs (ReadOnlySpan<byte>(Encoding.UTF8.GetBytes many)) with
    | Error(ParamError.Limit _) -> ()
    | other -> failwith $"{other}"

let private fromJsonBody (text: string) =
    Params.fromJsonBody (ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes text))

[<Fact>]
let ``json bodies are deep munged`` () =
    let pars = fromJsonBody """{"a": [1, null, "x"], "b": {"c": null}, "d": true}""" |> Result.defaultWith (fun e -> failwith $"{e}")
    Assert.Equal(json """{"a": [1, "x"], "b": {"c": null}, "d": true}""", pars.ToJson())
    Assert.Equal(json """{"_json": [1, 2]}""", (fromJsonBody "[1,2]" |> Result.defaultWith (fun e -> failwith $"{e}")).ToJson())
    match fromJsonBody "{bad" with
    | Error ParamError.Parse -> ()
    | other -> failwith $"{other}"

[<Fact>]
let ``require and permit`` () =
    let pars =
        fromQueryString
            "user[name]=Jo&user[admin]=1&user[tags][]=a&user[settings][x][y]=1&user[bad][]=1&blank=+&user[date(1i)]=2024"
    Assert.True((pars.Require "blank").IsError)
    Assert.True((pars.Require "missing").IsError)
    let user = (pars.Require "user" |> Result.defaultWith (fun e -> failwith $"{e}"))
    let permitted =
        user.Permit
            [ Permit.Key "name"
              Permit.Key "date"
              Permit.ScalarArray "tags"
              Permit.AnyHash "settings"
              Permit.Key "bad" ]
    Assert.Equal(
        json """{"name": "Jo", "date(1i)": "2024", "tags": ["a"], "settings": {"x": {"y": "1"}}}""",
        permitted.ToJson()
    )

[<Fact>]
let ``permit nested`` () =
    let pars = fromQueryString "a[b][c]=1&a[b][d]=2&list[][c]=1&list[][d]=2&ff[0][c]=1&ff[1][c]=2"
    let nested = [ Permit.Key "c" ]
    let permitted =
        pars.Permit
            [ Permit.Nested("a", [ Permit.Nested("b", nested) ])
              Permit.Nested("list", nested)
              Permit.Nested("ff", nested) ]
    Assert.Equal(
        json """{"a": {"b": {"c": "1"}}, "list": [{"c": "1"}], "ff": {"0": {"c": "1"}, "1": {"c": "2"}}}""",
        permitted.ToJson()
    )

[<Fact>]
let ``fetch default`` () =
    let pars = fromQueryString "user_ids[]=1&user_ids[]=2"
    Assert.Equal(json """["1", "2"]""", (pars.Fetch("user_ids", Param.Array(ResizeArray()))).ToJson())
    Assert.Equal(json "[]", (pars.Fetch("nope", Param.Array(ResizeArray()))).ToJson())

[<Fact>]
let ``many keys build in linear time`` () =
    let entries = [ for n in 0..99_999 -> $"k{n}", Value.Int(int64 n) ]
    let started = Stopwatch.GetTimestamp()
    match Params.ofJsonValue (Value.Object entries) with
    | Param.Hash map -> Assert.Equal(100_000, map.Count)
    | _ -> failwith "a hash"
    let elapsed = Stopwatch.GetElapsedTime started
    Assert.True(elapsed < TimeSpan.FromSeconds 1.0, $"{elapsed}")

[<Fact>]
let ``many keys parse from json and a query string in linear time`` () =
    let body = "{" + String.Join(",", [ for n in 0..99_999 -> $"\"k{n}\":{n}" ]) + "}"
    let started = Stopwatch.GetTimestamp()
    match fromJsonBody body with
    | Ok map -> Assert.Equal(100_000, map.Count)
    | Error e -> failwith $"{e}"
    let query = String.Join("&", [ for n in 0..99_999 -> $"k{n}=1" ])
    Assert.Equal(100_000, (fromQueryString query).Count)
    let elapsed = Stopwatch.GetElapsedTime started
    Assert.True(elapsed < TimeSpan.FromSeconds 2.0, $"{elapsed}")

[<Fact>]
let ``numbers print as serde_json prints them`` () =
    let show (n: JsonNumber) = JsonNumber.toString n
    Assert.Equal("5", show (NInt 5L))
    Assert.Equal("18446744073709551615", show (NUInt UInt64.MaxValue))
    Assert.Equal("1.0", show (NFloat 1.0))
    Assert.Equal("0.0001", show (NFloat 0.0001))
    Assert.Equal("1.5", show (NFloat 1.5))
    Assert.Equal("1e21", show (NFloat 1e21))
    Assert.Equal("1.5e-7", show (NFloat 1.5e-7))
    Assert.Equal("-2.5", show (NFloat -2.5))
    Assert.Equal("1e16", show (NFloat 1e16))
    Assert.Equal("1000000000000000.0", show (NFloat 1e15))
    Assert.Equal("0.0", show (NFloat 0.0))
    Assert.Equal("123456.789", show (NFloat 123456.789))
