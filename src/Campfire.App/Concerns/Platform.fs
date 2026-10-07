// Port of rust/crates/campfire/src/concerns/platform.rs
//
// `ApplicationPlatform` (reference/app/models/application_platform.rb, over platform_agent 1.0.1)
// and the `allow_browser` check from reference/app/controllers/concerns/allow_browser.rb.
namespace Campfire.App

open System
open Campfire.App.UserAgent
open Campfire.Views

/// `ApplicationPlatform.new(request.user_agent)`. Predicates marked "raises" in Ruby (a nil
/// `user_agent.browser`, which Rails turns into a 500) answer false here.
type ApplicationPlatform =
    internal
        { UserAgentString: string
          Agent: Agent }

module ApplicationPlatform =
    let create (userAgent: string | null) : ApplicationPlatform =
        // `match?` works on `user_agent_string.to_s`, and UserAgent.parse treats nil like "".
        let userAgentString = match userAgent with null -> "" | value -> value
        { UserAgentString = userAgentString
          Agent = UserAgent.parse userAgentString }

    let private matches (platform: ApplicationPlatform) (needle: string) : bool =
        platform.UserAgentString.Contains(needle, StringComparison.Ordinal)

    let ios (platform: ApplicationPlatform) : bool = matches platform "iPhone" || matches platform "iPad"

    let android (platform: ApplicationPlatform) : bool = matches platform "Android"

    let mac (platform: ApplicationPlatform) : bool = matches platform "Macintosh"

    /// Apple Messages link previews claim to be both the Facebook and Twitter bots.
    let appleMessages (platform: ApplicationPlatform) : bool =
        let lowercased = Helpers.Application.toLowercase platform.UserAgentString
        lowercased.Contains("facebookexternalhit", StringComparison.Ordinal) && lowercased.Contains("twitterbot", StringComparison.Ordinal)

    let mobile (platform: ApplicationPlatform) : bool = ios platform || android platform

    let desktop (platform: ApplicationPlatform) : bool = not (mobile platform)

    /// `operating_system`: nil when the gem's `os` is nil.
    let internal tryOperatingSystem (platform: ApplicationPlatform) : Rb<string option> =
        match Agent.tryPlatform platform.Agent with
        | Error e -> Error e
        | Ok agentPlatform ->
            let agentPlatform = defaultArg agentPlatform ""
            let named =
                [ "Android", "Android"; "iPad", "iPad"; "iPhone", "iPhone"; "Macintosh", "macOS"; "Windows", "Windows"; "CrOS", "ChromeOS" ]
                |> List.tryFind (fun (needle, _) -> agentPlatform.Contains(needle, StringComparison.Ordinal))
            match named with
            | Some(_, name) -> Ok(Some name)
            | None ->
                match Agent.tryOs platform.Agent with
                | Error e -> Error e
                | Ok os ->
                    Ok(os |> Option.map (fun os -> if os.Contains("Linux", StringComparison.Ordinal) then "Linux" else os))

    /// What `chrome?`, `firefox?`, `safari?` and `edge?` look for in the gem's `browser`.
    let internal Chrome = [ "Chrome" ]
    let internal Firefox = [ "Firefox"; "FxiOS" ]
    let internal Safari = [ "Safari" ]
    let internal Edge = [ "Edg" ]

    /// `user_agent.browser.match?(/A|B/)`, which raises for a nil browser.
    let internal browserMatches (browser: Rb<string option>) (names: string list) : Rb<bool> =
        match browser with
        | Error e -> Error e
        | Ok None -> Error Raised
        | Ok(Some browser) -> Ok(names |> List.exists (fun name -> browser.Contains(name, StringComparison.Ordinal)))

    /// `windows?`: `operating_system == "Windows"`.
    let internal isWindows (operatingSystem: Rb<string option>) : Rb<bool> =
        operatingSystem |> Result.map (fun os -> os = Some "Windows")

    /// The platform as the views see it. `chrome?`, `firefox?`, `safari?` and `edge?` all read the
    /// gem's `browser`, and `windows?` reads `operating_system`, so each is worked out once here.
    /// `browser` (delegated to the gem) and `operating_system` are "" when nil or raised.
    let toView (platform: ApplicationPlatform) : Platform =
        let browser = Agent.tryBrowser platform.Agent
        let operatingSystem = tryOperatingSystem platform
        let browserIs names = browserMatches browser names |> Result.defaultValue false
        let text (value: Rb<string option>) = match value with Ok(Some value) -> value | _ -> ""
        { Ios = ios platform
          Android = android platform
          Mac = mac platform
          Windows = isWindows operatingSystem |> Result.defaultValue false
          Chrome = browserIs Chrome
          Firefox = browserIs Firefox
          Safari = browserIs Safari
          Edge = browserIs Edge
          Mobile = mobile platform
          Desktop = desktop platform
          AppleMessages = appleMessages platform
          Browser = text browser
          OperatingSystem = text operatingSystem }

    let internal tryBrowserBlocked (platform: ApplicationPlatform) : Rb<bool> =
        if not (UserAgent.isPresent platform.UserAgentString) then
            Ok false
        else
            let agent = platform.Agent
            match Agent.tryVersion agent with
            | Error e -> Error e
            | Ok version ->
                match version |> Option.filter Version.isPresent with
                | None -> Ok false
                | Some version ->
                    match Agent.tryBrowser agent with
                    | Error e -> Error e
                    | Ok None -> Error Raised
                    | Ok(Some browser) ->
                        // `None` means the browser isn't version-guarded; `Some None` is `ie: false`, always blocked.
                        let minimum =
                            match browser.ToLowerInvariant() with
                            | "safari" -> Some(Some "17.2")
                            | "chrome" -> Some(Some "120")
                            | "firefox" -> Some(Some "121")
                            | "opera" -> Some(Some "104")
                            | "internet explorer" -> Some None
                            | _ -> None
                        match minimum with
                        | None -> Ok false
                        | Some minimum ->
                            let belowMinimum =
                                match minimum with
                                | None -> true
                                | Some minimum -> Version.lessThan version (Version.create minimum)
                            Ok(belowMinimum && not (Agent.isBot agent))

    /// `ActionController::AllowBrowser::BrowserBlocker#blocked?` with Campfire's
    /// `AllowBrowser::VERSIONS = { safari: 17.2, chrome: 120, firefox: 121, opera: 104, ie: false }`.
    /// The blocker parses the header itself; this reads the platform's parse of the same string.
    /// Rails raises (a 500) for a versioned agent with a nil browser; that is not blocked here.
    let browserBlocked (platform: ApplicationPlatform) : bool =
        tryBrowserBlocked platform |> Result.defaultValue false
