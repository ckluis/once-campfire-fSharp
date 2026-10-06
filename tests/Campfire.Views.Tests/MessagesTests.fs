// Ports of the #[cfg(test)] modules in rust/crates/views/src/messages/support.rs and searches.rs, and
// of `display_names` in rust/crates/views/tests/rooms_views.rs
module Campfire.Views.Tests.MessagesTests

open Xunit
open Campfire.RailsCompat
open Campfire.Views
open Campfire.Views.MessagesSupport

let private time (text: string) : Timestamp = (Timestamps.tryParse text).Value

// messages/support.rs

[<Fact>]
let ``epoch truncates through a float like ruby`` () =
    Assert.Equal(1790425426483L, epochMs (time "2026-09-26T12:23:46.483521Z"))
    Assert.Equal(1790421826000L, epochMs (time "2026-09-26T11:23:46Z"))

/// `epochMs` is arithmetic; `epochMsViaText` is the decimal string parsed as a double, which is what
/// `Time#to_f` is. They must agree on every timestamp, including the ones a float rounds either way.
[<Fact>]
let ``epoch by integer arithmetic is the float of the decimal string`` () =
    let epochTicks = System.DateTimeOffset.UnixEpoch.UtcTicks
    let ofTicks (ticks: int64) = System.DateTimeOffset(epochTicks + ticks, System.TimeSpan.Zero)
    let random = System.Random 20261005
    let check (ticks: int64) =
        let stamp = ofTicks ticks
        Assert.True(epochMsViaText stamp = epochMs stamp, $"{ticks} ticks after the epoch: {epochMs stamp} against {epochMsViaText stamp}")
    // The edges: the epoch, one tick, a millisecond either side of a second, today and the far future.
    for ticks in [ 0L; 1L; 9_999L; 10_000L; 10_001L; 9_999_999L; 10_000_000L; 10_000_001L; 17_904_254_264_835_210L; 2_534_023_007_999_999_999L ] do
        check ticks
    // Whole milliseconds, which the float scaling can truncate down by one.
    for _ in 1..20_000 do
        check (random.NextInt64(0L, 40_000_000_000_000L) * 10_000L)
    // Anything at 100 ns, across the range up to year 9999.
    for _ in 1..20_000 do
        check (random.NextInt64(0L, 2_534_023_007_999_999_999L))
    // And before 1970, which keeps the string path.
    for _ in 1..200 do
        check (-random.NextInt64(1L, 1_000_000_000_000_000L))

[<Fact>]
let ``formats numbers like ruby`` () =
    Assert.Equal("600.0", Float(600.0).ToString())
    Assert.Equal("1.7777777777777777", Float(16.0 / 9.0).ToString())
    Assert.Equal("1.0e+15", Float(1e15).ToString())
    Assert.Equal("1.0e-05", Float(0.00001).ToString())
    Assert.Equal("320", Int(641L).Half.ToString())
    Assert.Equal("-2", Int(-3L).Half.ToString())

[<Fact>]
let ``formats json times with milliseconds`` () =
    Assert.Equal("2026-09-26T12:26:46.848Z", jsonTime (time "2026-09-26T12:26:46.848999Z"))
    Assert.Equal("2026-09-26T12:26:46Z", iso8601 (time "2026-09-26T12:26:46.848999Z"))

// searches.rs

[<Fact>]
let ``search paths escape the query like cgi escape`` () =
    Assert.Equal("/searches?q=pizza+%26+%22pie%22+%2A~", Searches.searchPath "pizza & \"pie\" *~")

// rooms_views.rs

[<Fact>]
let ``display names`` () =
    Assert.Equal("HQ", Rooms.roomDisplayName (Some "HQ") false [] None)
    Assert.Equal("Jason and JZ", Rooms.roomDisplayName None true [ "Jason"; "JZ" ] (Some "David"))
    Assert.Equal("David", Rooms.roomDisplayName None true [] (Some "David"))

[<Fact>]
let ``a room knows its dom id and where it is edited`` () =
    let room: Rooms.RoomView = { Id = 3L; Kind = Messages.Direct; Name = None; DisplayName = "x" }
    Assert.Equal("rooms_direct_3", room.DomId "")
    Assert.Equal("involvement_rooms_direct_3", room.DomId "involvement")
    Assert.Equal("/rooms/directs/3/edit", room.EditPath)
    Assert.Equal("Ping", room.Noun)

// messages.rs: the keys of the fragments a message and a boost live under

[<Fact>]
let ``message fragments are keyed by version`` () =
    let at = time "2024-06-01T12:00:00.000123Z"
    let buffer = KeyBuf.Of ""
    Messages.messageFragmentKey buffer 7L at
    let key = buffer.ToString()
    Assert.StartsWith("views/messages/_message:", key)
    Assert.EndsWith("/messages/7-20240601120000000123/presentation-v3", key)
