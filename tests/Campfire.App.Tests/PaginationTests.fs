// Port of the tests of rust/crates/campfire/src/controllers/presenters/pagination.rs
module Campfire.App.Tests.PaginationTests

open Xunit
open Campfire.App.Presenters

let private page (param: string | null) (count: int64) (perPage: int64 list) = Pagination.Page.Create(param, count, perPage)

[<Fact>]
let ``offsets with one ratio`` () =
    let second = page "2" 1005L [ 500L ]
    Assert.Equal((500L, 500L, 3L), (second.Offset, second.Limit, second.PageCount))
    Assert.False second.IsLast
    Assert.True (page "3" 1005L [ 500L ]).IsLast
    Assert.True (page null 0L [ 20L ]).IsLast

[<Fact>]
let ``offsets with default ratios`` () =
    // `GearedPagination::Ratios::DEFAULTS`
    let page (n: string) = page n 1000L [ 15L; 30L; 50L; 100L ]
    Assert.Equal((0L, 15L), ((page "1").Offset, (page "1").Limit))
    Assert.Equal((45L, 50L), ((page "3").Offset, (page "3").Limit))
    Assert.Equal((195L, 100L), ((page "5").Offset, (page "5").Limit))
    Assert.Equal((295L, 100L), ((page "6").Offset, (page "6").Limit))

[<Fact>]
let ``huge page numbers are capped`` () =
    let huge = page "99999999999999999999" 10L [ 5L ]
    Assert.Equal(1_000_000_000L, huge.Number)
    Assert.Equal(1_000_000_001L, huge.NextParam)
    Assert.True(huge.Offset > 0L)

[<Fact>]
let ``page params like ruby to i`` () =
    Assert.Equal(1L, (page "abc" 10L [ 5L ]).Number)
    Assert.Equal(1L, (page "-2" 10L [ 5L ]).Number)
    Assert.Equal(2L, (page " 2x" 10L [ 5L ]).Number)
    Assert.Equal(10L, (page "1_0" 100L [ 5L ]).Number)

[<Fact>]
let ``next links merge the page into sorted query values`` () =
    Assert.Equal(
        "http://x.test/autocompletable/users.json?page=2",
        Pagination.Url.withPage "http://x.test/autocompletable/users.json" "2"
    )
    Assert.Equal(
        "http://x.test/autocompletable/users.json?page=2&query=a%20b&room_id=3",
        Pagination.Url.withPage "http://x.test/autocompletable/users.json?query=a+b&page=1&room_id=3" "2"
    )

[<Fact>]
let ``plus is a space only before unencoding`` () =
    // Addressable's `query_values=` after `query_values` in the reference.
    for (query, next) in
        [ "query=a%2Bb", "page=2&query=a%2Bb"
          "q=a%2B+b&page=1", "page=2&q=a%2B%20b"
          "a+b=c+d", "a%2Bb=c%20d&page=2"
          "k=%20+", "k=%20%20&page=2"
          "k", "k&page=2" ] do
        Assert.Equal($"http://x.test/p?{next}", Pagination.Url.withPage $"http://x.test/p?{query}" "2")
