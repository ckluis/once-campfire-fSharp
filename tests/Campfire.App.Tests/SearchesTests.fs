// Port of the tests in rust/crates/campfire/src/controllers/searches.rs (and the two of
// rust/crates/campfire/src/integrations/search.rs, which `SearchesController#query` is built on)
module Campfire.App.Tests.SearchesTests

open Xunit
open Campfire.App.Controllers
open Campfire.App.Tests.Support
open Campfire.Db

let private unwrap (result: Result<'T, DbError>) : 'T =
    match result with
    | Ok value -> value
    | Error e -> failwith (DbError.display e)

[<Fact>]
let ``searching records and clears recent searches`` () =
    task {
        use! test = bootSeeded "default"
        let david = david test
        let! empty = david.Get "/searches"
        Assert.Equal(200, empty.Status)
        Assert.Contains("<title>Search</title>", empty.Text)

        let! recorded = david.Write((request "POST" "/searches").Form(formBody [ "q", "hello, world" ]))
        Assert.Equal(Some "http://campfire.test/searches?q=hello++world", recorded.Location)
        let! results = david.Get "/searches?q=hello++world"
        Assert.Equal(200, results.Status)
        Assert.True(results.Text.Contains "“hello  world”", results.Text)

        let! cleared = david.Write(request "DELETE" "/searches/clear")
        Assert.Equal(Some "http://campfire.test/searches", cleared.Location)
        let! count = test.App.Db.Read(fun conn -> Search.countForUser conn DAVID)
        Assert.Equal(0L, unwrap count)

        let! missing = david.Write(request "POST" "/searches")
        Assert.Equal(500, missing.Status)
    }

/// The index reads the results, the recent searches and the last room visited together.
[<Fact>]
let ``the index shows results recent searches and the way back`` () =
    task {
        use! test = bootSeeded "default"
        let david = david test
        let exitTo (roomId: int64) =
            $"input-switcher; --btn-border-radius: 0.5em\" href=\"/rooms/{roomId}\""
        let! original = test.App.Db.Read(fun conn -> (Room.originalForUser conn DAVID).Value.Id)
        let original = unwrap original
        let! first = david.Get "/searches"
        Assert.True(first.Text.Contains(exitTo original), "no last room yet")

        let! posted =
            david.Write(
                ((request "POST" $"/rooms/{ALL_TALK}/messages").With("accept", "text/vnd.turbo-stream.html"))
                    .Form(formBody [ "message[body]", "Zanzibar at sunrise" ])
            )
        Assert.Equal(200, posted.Status)
        let! _ = david.Write((request "POST" "/searches").Form(formBody [ "q", "zanzibar" ]))
        let! _ = david.Get $"/rooms/{QUIET_CORNER}"

        let! page = david.Get "/searches?q=zanzibar"
        let page = page.Text
        Assert.True(page.Contains "Zanzibar at sunrise", page)
        Assert.Contains("""<span class="flex-item-no-shrink">1</span>""", page)
        // the recent search, in the nav and the sidebar
        let needle = "href=\"/searches?q=zanzibar\""
        let occurrences = (page.Split needle).Length - 1
        Assert.Equal(2, occurrences)
        Assert.Contains(exitTo QUIET_CORNER, page)
    }

[<Fact>]
let ``non word characters become spaces`` () =
    Assert.Equal(Some "hello  world ", SearchesController.query (Some "hello, world!"))
    Assert.Equal(Some "café_1 日本", SearchesController.query (Some "café_1 日本"))
    Assert.Equal(Some " quoted  AND x", SearchesController.query (Some "\"quoted\" AND-x"))
    Assert.Equal(None, SearchesController.query None)

// --- integrations/search.rs ------------------------------------------------------------------------

[<Fact>]
let ``keeps only word characters like ruby`` () =
    // Probed against the reference's Ruby
    Assert.Equal(
        Some "héllo wörld_1 ２ 日本語 ‿ a b \uFE0F   é",
        Campfire.App.Integrations.Search.sanitizeQuery (Some "héllo wörld_1 ２ 日本語 ‿ a-b \uFE0F ❤ é")
    )
    Assert.Equal(Some " quoted  OR NEAR x  ", Campfire.App.Integrations.Search.sanitizeQuery (Some "\"quoted\" OR NEAR(x*)"))
    Assert.Equal(Some "", Campfire.App.Integrations.Search.sanitizeQuery (Some ""))
    Assert.Equal(None, Campfire.App.Integrations.Search.sanitizeQuery None)

[<Fact>]
let ``classifies like onigmo`` () =
    // Probed against the reference's Ruby: alphabetic (including letter numbers and circled
    // letters), marks, decimal digits, connector punctuation and join controls.
    for c in [ 'a'; 'Z'; '0'; '_'; 'é'; 'ß'; '日'; '‿'; '́'; '٣'; 'ǅ'; 'ʰ'; 'Ⅻ'; 'Ⓐ'; '‍' ] do
        Assert.True(Campfire.App.Integrations.Search.isWord (int c), $"{c} is a word character")
    for c in [ ' '; '-'; '*'; '"'; ' '; '❤'; '€'; '½'; '²' ] do
        Assert.False(Campfire.App.Integrations.Search.isWord (int c), $"{c} is not a word character")
