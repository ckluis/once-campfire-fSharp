// Port of rust/crates/kit/src/app.rs
//
// The shared, per-process part of the HTTP layer: configuration, secrets, clock and app state.
namespace Campfire.Kit

open System
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open Campfire.RailsCompat
open Campfire.RailsCompat.Clock
open Campfire.Kit

module KitDefaults =
    /// `action_dispatch.default_headers` (`load_defaults 7.1`).
    let railsDefaultHeaders: (string * string)[] =
        [| "x-frame-options", "SAMEORIGIN"
           "x-xss-protection", "0"
           "x-content-type-options", "nosniff"
           "x-permitted-cross-domain-policies", "none"
           "referrer-policy", "strict-origin-when-cross-origin" |]

type KitConfig =
    { Proxy: ProxyConfig
      /// `config.force_ssl`: redirect plain HTTP to HTTPS, send HSTS, flag cookies `secure`.
      ForceSsl: bool
      /// The HSTS header value sent with `ForceSsl` (`ssl_options = { hsts: { subdomains: true } }`).
      Hsts: string
      Session: SessionConfig
      /// `forgery_protection_origin_check` (on since `load_defaults 5.0`).
      ForgeryProtectionOriginCheck: bool
      /// `action_dispatch.default_headers` (`load_defaults 7.1`).
      DefaultHeaders: (string * string)[]
      /// `public/404.html`, `422.html`, `500.html`, ... (`ActionDispatch::PublicExceptions`).
      ErrorPages: ErrorPages
      /// Largest request body accepted; none is unlimited, like Puma.
      MaxBodyBytes: int voption }

    static member Default: KitConfig =
        { Proxy = ProxyConfig.Default
          ForceSsl = false
          Hsts = "max-age=63072000; includeSubDomains"
          Session = SessionConfig.Default
          ForgeryProtectionOriginCheck = true
          DefaultHeaders = KitDefaults.railsDefaultHeaders
          ErrorPages = ErrorPages.Empty
          MaxBodyBytes = ValueNone }

    /// Campfire's production settings: `assume_ssl` and `force_ssl` unless `DISABLE_SSL` is set
    /// (`reference/config/environments/production.rb`).
    static member Production(disableSsl: bool) : KitConfig =
        let config = KitConfig.Default
        { config with
            Proxy = { config.Proxy with AssumeSsl = not disableSsl }
            ForceSsl = not disableSsl }

/// Everything a request needs that outlives it.
[<Sealed>]
type Kit(config: KitConfig, secrets: Secrets, clock: SharedClock, state: objnull, logger: ILogger) =
    /// `state` is the application's own state (database handles etc.), reachable from actions
    /// with `Ctx.State`.
    new(config: KitConfig, secrets: Secrets, clock: SharedClock, state: objnull) =
        Kit(config, secrets, clock, state, NullLogger.Instance)

    member _.Config = config

    /// What cookies are signed and encrypted with.
    member _.Secrets = secrets

    member _.Clock = clock

    /// Where rejected and failed requests are logged.
    member _.Logger = logger

    member _.ErrorPages = config.ErrorPages

    member _.State<'S when 'S: not struct>() : 'S =
        match state with
        | :? 'S as state -> state
        | _ -> invalidOp "Kit state has a different type"
