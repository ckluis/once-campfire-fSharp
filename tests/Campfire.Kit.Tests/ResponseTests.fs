// Ports of the #[cfg(test)] modules in rust/crates/kit/src/{response,exceptions,error}.rs
module Campfire.Kit.Tests.ResponseTests

open System
open System.Text
open Xunit
open Campfire.Kit
open Campfire.Kit.Tests.Helpers

// response.rs

let private values (value: string) : string list =
    let response = Response(Status.Ok).Header("x-test", "replaced").Header("x-test", value)
    response.Headers.GetAll "x-test"

[<Fact>]
let ``header values with control characters go out as puma writes them`` () =
    Assert.Equal<string list>([ "a\tb" ], values "a\tb")
    Assert.Equal<string list>([ "é" ], values "é")
    Assert.Equal<string list>([ "" ], values "")
    // Each line on its own, and a line with a control character left out.
    Assert.Equal<string list>([ "x"; "y; z" ], values "x\ny; z")
    Assert.Equal<string list>([ ""; "a" ], values "\na\n\n")
    Assert.Equal<string list>([ "y" ], values "x\r\ny")
    Assert.Empty(values "x\ry")
    Assert.Empty(values "x\001y")
    Assert.Empty(values "x\127y")
    Assert.Empty(values "\n")

[<Fact>]
let ``content disposition like rails`` () =
    let cd = Response.contentDisposition
    Assert.Equal("inline", cd "inline" null)
    Assert.Equal("attachment; filename=\"logo.png\"; filename*=UTF-8''logo.png", cd "attachment" "logo.png")
    Assert.Equal(
        "inline; filename=\"resume 1.pdf\"; filename*=UTF-8''r%C3%A9sum%C3%A9%201.pdf",
        cd "inline" "résumé 1.pdf"
    )
    Assert.Equal(
        "inline; filename=\"%3F%3F%22x%22.txt\"; filename*=UTF-8''%E6%97%A5%E6%9C%AC%22x%22.txt",
        cd "inline" "日本\"x\".txt"
    )
    // I18n's whole table, not just Latin-1 letters.
    Assert.Equal(
        "attachment; filename=\"Lodz x.pdf\"; filename*=UTF-8''%C5%81%C3%B3d%C5%BA%20%C3%97.pdf",
        cd "attachment" "Łódź ×.pdf"
    )

[<Fact>]
let ``cache control normalization`` () =
    let cc = { CacheControl.Empty with MaxAge = ValueSome 300UL; Public = true; StaleWhileRevalidate = ValueSome 604800UL }
    Assert.Equal("max-age=300, public, stale-while-revalidate=604800", cc.ToHeader())
    let cc = { CacheControl.Empty with MaxAge = ValueSome 0UL; MustRevalidate = true }
    Assert.Equal("max-age=0, private, must-revalidate", cc.ToHeader())
    let cc = { CacheControl.Empty with NoStore = true; MaxAge = ValueSome 5UL }
    Assert.Equal("no-store", cc.ToHeader())
    Assert.Null(CacheControl.Empty.ToHeader())

// exceptions.rs

let private header (response: Response) (name: string) : string = nonNull (response.GetHeader name)

[<Fact>]
let ``json errors`` () =
    let response = Exceptions.render ErrorPages.Empty Status.UnprocessableEntity (ValueSome Format.Json) false
    Assert.Equal("""{"status":422,"error":"Unprocessable Content"}""", bodyText response)

[<Fact>]
let ``html pages`` () =
    let pages = ErrorPages.Of [ 404, bytesOf "<h1>404</h1>" ]
    let response = Exceptions.render pages Status.NotFound (ValueSome Format.Html) false
    Assert.Equal(Status.NotFound, response.Status)
    Assert.Equal("<h1>404</h1>", bodyText response)
    Assert.Equal("text/html; charset=UTF-8", header response "content-type")
    let missing = Exceptions.render pages Status.BadRequest ValueNone false
    Assert.Equal(Status.BadRequest, missing.Status)
    Assert.Equal("", bodyText missing)

[<Fact>]
let ``xml and yaml errors`` () =
    let xml = Exceptions.render ErrorPages.Empty Status.NotAcceptable (ValueSome Format.Xml) false
    Assert.Equal(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<hash>\n  <status type=\"integer\">406</status>\n  <error>Not Acceptable</error>\n</hash>\n",
        bodyText xml
    )
    Assert.Equal("application/xml; charset=UTF-8", header xml "content-type")
    Assert.Equal("124", header xml "content-length")
    let yaml = Exceptions.render ErrorPages.Empty Status.NotAcceptable (ValueSome Format.Yaml) false
    Assert.Equal("---\n:status: 406\n:error: Not Acceptable\n", bodyText yaml)

[<Fact>]
let ``head errors are empty in the request format`` () =
    let head = Exceptions.render ErrorPages.Empty Status.NotFound (ValueSome Format.All) true
    Assert.Equal("*/*; charset=UTF-8", header head "content-type")
    Assert.Equal("0", header head "content-length")
    let head = Exceptions.render ErrorPages.Empty Status.NotFound ValueNone true
    Assert.Equal("text/html; charset=UTF-8", header head "content-type")

// error.rs

[<Fact>]
let ``with status answers the status and logs the cause`` () =
    let cause = Exception("validation failed", Exception("name can't be blank"))
    let error = Error.withStatus Status.UnprocessableEntity cause
    Assert.Equal(Status.UnprocessableEntity, Error.status error)
    Assert.Equal("422 Unprocessable Entity: validation failed: name can't be blank", Error.display error)
