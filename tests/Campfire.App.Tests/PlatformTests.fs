// Port of the tests of rust/crates/campfire/src/concerns/platform.rs
module Campfire.App.Tests.PlatformTests

open System
open System.Collections.Generic
open System.Text.Json
open Xunit
open Campfire.App
open Campfire.App.UserAgent
open Campfire.Tests
open Campfire.Views
open Campfire.App.Tests.UserAgentTests

[<Fact>]
let ``matches application platform and allow browser`` () =
    let failures = List<string>()
    for case in (vectors ()).GetProperty("user_agents").EnumerateArray() do
        let ua = userAgentOf case
        let platform = ApplicationPlatform.create ua
        let browser = Agent.tryBrowser platform.Agent
        let operatingSystem = ApplicationPlatform.tryOperatingSystem platform
        let expected = case.GetProperty "application_platform"
        let label = case.GetProperty("ua").ToString()
        let field (name: string) (actual: Rb<string option>) = check failures $"{label} {name}" (expected.GetProperty name) actual
        field "ios" (Ok(flag (ApplicationPlatform.ios platform)))
        field "android" (Ok(flag (ApplicationPlatform.android platform)))
        field "mac" (Ok(flag (ApplicationPlatform.mac platform)))
        field "chrome" (ApplicationPlatform.browserMatches browser ApplicationPlatform.Chrome |> Result.map flag)
        field "firefox" (ApplicationPlatform.browserMatches browser ApplicationPlatform.Firefox |> Result.map flag)
        field "safari" (ApplicationPlatform.browserMatches browser ApplicationPlatform.Safari |> Result.map flag)
        field "edge" (ApplicationPlatform.browserMatches browser ApplicationPlatform.Edge |> Result.map flag)
        field "apple_messages" (Ok(flag (ApplicationPlatform.appleMessages platform)))
        field "mobile" (Ok(flag (ApplicationPlatform.mobile platform)))
        field "desktop" (Ok(flag (ApplicationPlatform.desktop platform)))
        field "windows" (ApplicationPlatform.isWindows operatingSystem |> Result.map flag)
        field "operating_system" (operatingSystem |> Result.map text)
        field "browser" (browser |> Result.map text)

        // The view answers false, or "", where Ruby raises.
        let isTrue (name: string) = expected.GetProperty(name).ValueKind = JsonValueKind.True
        let string (name: string) =
            match expected.GetProperty(name).ValueKind with
            | JsonValueKind.String -> str (expected.GetProperty name)
            | _ -> ""
        let expectedView: Platform =
            { Ios = isTrue "ios"
              Android = isTrue "android"
              Mac = isTrue "mac"
              Windows = isTrue "windows"
              Chrome = isTrue "chrome"
              Firefox = isTrue "firefox"
              Safari = isTrue "safari"
              Edge = isTrue "edge"
              Mobile = isTrue "mobile"
              Desktop = isTrue "desktop"
              AppleMessages = isTrue "apple_messages"
              Browser = string "browser"
              OperatingSystem = string "operating_system" }
        let view = ApplicationPlatform.toView platform
        if view <> expectedView then failures.Add $"{label} view: expected {expectedView}, got {view}"

        check failures $"{label} blocked" (case.GetProperty "blocked") (ApplicationPlatform.tryBrowserBlocked platform |> Result.map flag)
    Assert.True(failures.Count = 0, $"{failures.Count} mismatches:\n{String.Join('\n', failures)}")

[<Fact>]
let ``view platform uses empty strings for nil`` () =
    let view = ApplicationPlatform.toView (ApplicationPlatform.create "curl/8.4.0")
    Assert.Equal("", view.OperatingSystem)
    Assert.Equal("curl", view.Browser)
    Assert.True view.Desktop
