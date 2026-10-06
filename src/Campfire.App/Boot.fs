// Port of rust/crates/campfire/src/app.rs (boot, the HTTP stack and the commands)
//
// Boot: configuration, the database (`db:prepare`), storage, the cable server, jobs and the HTTP stack,
// wired the way the reference's middleware and initializers are. `AppState` (`AppState.fs`) is what
// controllers, channels, jobs and integrations share. Actions reach it with `c.App`.
//
// Request flow (mirroring the Rails middleware order): `Rack::Deflater` (config.ru) -> kit's pre-routing
// middleware (`ActionDispatch::SSL`, request id, `_method` override) -> public files
// (`ActionDispatch::Static`, from `Campfire.Assets`) -> `/cable` (Action Cable) or the Rails route table
// (`RouteTable.dispatch`). The `/cable` endpoint is Falco's; the route table is what Falco falls back to
// for every other path, so ASP.NET Core's own routing never sees (and never rewrites) a path.
namespace Campfire.App

open System
open System.IO
open System.Threading.Tasks
open Falco
open Falco.Routing
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Logging
open Campfire.App.Channels
open Campfire.App.Integrations
open Campfire.Assets
open Campfire.Db
open Campfire.RailsCompat
open Campfire.RailsCompat.Clock
open Campfire.Kit
open Campfire.Storage
open Campfire.Views

/// A booted app: its state, the HTTP pipeline, and the job runner.
[<NoComparison; NoEquality>]
type Booted =
    { App: AppState
      /// What `Front.serve` builds the app's pipeline with (the kit's `Adapter.app` and what the app puts in it).
      Pipeline: IApplicationBuilder -> unit
      Jobs: Runner }

module Boot =
    // --- The HTTP stack ----------------------------------------------------------------------------------

    /// The error pages kit renders (`ActionDispatch::PublicExceptions`), from the embedded `public/`.
    let errorPages () : ErrorPages =
        ErrorPages.Of
            [ for status in [ 404; 422; 500; 502 ] do
                  match Assets.serve (Serve.StaticRequest.create "GET" $"/{status}.html") with
                  | Some page -> status, page.Body
                  | None -> () ]

    /// The raw request path, still percent-encoded and without its query (`request.uri().path()`).
    let private rawPath (http: HttpContext) : string =
        let target =
            match http.Features.Get<IHttpRequestFeature>() with
            | null -> http.Request.Path.Value
            | feature -> feature.RawTarget
        match target with
        | null
        | "" -> "/"
        | target ->
            let target = match target.IndexOf '?' with -1 -> target | q -> target.Substring(0, q)
            if target.StartsWith '/' then
                target
            else
                // Absolute form: `http://host/path`.
                match target.IndexOf("://", StringComparison.Ordinal) with
                | -1 -> target
                | at ->
                    match target.IndexOf('/', at + 3) with
                    | -1 -> "/"
                    | slash -> target.Substring slash

    let private headerValue (http: HttpContext) (name: string) : string option =
        match http.Request.Headers[name].ToString() with
        | "" -> None
        | value -> Some value

    /// `ActionDispatch::Static`: the public file (including digested `/assets`) a request names, as the
    /// response the kit writes. The body is the embedded bytes themselves (never copied), and an empty
    /// one (HEAD, 304) keeps the `Content-Length` the served headers carry.
    let staticResponse (meth: string) (path: string) (acceptEncoding: string option) (range: string option) (ifModifiedSince: string option) : Response voption =
        match
            Assets.serve
                { Method = meth
                  Path = path
                  AcceptEncoding = acceptEncoding
                  Range = range
                  IfModifiedSince = ifModifiedSince }
        with
        | None -> ValueNone
        | Some served ->
            let response = Response(served.Status)
            if served.Body.IsEmpty then () else response.Body <- Body.Bytes served.Body
            response.StaticFile <- true
            for (name, value) in served.Headers do
                if HeaderMap.IsValidValue value then response.Headers.Append(name.ToLowerInvariant(), value)
            ValueSome response

    /// `ActionDispatch::Static`: serve `public/` (including digested `/assets`) before routing.
    let publicFiles (kit: Kit) : Func<HttpContext, RequestDelegate, Task> =
        Func<HttpContext, RequestDelegate, Task>(fun http next ->
            let request = http.Request
            match
                staticResponse
                    request.Method
                    (rawPath http)
                    (headerValue http "accept-encoding")
                    (headerValue http "range")
                    (headerValue http "if-modified-since")
            with
            | ValueSome response -> Adapter.write kit http response (request.Method = "HEAD")
            | ValueNone -> next.Invoke http)

    /// The Rails route table, with the app's fragment cache current while the action runs.
    let dispatchWithFragmentCache (c: Ctx) : Task<Result<Response, Error>> =
        FragmentCache.scoped c.App.FragmentCache (fun () -> RouteTable.dispatch c)

    /// The HTTP pipeline: public files, then `/cable`, then the Rails route table. `Rack::Deflater`
    /// (config.ru) is around the whole app, kit's pre-routing middleware inside it.
    let pipeline (app: AppState) (kit: Kit) (builder: IApplicationBuilder) : unit =
        Adapter.useDeflater kit builder |> ignore
        builder.Use(Adapter.railsMiddleware kit) |> ignore
        builder.Use(publicFiles kit) |> ignore
        builder.UseRouting() |> ignore
        builder.UseFalco [ any Campfire.Cable.Protocol.DefaultMountPath (fun http -> Campfire.Cable.Endpoint.call app.Cable http) ] |> ignore
        builder.UseFalcoNotFound(Adapter.dispatch kit dispatchWithFragmentCache) |> ignore

    // --- Boot ----------------------------------------------------------------------------------------------

    let private openDatabase (config: AppConfig) (clock: SharedClock) (jobs: Jobs) (richText: AppRichText) : Task<Database> =
        Task.Run(fun () ->
            let dbConfig =
                { Campfire.Db.Config.create config.Storage.Database with
                    Readers = config.DbReaders
                    Environment = config.Environment }
            let env: Env =
                { Clock = clock
                  Sink = jobs
                  RichText = richText
                  BcryptCost = Password.Cost }
            Database.Open(dbConfig, env))

    /// Boots the app from `config`: prepares the database, restores the reference's boot-time side
    /// effects, and builds the HTTP stack.
    let boot (config: AppConfig) (loggers: ILoggerFactory) : Task<Booted> =
        task {
            StoragePaths.createDirs config.Storage
            let secrets = Secrets.create config.SecretKeyBase
            let clock =
                match KitClock.fromEnv () with
                | Ok clock -> clock
                | Error message -> failwith message

            let logger = loggers.CreateLogger "campfire"
            let jobs = Jobs(JobLimits.QueueCapacity, loggers.CreateLogger "campfire.jobs")
            let richText = AppRichText(secrets, clock, loggers.CreateLogger "campfire.rich_text")
            let! db = openDatabase config clock jobs richText

            // config/puma.rb: `Membership.disconnect_all` when the server boots.
            match! db.Write(fun tx -> Membership.disconnectAll tx |> ignore) with
            | Ok() -> ()
            | Error error -> failwith (DbError.display error)

            let storage =
                Storage.create (DiskService.create config.Storage.Files "local") (RailsCompat.appVerifier secrets "ActiveStorage")

            let cableConfig = { Campfire.Cable.Config.defaults with AssumeSsl = not config.DisableSsl }
            let deps: Deps = { Db = db; Secrets = secrets; Clock = clock }
            let cable = Registry.server deps cableConfig (loggers.CreateLogger "campfire.cable")

            let kitConfig = { KitConfig.Production config.DisableSsl with ErrorPages = errorPages () }

            let fragmentCache = FragmentCache(int (min config.FragmentCacheBytes (int64 Int32.MaxValue)))
            // config/initializers/web_push.rb: off without a valid VAPID key pair.
            let webPush = IntegrationJobs.webPushPool config db (loggers.CreateLogger "campfire.web_push")
            let app =
                AppState(config, secrets, clock, db, storage, cable, Broadcasts cable, jobs, webPush, fragmentCache, loggers)

            let registry = CoreJobs.withCoreJobs ()
            IntegrationJobs.registerJobs registry
            let runner = JobRunner.start jobs cable app registry config.JobConcurrency

            let kit = Kit(kitConfig, secrets, clock, app, logger)
            return { App = app; Pipeline = pipeline app kit; Jobs = runner }
        }

    // --- Commands ----------------------------------------------------------------------------------------

    [<Literal>]
    let Usage = "usage: campfire [server|backup]"

    /// How long in-flight requests and queued jobs get after SIGTERM/SIGINT.
    let ShutdownGrace = TimeSpan.FromSeconds 10.0

    /// `bin/boot`'s `thrust bin/start-app`: the front server (kit's Thruster) on HTTP_PORT and, with
    /// TLS_DOMAIN, HTTPS_PORT, and the app itself on TARGET_PORT.
    let serve (config: AppConfig) (loggers: ILoggerFactory) : Task =
        task {
            let logger = loggers.CreateLogger "campfire"
            let front = FrontConfig.fromEnv ()
            let! booted = boot config loggers
            let app = booted.App

            let stopping = TaskCompletionSource()
            let signal =
                task {
                    do! Server.shutdownSignal ()
                    logger.LogInformation "shutting down"
                    // Close every WebSocket (`server_restart`, so clients reconnect) or they'd hold the
                    // graceful shutdown open.
                    app.Cable.Restart()
                    stopping.TrySetResult() |> ignore
                }
            // The request log goes to stdout as batched bytes (the `thruster` lines the console logger would write), not through `ILogger`.
            let requestLines = RequestLog.stdout ()
            let server = Front.serveWithLines front booted.Pipeline ValueNone loggers requestLines signal
            let deadline =
                task {
                    do! stopping.Task
                    do! Task.Delay ShutdownGrace
                }
            let! first = Task.WhenAny(server, deadline)
            if obj.ReferenceEquals(first, server) then
                do! server
            else
                logger.LogWarning "requests still running at shutdown were abandoned"
            // Jobs first: pushing a message queues its notifications on the Web Push pool.
            do! booted.Jobs.Shutdown ShutdownGrace
            match app.WebPush with
            | Some pool -> do! pool.Shutdown()
            | None -> ()
            requestLines.Stop()
            // The database goes last: its writer checkpoints and closes the file (Rust drops it with the app).
            (app.Db :> IDisposable).Dispose()
        }

    /// SQLite's online backup of the live database at `source` into a new file at `target`: `sqlite3_backup_step(-1)`
    /// (every page in one step), retried `attempts` times, `delay` apart, while the source is busy or locked
    /// (`copy_database`). `busyTimeout` is the source connection's own busy handler, in milliseconds.
    let internal copyDatabaseWith (busyTimeout: int) (attempts: int) (delay: TimeSpan) (source: string) (target: string) : unit =
        let open' (path: string) (mode: SqliteOpenMode) =
            let builder = SqliteConnectionStringBuilder()
            builder.DataSource <- path
            builder.Mode <- mode
            builder.Pooling <- false
            let connection = new SqliteConnection(builder.ToString())
            connection.Open()
            connection
        use source = open' source SqliteOpenMode.ReadOnly
        use command = source.CreateCommand()
        command.CommandText <- $"PRAGMA busy_timeout = {busyTimeout}"
        command.ExecuteNonQuery() |> ignore
        use target = open' target SqliteOpenMode.ReadWriteCreate
        let fail (message: string) (code: int) : exn = SqliteException(message, code)
        let backup = SQLitePCL.raw.sqlite3_backup_init (target.Handle, "main", source.Handle, "main")
        if isNull backup then
            raise (fail ((SQLitePCL.raw.sqlite3_errmsg target.Handle).utf8_to_string()) (SQLitePCL.raw.sqlite3_errcode target.Handle))
        try
            let mutable tried = 0
            let mutable finished = false
            while not finished do
                match SQLitePCL.raw.sqlite3_backup_step (backup, -1) with
                | rc when rc = SQLitePCL.raw.SQLITE_DONE -> finished <- true
                | rc when (rc = SQLitePCL.raw.SQLITE_BUSY || rc = SQLitePCL.raw.SQLITE_LOCKED) ->
                    if tried < attempts then
                        tried <- tried + 1
                        Threading.Thread.Sleep delay
                    else
                        raise (fail "backup did not finish: source is busy or locked" rc)
                | rc -> raise (fail ((SQLitePCL.raw.sqlite3_errmsg source.Handle).utf8_to_string()) rc)
        finally
            SQLitePCL.raw.sqlite3_backup_finish backup |> ignore

    let private copyDatabase (source: string) (target: string) : unit =
        copyDatabaseWith 5000 50 (TimeSpan.FromMilliseconds 100.0) source target

    /// `script/admin/prepare-backup`: `SQLite3::Backup` of the live database, all pages in one step,
    /// into `storage/backups/<database file name>`.
    let backup (config: AppConfig) (logger: ILogger) : unit =
        let destination = StoragePaths.backupFile config.Storage
        let dir =
            match Path.GetDirectoryName destination with
            | null
            | "" -> "."
            | dir -> dir
        Directory.CreateDirectory dir |> ignore
        // Written to a file of its own beside the destination and renamed over it, so a failed,
        // interrupted or concurrent backup never leaves a torn file where ONCE (and `post-restore`)
        // expect the last good one. A failed one's file is deleted.
        let partial = Path.Combine(dir, $".backup-{Guid.NewGuid():N}.sqlite3")
        try
            copyDatabase config.Storage.Database partial
            File.Move(partial, destination, true)
            logger.LogInformation("backup written path={Path}", destination)
        finally
            if File.Exists partial then File.Delete partial

    /// A logger factory at the level `RAILS_LOG_LEVEL` names (or `CAMPFIRE_LOG`, which replaces it), with
    /// the front server logging on its own terms, as Thruster did: requests at info, more with DEBUG.
    let createLoggerFactory (config: AppConfig) : ILoggerFactory =
        let level =
            match
                (match Environment.GetEnvironmentVariable "CAMPFIRE_LOG" with
                 | null
                 | "" -> config.LogLevel
                 | value -> value)
                    .ToLowerInvariant()
            with
            | "trace" -> LogLevel.Trace
            | "debug" -> LogLevel.Debug
            | "warn" -> LogLevel.Warning
            | "error"
            | "fatal"
            | "unknown" -> LogLevel.Error
            | _ -> LogLevel.Information
        let front = if (FrontConfig.fromEnv ()).Debug then LogLevel.Debug else LogLevel.Information
        LoggerFactory.Create(fun builder ->
            builder
                .AddSimpleConsole(fun options ->
                    options.SingleLine <- true
                    options.UseUtcTimestamp <- true
                    options.TimestampFormat <- "yyyy-MM-ddTHH:mm:ss.ffffffZ ")
                .SetMinimumLevel(level)
                .AddFilter("Microsoft", LogLevel.Warning)
                .AddFilter("System", LogLevel.Warning)
                .AddFilter("thruster", front)
            |> ignore)

    /// Run a command; a failure is `Error: <message>` on stderr and exit code 1, as `anyhow`'s `main` ends.
    let internal execute (config: AppConfig) (loggers: ILoggerFactory) (command: string option) : int =
        try
            match command with
            | None
            | Some "server" ->
                match Server.raiseOpenFileLimit () with
                | ValueSome limit -> (loggers.CreateLogger "campfire").LogInformation("open files limit={Limit}", limit)
                | ValueNone -> ()
                (serve config loggers).GetAwaiter().GetResult()
                0
            | Some "backup" ->
                backup config (loggers.CreateLogger "campfire")
                0
            | Some other ->
                eprintfn "Error: unknown command %A\n%s" other Usage
                1
        with ex ->
            eprintfn "Error: %s" ex.Message
            1

    /// The binary's entry point.
    ///
    /// - `campfire` / `campfire server`: serve the app behind the front server, as `bin/boot` did with
    ///   Thruster in front of `bin/start-app` (see `Campfire.Kit.Front`).
    /// - `campfire backup`: the ONCE `pre-backup` hook (`script/admin/prepare-backup`): snapshot the live
    ///   database into `storage/backups/` with SQLite's online backup API.
    ///
    /// The ONCE `post-restore` hook stays the reference's shell script (`hooks/post-restore`): copy
    /// `storage/backups/<env>.sqlite3` over `storage/db/<env>.sqlite3` and delete its `-wal` and `-shm`
    /// files; the next boot's `db:prepare` picks it up.
    let run (argv: string[]) : int =
        let command = Array.tryHead argv
        if command = Some "-h" || command = Some "--help" then
            printfn "%s" Usage
            0
        else
            match AppConfig.fromEnv () with
            | Error message ->
                eprintfn "Error: %s" message
                1
            | Ok config ->
                use loggers = createLoggerFactory config
                execute config loggers command
