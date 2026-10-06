// Port of the tests inside the account-side controllers of rust/crates/campfire/src/controllers:
// `unfurl_links.rs`, `users/avatars.rs` and `qr_code.rs`.
module Campfire.App.Tests.ControllerTests

open System.IO
open System.Threading.Tasks
open Xunit
open Campfire.App.Controllers
open Campfire.App.Tests.Support
open Campfire.RailsCompat

// --- unfurl_links.rs -------------------------------------------------------------------------------

[<Fact>]
let ``unfurls nothing from private addresses and needs a url`` () =
    task {
        use! test = bootSeeded "default"
        let david = david test
        let post (pairs: (string * string) list) = david.Write((request "POST" "/unfurl_link").Form(formBody pairs))
        let! ``private`` = post [ "url", "http://127.0.0.1/secret" ]
        Assert.Equal(204, ``private``.Status)
        let! missing = post [ "url", "" ]
        Assert.Equal(400, missing.Status)
        let! hash = post [ "url[a]", "http://example.com" ]
        Assert.Equal(204, hash.Status)
        let! array = post [ "url[]", "http://example.com" ]
        Assert.Equal(204, array.Status)
    }

// --- users/avatars.rs ------------------------------------------------------------------------------

[<Fact>]
let ``concurrent first requests all get the whole file`` () =
    let read () =
        match Avatars.assetFile "default-bot-avatar.svg" with
        | Ok path -> File.ReadAllBytes path
        | Error e -> failwith (sprintf "%A" e)
    let contents = [| for _ in 1..16 -> Task.Run read |] |> Array.map (fun t -> t.GetAwaiter().GetResult())
    Assert.NotEmpty contents[0]
    Assert.True(contents |> Array.forall (fun c -> c = contents[0]))

// --- qr_code.rs ------------------------------------------------------------------------------------

[<Fact>]
let ``decodes like ruby urlsafe decode64`` () =
    let bytes (s: string) = System.Text.Encoding.UTF8.GetBytes s
    // `None` where Ruby raises ArgumentError.
    Assert.Equal<byte[]>(bytes "http://campfire.test", (RailsEncoding.urlsafeDecode "aHR0cDovL2NhbXBmaXJlLnRlc3Q").Value)
    Assert.Equal<byte[]>(bytes "http://campfire.test", (RailsEncoding.urlsafeDecode "aHR0cDovL2NhbXBmaXJlLnRlc3Q=").Value)
    Assert.Equal<byte[]>([| 0xfbuy; 0xffuy |], (RailsEncoding.urlsafeDecode "-_8").Value)
    Assert.Equal<byte[]>([| 0xfbuy; 0xffuy |], (RailsEncoding.urlsafeDecode "+/8").Value)
    Assert.Equal<byte[]>([||], (RailsEncoding.urlsafeDecode "").Value)
    for malformed in [ "a"; "ab="; "ab=c"; "aB=="; "a*bc" ] do
        Assert.True((RailsEncoding.urlsafeDecode malformed).IsNone, malformed)
