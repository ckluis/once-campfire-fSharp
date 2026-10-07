// Port of the #[cfg(test)] module in rust/crates/kit/src/clock.rs
module Campfire.Kit.Tests.ClockTests

open System
open Xunit
open Campfire.RailsCompat
open Campfire.Kit
open Campfire.Kit.Tests.Helpers

[<Fact>]
let ``twenty years is calendar years`` () =
    let t = ts "2024-02-29T00:00:00Z"
    Assert.Equal(ts "2044-02-29T00:00:00Z", KitClock.yearsFrom t 20)

[<Fact>]
let ``http dates`` () =
    Assert.Equal("Thu, 01 Jan 1970 00:00:00 GMT", KitClock.httpdate Timestamps.unixEpoch)
    Assert.Equal(Some Timestamps.unixEpoch, KitClock.parseHttpdate "Thu, 01 Jan 1970 00:00:00 GMT")
    Assert.Equal(None, KitClock.parseHttpdate "garbage")

[<Fact>]
let ``http dates in the other forms rfc 2822 allows`` () =
    let expected = Some(ts "2024-06-01T12:00:00Z")
    Assert.Equal(expected, KitClock.parseHttpdate "Sat, 01 Jun 2024 12:00:00 GMT")
    Assert.Equal(expected, KitClock.parseHttpdate "Sat, 1 Jun 2024 12:00:00 +0000")
    Assert.Equal(expected, KitClock.parseHttpdate "01 Jun 2024 14:00 +0200")
    Assert.Equal(expected, KitClock.parseHttpdate "Sat, 01 Jun 2024 07:00:00 EST")
    // A weekday that doesn't match the date is refused.
    Assert.Equal(None, KitClock.parseHttpdate "Mon, 01 Jun 2024 12:00:00 GMT")
    Assert.Equal(None, KitClock.parseHttpdate "Sat, 31 Jun 2024 12:00:00 GMT")

[<Fact>]
let ``the process clock is frozen by the environment`` () =
    let previous = Environment.GetEnvironmentVariable KitClock.FrozenTimeEnv
    try
        Environment.SetEnvironmentVariable(KitClock.FrozenTimeEnv, "2024-06-01T12:00:00Z")
        match KitClock.fromEnv () with
        | Ok clock -> Assert.Equal(ts "2024-06-01T12:00:00Z", clock.Now())
        | Error e -> failwith e
        Environment.SetEnvironmentVariable(KitClock.FrozenTimeEnv, "not a time")
        Assert.True((KitClock.fromEnv ()).IsError)
        Environment.SetEnvironmentVariable(KitClock.FrozenTimeEnv, " ")
        match KitClock.fromEnv () with
        | Ok clock -> Assert.True(abs ((clock.Now() - DateTimeOffset.UtcNow).TotalSeconds) < 5.0)
        | Error e -> failwith e
    finally
        Environment.SetEnvironmentVariable(KitClock.FrozenTimeEnv, previous)
