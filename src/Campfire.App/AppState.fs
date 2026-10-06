// Port of `AppState`, `AppCtx` and `db_error` in rust/crates/campfire/src/app.rs
//
// `AppState` is what controllers, channels, jobs and integrations share. Actions reach it with
// `c.App` (`CtxApp`). Boot (`App.boot`) builds it.
namespace Campfire.App

open System
open System.Threading.Tasks
open Campfire.App.Channels
open Campfire.Db
open Campfire.Kit
open Campfire.RailsCompat
open Campfire.RailsCompat.Clock
open Campfire.Storage
open Campfire.Views

module AppErrors =
    /// A database error raised in an action, as Active Record's `rescue_responses` answer it
    /// (activerecord/lib/active_record/railtie.rb): `RecordNotFound` is a 404, `RecordInvalid` a 422,
    /// and anything else a 500.
    let dbError (error: DbError) : Campfire.Kit.Error =
        match error with
        | RecordNotFound _ -> Campfire.Kit.Error.NotFound
        | RecordInvalid _ -> Campfire.Kit.Error.WithStatus(Status.UnprocessableEntity, Exception(DbError.display error))
        | Sqlite e -> Campfire.Kit.Error.Internal e
        | Other e -> Campfire.Kit.Error.Internal e
        | WriterGone -> Campfire.Kit.Error.Internal(Exception(DbError.display error))

/// `config.x.web_push_pool`: what the app shuts down with. The pool itself is the integrations'.
type IWebPushPool =
    abstract Shutdown: unit -> Task

/// Everything that outlives a request.
[<Sealed; NoComparison; NoEquality>]
type AppState
    (
        config: AppConfig,
        secrets: Secrets,
        clock: SharedClock,
        db: Database,
        storage: Storage,
        cable: Cable,
        broadcasts: Broadcasts,
        jobs: Jobs,
        webPush: IWebPushPool option,
        fragmentCache: FragmentCache
    ) =
    member _.Config = config
    member _.Secrets = secrets
    member _.Clock = clock
    member _.Db = db
    member _.Storage = storage
    member _.Cable = cable
    member _.Broadcasts = broadcasts
    member _.Jobs = jobs

    /// `config.x.web_push_pool`; `None` when Web Push is off (no valid VAPID keys).
    member _.WebPush = webPush

    /// `Rails.cache` for view fragments (`cache message do`), current during every request and every
    /// render outside one.
    member _.FragmentCache = fragmentCache

    /// The key pages offer browsers to subscribe with: none while Web Push is off, so that browsers
    /// don't subscribe to notifications that would never be sent.
    member _.VapidPublicKey: string option =
        match webPush with
        | Some _ -> config.VapidPublicKey
        | None -> None

    /// `db.Read` for actions, whose errors become responses as `AppErrors.dbError` maps them. (Jobs
    /// and channels use `db.Read`, for a `DbError` result.)
    member _.Read<'T>(f: Conn -> 'T) : Task<Result<'T, Campfire.Kit.Error>> =
        task {
            let! result = db.Read f
            return result |> Result.mapError AppErrors.dbError
        }

    /// `db.ReadOffloaded` for actions (reads whose cost grows with the whole database), with errors
    /// mapped as `Read` maps them.
    member _.ReadOffloaded<'T>(f: Conn -> 'T) : Task<Result<'T, Campfire.Kit.Error>> =
        task {
            let! result = db.ReadOffloaded f
            return result |> Result.mapError AppErrors.dbError
        }

    /// `db.Write` for actions, whose errors become responses as `AppErrors.dbError` maps them.
    member _.Write<'T>(f: Tx -> 'T) : Task<Result<'T, Campfire.Kit.Error>> =
        task {
            let! result = db.Write f
            return result |> Result.mapError AppErrors.dbError
        }

type App = AppState

/// `c.app()` in actions.
[<AutoOpen>]
module CtxApp =
    type Campfire.Kit.Ctx with
        member c.App: AppState = c.State<AppState>()
