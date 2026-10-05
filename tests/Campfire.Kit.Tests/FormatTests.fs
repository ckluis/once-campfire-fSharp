// Port of the #[cfg(test)] module in rust/crates/kit/src/format.rs
module Campfire.Kit.Tests.FormatTests

open Xunit
open Campfire.Kit

let private symbols (formats: Format list) : string list = formats |> List.map (fun m -> m.Symbol)

let private fmts (accept: string | null) (path: string) (xhr: bool) : string list =
    match Format.formats { Format.emptyInput with Accept = accept; Path = path; Xhr = xhr } with
    | Ok formats -> symbols formats
    | Error e -> failwith $"{e}"

[<Fact>]
let ``browser accept falls back to html`` () =
    let chrome = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8"
    Assert.Equal<string list>([ "html" ], fmts chrome "/rooms/1" false)
    Assert.Equal<string list>([ "html" ], fmts null "/rooms/1" false)
    Assert.Equal<string list>([ "*/*" ], fmts "*/*" "/rooms/1" false)

[<Fact>]
let ``turbo form submission`` () =
    let turbo = "text/vnd.turbo-stream.html, text/html, application/xhtml+xml"
    Assert.Equal<string list>([ "turbo_stream"; "html" ], fmts turbo "/rooms/1/messages" false)

[<Fact>]
let ``quality ordering`` () =
    Assert.Equal<string list>([ "json"; "html" ], fmts "text/html;q=0.5, application/json" "/" false)
    Assert.Equal<string list>([ "html" ], fmts "application/json, */*" "/" false)
    Assert.Equal<string list>([ "html" ], fmts "*/*, application/json;q=0.1" "/" false)
    Assert.Equal<string list>([ "json"; "html" ], fmts "application/json;q=0.1,text/html;q=0.1" "/" false)

let private parseAccept (accept: string) : string list =
    match Format.parseAccept accept with
    | Ok formats -> symbols formats
    | Error e -> failwith $"{e}"

[<Fact>]
let ``q values read like rails`` () =
    // `Mime::Type.parse(header)` in the reference.
    for (accept, order) in
        [ "text/html;q=, application/json", [ "html"; "json" ]
          "text/html; q=, application/json;q=0.5", [ "html"; "json" ]
          "text/html;q=;q=, application/json;q=0.5", [ "html"; "json" ]
          "text/html;q=0.5, application/json;q=", [ "json"; "html" ]
          "text/html;q=;x=1, application/json", [ "json"; "html" ]
          "text/html;q=;q=0.9, application/json;q=0.5", [ "json"; "html" ]
          "text/html;q=0.4;q=0.9, application/json;q=0.5", [ "json"; "html" ]
          "text/html;q=0.5.1, application/json;q=0.6", [ "json"; "html" ]
          "text/html;q=0.5.1.2, application/json;q=0.49", [ "html"; "json" ]
          "text/html;q=\"0.9\", application/json;q=0.5", [ "html"; "json" ]
          "text/html;q=\"\"0.9, application/json;q=0.5", [ "json"; "html" ]
          "text/html;q=1e-1, application/json;q=0.5", [ "json"; "html" ]
          "text/html;q=1_0, application/json;q=5", [ "html"; "json" ]
          "text/html;q=abc, application/json;q=0.1", [ "json"; "html" ]
          "text/html;q=+0.3, application/json;q=0.2", [ "html"; "json" ]
          "text/html;q= 0.3, application/json;q=0.2", [ "html"; "json" ]
          "text/html;q=1e17, application/json;q=1e18", [ "json"; "html" ]
          "text/html;q=1e19, application/json;q=1e20", [ "json"; "html" ]
          "text/html;q=-0.001, application/json;q=0", [ "html"; "json" ]
          "application/json;q=0, text/html;q=-0.001", [ "json"; "html" ]
          // `String#to_f` reads hexadecimal after a sign only.
          "text/html;q=0.5, application/json;q=+0x1", [ "json"; "html" ]
          "text/html;q=0.5, application/json;q=0x1", [ "html"; "json" ] ] do
        Assert.True((order = parseAccept accept), $"{accept}: got {parseAccept accept}")
    Assert.Equal<string list>([ "json"; "*/*" ], parseAccept "*/*;q=, application/json;q=0.5")
    // Rails raises FloatDomainError (a 500) on an infinite q-value.
    Assert.Equal<string list>([ "html"; "json" ], parseAccept "text/html;q=1e400, application/json")
    Assert.Equal<string list>([ "json"; "html" ], parseAccept "text/html;q=-1e400, application/json")

[<Fact>]
let ``single types`` () =
    Assert.Equal<string list>([ "json" ], fmts "application/json" "/" false)
    Assert.Equal<string list>([ "json" ], fmts "application/json; charset=utf-8" "/" false)
    Assert.Equal<string list>([ "svg" ], fmts "image/svg+xml" "/" false)
    Assert.Equal<string list>([], fmts "application/x-unknown" "/" false)
    Assert.True((Format.formats { Format.emptyInput with Accept = "garbage"; Path = "/" }).IsError)

[<Fact>]
let ``wildcards expand in registration order`` () =
    Assert.Equal<string list>([ "html"; "text"; "js" ], fmts "text/*" "/" false |> List.truncate 3)
    Assert.Contains("turbo_stream", fmts "text/*" "/" false)

[<Fact>]
let ``xml folding`` () =
    Assert.Equal<string list>([ "xml"; "rss" ], fmts "text/xml, application/rss+xml" "/" false)
    Assert.Equal<string list>([ "rss"; "xml" ], fmts "application/xml, application/rss+xml" "/" false)
    Assert.Equal<string list>([ "html"; "xml" ], fmts "text/xml;q=0.9, application/xml;q=0.5, text/html" "/" false)

[<Fact>]
let ``path extension and format param`` () =
    Assert.Equal<string list>([ "svg" ], fmts null "/users/1/avatar.svg" false)
    Assert.Equal<string list>([ "json" ], fmts null "/messages.json" false)
    Assert.Equal<string list>([ "html" ], fmts null "/x.unknownext" false)
    let input = { Format.emptyInput with FormatParam = "json"; Accept = "text/html"; Path = "/" }
    Assert.Equal<string list>([ "json" ], symbols (Format.formats input |> Result.defaultValue []))
    let input = { Format.emptyInput with FormatParam = "nope"; Path = "/" }
    Assert.Empty(Format.formats input |> Result.defaultValue [ Format.Html ])

[<Fact>]
let ``xhr defaults to js`` () =
    Assert.Equal<string list>([ "js" ], fmts null "/" true)
    let input = { Format.emptyInput with Xhr = true; ContentType = "application/json"; Path = "/" }
    Assert.Equal<string list>([ "json" ], symbols (Format.formats input |> Result.defaultValue []))

[<Fact>]
let ``negotiation`` () =
    let negotiate formats order = Format.negotiate formats order
    Assert.Equal(ValueSome Format.Html, negotiate [ Format.TurboStream; Format.Html ] [ Format.Html; Format.Json ])
    Assert.Equal(ValueSome Format.Html, negotiate [ Format.All ] [ Format.Html; Format.Json ])
    Assert.Equal(ValueNone, negotiate [ Format.Png ] [ Format.Html; Format.Json ])
    Assert.Equal(ValueSome Format.Png, negotiate [ Format.Png ] [ Format.Html; Format.All ])
    Assert.Equal(ValueNone, negotiate [] [ Format.Html ])
