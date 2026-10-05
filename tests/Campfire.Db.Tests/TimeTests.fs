// Port of the tests in rust/crates/db/src/time.rs
module Campfire.Db.Tests.TimeTests

open System
open Xunit
open Campfire.Db

[<Fact>]
let ``impossible dates dont parse`` () =
    Assert.True((Timestamp.ParseDb "2026-13-01 00:00:00").IsNone)
    Assert.True((Timestamp.ParseDb "2026-02-30 00:00:00").IsNone)

let private parse (text: string) = (Timestamp.ParseDb text).Value

[<Fact>]
let ``encodes like active record`` () =
    Assert.Equal("2026-09-26 12:34:56.123456", (parse "2026-09-26 12:34:56.123456").ToDb())
    Assert.Equal("2026-09-26 12:34:56", (parse "2026-09-26 12:34:56").ToDb())
    Assert.Equal("2026-09-26 12:34:56.120000", (parse "2026-09-26 12:34:56.120000").ToDb())
    Assert.Equal("2026-01-01 00:00:00.000001", (parse "2026-01-01 00:00:00.000001").ToDb())

[<Fact>]
let ``reads sqlite strftime milliseconds`` () =
    Assert.Equal("2026-09-26 12:25:26.826000", (parse "2026-09-26 12:25:26.826").ToDb())

[<Fact>]
let ``truncates to microseconds`` () =
    // jiff's `Timestamp::new(1_700_000_000, 123_456_999)`: .NET keeps 100 ns ticks, so 123_456_900 ns.
    let at = DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000.0).AddTicks(1_234_569L)
    Assert.Equal(123_456, (Timestamp.FromDateTimeOffset at).SubsecMicrosecond)

[<Fact>]
let ``rejects garbage`` () =
    Assert.True((Timestamp.ParseDb "yesterday").IsNone)
    Assert.True((Timestamp.ParseDb "2026-09-26 12:34:56.x").IsNone)

[<Fact>]
let ``tolerates a T separator and a trailing Z or UTC`` () =
    let expected = (parse "2026-09-26 12:34:56.5").ToDb()
    Assert.Equal(expected, (parse "2026-09-26T12:34:56.5Z").ToDb())
    Assert.Equal(expected, (parse "2026-09-26 12:34:56.5 UTC").ToDb())
