// Port of the tests of rust/crates/campfire/src/integrations/webhook.rs
module Campfire.App.Tests.WebhookTests

open System
open System.Collections.Generic
open System.Net
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Campfire.App.Integrations
open Campfire.App.Tests.IntegrationsSupport
open Campfire.App.Tests.Support

let private b64 (s: string) : byte[] = Convert.FromBase64String s

type private Shown = { Status: int option; Error: string option; Reply: ShownReply }

and private ShownReply =
    | NoReply
    | Said of string
    | Attached of filename: string * contentType: string * data: byte[]

let private testdata (name: string) : JsonElement =
    let text = IO.File.ReadAllText(IO.Path.Combine(AppContext.BaseDirectory, "testdata", name))
    (JsonDocument.Parse text).RootElement.Clone()

let private headersOf (element: JsonElement) : (string * string) list =
    [ for pair in element.EnumerateArray() -> str pair[0], str pair[1] ]

let private property (element: JsonElement) (name: string) : JsonElement option =
    match element.TryGetProperty name with
    | true, value when value.ValueKind <> JsonValueKind.Null -> Some value
    | _ -> None

/// Replays testdata/webhook_cases.json and compares with what `Webhook#deliver` did in the reference
/// (testdata/webhook_expected.json, from the oracle script in Rust's testdata/oracle/webhook.rb).
[<Fact>]
let ``delivers like the reference`` () =
    task {
        let cases = [ for c in (testdata "webhook_cases.json").EnumerateArray() -> c ]
        let expected = [ for e in (testdata "webhook_expected.json").EnumerateArray() -> e ]
        let routes =
            [ for c in cases ->
                  let name = str (c.GetProperty "name")
                  let route = Route.create "POST" "*" ("/" + name) (c.GetProperty("status").GetInt32())
                  { route with
                      Headers = headersOf (c.GetProperty "headers")
                      Body =
                        (match property c "body_b64" with
                         | Some body -> b64 (str body)
                         | None -> Encoding.UTF8.GetBytes(match property c "body" with Some body -> str body | None -> ""))
                      Gzip = (match property c "gzip" with Some g -> g.GetBoolean() | None -> false)
                      Delay = TimeSpan.FromSeconds(match property c "delay" with Some d -> float (d.GetInt32()) | None -> 0.0) } ]
        use server = FakeServer.Start routes
        let net = Network.system ()

        let! outcomes =
            cases
            |> List.map (fun c ->
                let name = str (c.GetProperty "name")
                let url =
                    match property c "url" with
                    | Some url -> str url
                    | None -> $"http://{server.Addr}/{name}"
                WebhookClient.deliver net url """{"message":"hi"}""")
            |> Task.WhenAll

        for (case', expected), outcome in List.zip (List.zip cases expected) (List.ofArray outcomes) do
            let name = str (case'.GetProperty "name")
            let actual =
                match outcome with
                | Ok delivery ->
                    let reply =
                        match delivery.Reply with
                        | ReplyNone -> NoReply
                        | ReplyText text -> Said text
                        | ReplyAttachment a -> Attached(a.Filename, a.ContentType, a.Data)
                    { Status = delivery.Status; Error = None; Reply = reply }
                | Error(InvalidMimeType _) -> { Status = None; Error = Some "Mime::Type::InvalidMimeType"; Reply = NoReply }
                | Error(WebhookError.Http error) when HttpError.isConnectionRefused error ->
                    { Status = None; Error = Some "Errno::ECONNREFUSED"; Reply = NoReply }
                | Error error -> { Status = None; Error = Some(WebhookError.message error); Reply = NoReply }
            let wantedReply =
                match property expected "reply" with
                | None -> NoReply
                | Some reply ->
                    match property reply "attachment" with
                    | Some attachment ->
                        Attached(
                            str (attachment.GetProperty "filename"),
                            str (attachment.GetProperty "content_type"),
                            b64 (str (attachment.GetProperty "body_b64"))
                        )
                    | None -> Said(Encoding.UTF8.GetString(b64 (str (reply.GetProperty "text_b64"))))
            let wanted =
                match property expected "error" with
                | Some error -> { Status = None; Error = Some(str error); Reply = wantedReply }
                | None ->
                    { Status = property expected "status" |> Option.map (fun s -> s.GetInt32())
                      Error = None
                      Reply = wantedReply }
            Assert.True((actual = wanted), $"{name}: expected {wanted} but got {actual}")

        // The request as Net::HTTP sends it
        let request = server.Received() |> List.find (fun r -> r.Target = "/text")
        let recorded = (expected |> List.head).GetProperty("requests").EnumerateArray() |> Seq.head
        let wanted =
            headersOf (recorded.GetProperty "headers")
            |> List.map (fun (n, v) -> if n = "Host" then n, string server.Addr else n, v)
        Assert.Equal<(string * string) list>(wanted, request.Headers)
        Assert.Equal(str (recorded.GetProperty "body"), Encoding.UTF8.GetString request.Body)
    }

/// Unguarded: private and loopback addresses are fine, names resolve normally, and nothing is pinned.
[<Fact>]
let ``reaches internal services`` () =
    task {
        use server = FakeServer.Start [ Route.create "POST" "*" "/hook" 200 |> Route.header "Content-Type" "text/plain" |> Route.text "ok" ]
        let resolver = FakeResolver([ "bots.internal", [ "10.0.0.7" ] ])
        let dialer = MappingDialer(HashSet [ IPAddress.Parse "10.0.0.7" ], server.Addr)
        let net = network resolver dialer
        let! delivery = WebhookClient.deliver net "http://bots.internal:8080/hook" "{}"
        match delivery with
        | Ok delivery -> Assert.Equal({ Status = Some 200; Reply = ReplyText "ok" }, delivery)
        | Error error -> failwith (WebhookError.message error)
        Assert.Equal<string list>([ "bots.internal" ], resolver.Lookups)
        Assert.Equal<IPEndPoint list>([ IPEndPoint(IPAddress.Parse "10.0.0.7", 8080) ], dialer.Dialed)
        Assert.Equal(Some "bots.internal:8080", (List.head (server.Received())).Header "Host")
    }

/// An endpoint that keeps sending never trips the 7-second read timeout, but the delivery as a whole gives up and says
/// so.
[<Fact>]
let ``gives up on a trickling reply`` () =
    task {
        let handle, address = trickleServer "HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nConnection: close\r\n\r\n"
        use _server = handle
        let! delivery = WebhookClient.deliverWithin (Network.system ()) $"http://{address}/hook" "{}" (TimeSpan.FromSeconds 1.0)
        match delivery with
        | Ok delivery -> Assert.Equal({ Status = None; Reply = ReplyText "Failed to respond within 1 seconds" }, delivery)
        | Error error -> failwith (WebhookError.message error)
    }

/// A reply that inflates past the limit fails the delivery, having read little more than the limit.
[<Fact>]
let ``rejects replies over the limit`` () =
    task {
        let bomb = gzipBomb (WebhookClient.MaxReplySize / 1024 / 1024 + 1)
        let route =
            Route.create "POST" "*" "/hook" 200
            |> Route.header "Content-Type" "image/png"
            |> Route.header "Content-Encoding" "gzip"
            |> Route.body bomb
        use server = FakeServer.Start [ route ]
        let! outcome = WebhookClient.deliver (Network.system ()) $"http://{server.Addr}/hook" "{}"
        match outcome with
        | Error ReplyTooLarge -> ()
        | other -> failwith $"{other}"
    }

[<Fact>]
let ``looks up mime types like rails`` () =
    let lookup (s: string) =
        match WebhookClient.mimeLookup s with
        | Ok found -> found
        | Error error -> failwith (WebhookError.message error)
    Assert.Equal((Some "jpeg", "image/jpeg"), lookup "image/jpeg")
    Assert.Equal((Some "gzip", "application/gzip"), lookup "application/x-gzip")
    Assert.Equal((None, "IMAGE/PNG"), lookup "IMAGE/PNG")
    Assert.Equal((Some "html", "text/html"), lookup "text/html; charset=utf-8")
    Assert.Equal((None, "video/quicktime"), lookup "video/quicktime")
    for invalid in [ "image"; ""; "text/html, text"; "a/b c" ] do
        Assert.True((WebhookClient.mimeLookup invalid).IsError, invalid)
