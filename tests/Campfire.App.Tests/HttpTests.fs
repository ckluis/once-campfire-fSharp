// Port of the tests of rust/crates/campfire/src/integrations/net/http.rs
//
// Rust tests its `Inflater` directly; here the inflating is the stream `ReadBody` pulls through, so the same two facts
// are checked through a response off a local server.
module Campfire.App.Tests.HttpTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Campfire.App.Integrations
open Campfire.App.Tests.IntegrationsSupport

[<Literal>]
let private Limit = 5242880

let private readGzipped (body: byte[]) : Task<Result<Body, HttpError>> =
    task {
        use server = FakeServer.Start [ Route.create "GET" "*" "/bomb" 200 |> Route.header "Content-Encoding" "gzip" |> Route.body body ]
        let net = Network.system ()
        let endpoint: Endpoint =
            { Https = false
              Host = "127.0.0.1"
              Port = server.Addr.Port
              PinnedIp = None }
        let request = Request.netHttp "GET" "/bomb" None [] |> Request.transport false endpoint
        match! Http.exchange net endpoint request Timeouts.defaults CancellationToken.None with
        | Error error -> return Error error
        | Ok response ->
            use response = response
            return! response.ReadBody Limit
    }

/// A gigabyte packed into a megabyte stops inflating just past the limit.
[<Fact>]
let ``stops inflating a gzip bomb at the limit`` () =
    task {
        let started = DateTime.UtcNow
        let! outcome = readGzipped (gzipBomb 1024)
        Assert.Equal(Ok TooLarge, outcome)
        // Inflating the whole gigabyte would take minutes; stopping at the limit takes about a second.
        Assert.True((DateTime.UtcNow - started) < TimeSpan.FromSeconds 10.0, $"{DateTime.UtcNow - started}")
    }

[<Fact>]
let ``inflates bodies within the limit`` () =
    task {
        match! readGzipped (gzipBomb 3) with
        | Ok(Complete body) -> Assert.Equal<byte[]>(Array.zeroCreate (3 * 1024 * 1024), body)
        | other -> failwith $"{other}"
    }
