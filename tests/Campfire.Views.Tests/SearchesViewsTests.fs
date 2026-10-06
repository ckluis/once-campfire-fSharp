// Port of rust/crates/views/tests/searches_views.rs: DOM parity of the search views with the reference app
// (goldens in rust/crates/views/tests/golden/b, read here and never written).
module Campfire.Views.Tests.SearchesViewsTests

open Xunit
open Campfire.Views
open Campfire.Views.Differential.Inputs
open Campfire.Views.Tests.GoldenB

[<Fact>]
let ``index with results`` () =
    let g = golden "searches_index"
    let index = searchesIndexView g.Input
    g.AssertDom(Render.text (fun w -> Templates.Searches.Index.render w g.Context index))

[<Fact>]
let ``index without query`` () =
    let g = golden "searches_index_empty"
    let index = searchesIndexView g.Input
    g.AssertDom(Render.text (fun w -> Templates.Searches.Index.render w g.Context index))
