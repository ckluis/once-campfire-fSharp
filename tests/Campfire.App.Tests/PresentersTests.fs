// Port of the part of the tests of rust/crates/campfire/src/controllers/presenters.rs for the helpers
// `Presenters/Common.fs` has (`to_fs_number`, `cache_key_with_version`). The tests of the message
// presenter's `all_emoji` and Jbuilder keys follow with the message controllers.
module Campfire.App.Tests.PresentersTests

open System
open Xunit
open Campfire.App.Presenters
open Campfire.Db
open Campfire.Views

[<Fact>]
let ``cache versions use usec`` () =
    let time = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2024-06-01T12:00:00Z").AddTicks(1230L))
    Assert.Equal("messages/1-20240601120000000123", FragmentCache.cacheKeyWithVersion "messages" 1L (time.ToDateTimeOffset()))
    Assert.Equal("20240601120000", Common.toFsNumber time)
