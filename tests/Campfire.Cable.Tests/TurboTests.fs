// Port of the tests in rust/crates/cable/src/turbo.rs
module Campfire.Cable.Tests.TurboTests

open System
open System.Text
open Xunit
open Campfire.RailsCompat
open Campfire.Cable
open Campfire.Cable.Turbo

[<Fact>]
let ``append tag`` () =
    Assert.Equal(
        """<turbo-stream action="append" target="messages_room_1"><template><div id="m">Hi &amp; bye</div></template></turbo-stream>""",
        actionTag Append (Target "messages_room_1") (Some "<div id=\"m\">Hi &amp; bye</div>") []
    )

[<Fact>]
let ``remove tag has no template`` () =
    Assert.Equal(
        """<turbo-stream action="remove" target="message_1"></turbo-stream>""",
        actionTag Remove (Target "message_1") None []
    )

[<Fact>]
let ``attributes come before action and are not dasherized`` () =
    Assert.Equal(
        """<turbo-stream maintain_scroll="true" action="replace" target="presentation_message_1"><template>x</template></turbo-stream>""",
        actionTag Replace (Target "presentation_message_1") (Some "x") [ "maintain_scroll", Some "true" ]
    )

[<Fact>]
let ``targets and escaping`` () =
    Assert.Equal(
        """<turbo-stream action="update" targets="#a &gt; b[data-x=&#39;1&#39;]"><template></template></turbo-stream>""",
        actionTag Update (Targets "#a > b[data-x='1']") None []
    )

[<Fact>]
let ``refresh tags`` () =
    Assert.Equal("""<turbo-stream action="refresh"></turbo-stream>""", refreshTag None)
    Assert.Equal("""<turbo-stream request-id="abc" action="refresh"></turbo-stream>""", refreshTag (Some "abc"))

// Not in the Rust tests: a broadcast writes the tag straight into JSON, and must send what encoding
// the tag as a string would.

type private Recorder() =
    member val Sent = Collections.Generic.List<string * string>()

    interface IServer with
        member _.Config = Config.defaults
        member _.Hub = Hub(1)
        member _.Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
        member this.Broadcast(b, v) = this.Sent.Add((b, Json.encode v)); 1
        member this.BroadcastText(b, t) = this.Sent.Add((b, Json.encode (Value.String t))); 1
        member this.BroadcastEncoded(b, json) = this.Sent.Add((b, Encoding.UTF8.GetString json)); 1

[<Fact>]
let ``broadcast helpers send the encoded tag, to the joined stream name`` () =
    let server = Recorder()
    let html = "<div id=\"m\" class='x'>Hi &amp; ☃ \"you\"</div>\n"
    Assert.Equal(1, broadcastAppendTo server [ "Z2lk"; "messages" ] "messages" html)
    Assert.Equal(1, broadcastActionTo server [ "rooms" ] Replace (Targets "#a > b") (HtmlUtf8(ReadOnlyMemory(Encoding.UTF8.GetBytes html))) [ "maintain_scroll", Some "true"; "skipped", None ])
    Assert.Equal(1, broadcastRemoveTo server [ "rooms" ] "list_room_1")
    Assert.Equal(1, broadcastRefreshTo server [ "rooms" ] (Some "abc"))
    Assert.Equal(1, broadcastStreamTo server [ "rooms" ] "<raw/>")
    let expected =
        [ "Z2lk:messages", Json.encode (Value.String(actionTag Append (Target "messages") (Some html) []))
          "rooms",
          Json.encode (Value.String(actionTag Replace (Targets "#a > b") (Some html) [ "maintain_scroll", Some "true" ]))
          "rooms", Json.encode (Value.String(actionTag Remove (Target "list_room_1") None []))
          "rooms", Json.encode (Value.String(refreshTag (Some "abc")))
          "rooms", Json.encode (Value.String "<raw/>") ]
    Assert.Equal<(string * string) list>(expected, List.ofSeq server.Sent)

[<Fact>]
let ``blank streamables are dropped, and nothing is sent when none are left`` () =
    let server = Recorder()
    Assert.Equal(1, broadcastStreamTo server [ " "; "rooms"; "" ] "x")
    Assert.Equal(0, broadcastStreamTo server [ ""; "  " ] "x")
    Assert.Equal(0, broadcastRemoveTo server [] "x")
    Assert.Equal<(string * string) list>([ "rooms", "\"x\"" ], List.ofSeq server.Sent)
