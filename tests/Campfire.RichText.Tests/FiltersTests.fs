// Port of the tests in rust/crates/richtext/src/filters.rs
module Campfire.RichText.Tests.FiltersTests

open Xunit
open Campfire.RichText

[<Fact>]
let ``normalizes_tweet_urls_like_rails`` () =
    // `RemoveSoloUnfurledLinkText#normalize_tweet_url` in the reference.
    for (url, normalized) in
        [ "HTTPS://x.com/dhh/status/1?s=20", "https://twitter.com/dhh/status/1"
          "https://x.com/dhh/status/1?s=20", "https://twitter.com/dhh/status/1"
          "HTTP://twitter.com/a", "http://twitter.com/a"
          "Https://X.com/dhh/status/1", "Https://X.com/dhh/status/1" ] do
        Assert.Equal(Ok(Some normalized), Filters.normalizeTweetUrl (Some url))
