// Port of rust/crates/campfire/src/controllers/sessions.rs
//
// `SessionsController` (reference/app/controllers/sessions_controller.rb): sign in and out. Pulled
// into the boot unit because signing in is what proves the image boots; `sessions/transfers` follows
// with the rest of the controllers.
namespace Campfire.App.Controllers

open System
open System.Collections.Generic
open System.Threading.Tasks
open Campfire.RailsCompat
open Campfire.Kit
open Campfire.App
open Campfire.App.Presenters
open Campfire.Db
open Campfire.Routes
open Campfire.Views.Templates.Sessions

module Sessions =
    /// `rate_limit to: 10, within: 3.minutes, only: :create`
    [<Literal>]
    let private RateLimitTo = 10UL

    let private rateLimitWithin = TimeSpan.FromMinutes 3.0

    [<Literal>]
    let private Rejection = "Too many requests or unauthorized."

    let private newSize = Campfire.Views.RenderSize()

    /// `redirect_to first_run_url if User.none?`
    let private ensureUserExists (c: Ctx) : Task<Result<unit, Error>> =
        act {
            let! none = c.App.Read(fun conn -> Accounts.noUsers conn)
            if none then
                let firstRun = c.UrlFor(Routes.firstRun ())
                match c.RedirectTo firstRun with
                | Ok response -> return! halt response
                | Error e -> return! Error e
        }

    let private renderNew (c: Ctx) (status: int) : Task<Result<Response, Error>> =
        act {
            do! c.RespondTo [ Format.Html ] |> Result.map ignore
            let emailAddress = match c.ParamStr "email_address" with null -> None | email -> Some email
            let! helpContact = c.App.Read(fun conn -> Accounts.helpContact conn)
            return!
                Page.framedPage
                    c
                    status
                    newSize
                    (fun w ctx -> New.render w ctx emailAddress helpContact)
                    New.head
                    (fun w ctx -> New.content w ctx emailAddress helpContact)
        }

    /// `flash.now[:alert] = "Too many requests or unauthorized."; render :new, status:`
    let private renderRejection (c: Ctx) (status: int) : Task<Result<Response, Error>> =
        c.Flash().Now("alert", Rejection)
        renderNew c status

    // --- Rate limiting ---------------------------------------------------------------------------------

    /// The Rails cache entries `rate_limit` counts in: `"rate-limit:sessions:#{request.remote_ip}"`,
    /// incremented with `expires_in: within`, which (like Redis' `EXPIRE ... NX`) only sets the expiry
    /// when the counter starts. One process holds them all, like one Redis would.
    let private rateLimits = Dictionary<string, struct (uint64 * DateTimeOffset)>()

    /// Counts a hit under `key` in a fixed window that starts at the first hit.
    let increment (key: string) (now: DateTimeOffset) : uint64 =
        lock rateLimits (fun () ->
            for expired in [ for KeyValue(k, struct (_, expiresAt)) in rateLimits do if expiresAt <= now then k ] do
                rateLimits.Remove expired |> ignore
            let struct (count, expiresAt) =
                match rateLimits.TryGetValue key with
                | true, entry -> entry
                | _ -> struct (0UL, now + rateLimitWithin)
            rateLimits[key] <- struct (count + 1UL, expiresAt)
            count + 1UL)

    /// `rate_limiting(to:, within:, by: -> { request.remote_ip }, with: -> { render_rejection :too_many_requests })`
    let private rateLimit (c: Ctx) : Task<Result<unit, Error>> =
        act {
            let! ip = c.Request.RemoteIp()
            if increment $"rate-limit:sessions:{ip}" (c.Now()) > RateLimitTo then
                let! response = renderRejection c 429
                return! halt response
        }

    // --- Actions ---------------------------------------------------------------------------------------

    /// `allow_unauthenticated_access only: %i[ new create ]`, `before_action :ensure_user_exists, only: :new`
    let ``new`` (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c (Before.allowUnauthenticatedAccess Before.Default)
            do! ensureUserExists c
            return! renderNew c Status.Ok
        }

    let create (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c (Before.allowUnauthenticatedAccess Before.Default)
            do! rateLimit c
            let emailAddress = c.ParamStr "email_address"
            let password = c.ParamStr "password"
            let! user =
                match emailAddress, password with
                | null, _
                | _, null -> Task.FromResult(Ok None)
                | email, password -> Concerns.authenticateBy c email password
            match user with
            | Some user ->
                let! (_: Session) = Concerns.startNewSessionFor c user
                let location = Concerns.postAuthenticatingUrl c
                return! c.RedirectTo location
            | None -> return! renderRejection c 401
        }

    /// `Push::Subscription.destroy_by(endpoint: params[:push_subscription_endpoint], user_id: Current.user.id)`
    let private removePushSubscription (c: Ctx) : Task<Result<unit, Error>> =
        match c.ParamStr "push_subscription_endpoint", Concerns.currentUser c with
        | null, _
        | _, None -> Task.FromResult(Ok())
        | endpoint, Some user -> c.App.Write(fun tx -> PushSubscription.destroyByEndpoint tx user.Id endpoint)

    let destroy (c: Ctx) : Task<Result<Response, Error>> =
        act {
            do! Concerns.beforeActions c Before.Default
            do! removePushSubscription c
            do! Concerns.terminateCurrentSession c
            return! c.RedirectTo(c.UrlFor(Routes.root ()))
        }
