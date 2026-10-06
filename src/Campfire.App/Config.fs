// Port of rust/crates/campfire/src/config.rs
//
// Environment configuration: the env vars the reference reads in production, plus a few
// `CAMPFIRE_*` knobs for things Rails gets from its directory layout.
//
// Reference sources:
// - `SECRET_KEY_BASE`: Rails' `secret_key_base` (required in production; `SECRET_KEY_BASE_DUMMY`
//   makes a throwaway one, as Rails does for asset precompilation).
// - `VAPID_PUBLIC_KEY`, `VAPID_PRIVATE_KEY`: `config/initializers/vapid.rb`. Checked at boot:
//   when either is missing or they aren't a matching P-256 key pair, Web Push is off (logged).
// - `VAPID_SUBJECT`: the contact push services see in the VAPID JWT's `sub` (a `mailto:` or
//   `https:` URL). The reference hardcodes `mailto:support@37signals.com`; this defaults to
//   `https://` and the first `TLS_DOMAIN`, or the project's URL without one.
// - `DISABLE_SSL`: `config/environments/production.rb` (`assume_ssl`/`force_ssl` unless present).
// - `APP_VERSION`, `GIT_REVISION`: `config/initializers/version.rb` (`X-Version`, `X-Rev`).
// - `RAILS_ENV`: names the database file (`storage/db/<env>.sqlite3`, `config/database.yml`).
// - `RAILS_MAX_THREADS`: `config/database.yml` pool size, used for the number of reader threads,
//   each with a reader connection of its own (`Campfire.Db.Database`).
// - `JOB_CONCURRENCY`: Resque worker count (`config/puma.rb`), used for job concurrency.
// - `RAILS_LOG_LEVEL`: `config/environments/production.rb` log level.
// - Thruster's (`TLS_DOMAIN`, `HTTP_PORT`, `HTTP_*_TIMEOUT`, `TARGET_PORT`, ...): read by
//   `Campfire.Kit.FrontConfig`, which does Thruster's job in this binary.
// - Not applicable: `REDIS_URL` and `WEB_CONCURRENCY` (no Redis, one process), `PORT` (Puma's;
//   the app listens on Thruster's `TARGET_PORT`), and `SENTRY_DSN` and `SKIP_TELEMETRY` (the app
//   sends no telemetry).
// - `CAMPFIRE_FRAGMENT_CACHE_MB`: the fragment store's limit in megabytes (default 32). The
//   reference caches fragments in Redis (`redis_cache_store`) with no `maxmemory`; this store is
//   in the process, so it's bounded like Rails' `MemoryStore` (default `size` 32 MB), evicting the
//   least recently used fragments. See `Campfire.Views.FragmentCache`.
// - `CAMPFIRE_FROZEN_TIME` (`Campfire.Kit.KitClock`): pins the clock, for parity runs.
// - `CAMPFIRE_LOG`: a `Microsoft.Extensions.Logging` level (`debug`, `info`, ...) that replaces the
//   one `RAILS_LOG_LEVEL` gives (Rust's `CAMPFIRE_LOG` is a tracing filter; see Program.fs).
//
// Storage paths mirror `Rails.root.join("storage")`: the database under `db/`, blobs under
// `files/` (`config/storage.yml`), backups under `backups/` (`script/admin/prepare-backup`).
namespace Campfire.App

open System
open System.IO
open System.Security.Cryptography
open Campfire.Views

type StoragePaths =
    {
        /// `storage/db/<env>.sqlite3`
        Database: string
        /// The `local` Disk service root, `storage/files`.
        Files: string
        /// `storage/backups`
        Backups: string
    }

module StoragePaths =
    let create (root: string) (environment: string) : StoragePaths =
        { Database = Path.Combine(root, "db", $"{environment}.sqlite3")
          Files = Path.Combine(root, "files")
          Backups = Path.Combine(root, "backups") }

    /// `config/initializers/storage_paths.rb`: `storage/{db,files}` exist after boot.
    let createDirs (paths: StoragePaths) : unit =
        match Path.GetDirectoryName paths.Database with
        | null
        | "" -> ()
        | dbDir -> Directory.CreateDirectory dbDir |> ignore
        Directory.CreateDirectory paths.Files |> ignore

    /// Where `prepare-backup` writes the snapshot: `storage/backups/<database file name>`.
    let backupFile (paths: StoragePaths) : string =
        let name =
            match Path.GetFileName paths.Database with
            | null
            | "" -> "production.sqlite3"
            | name -> name
        Path.Combine(paths.Backups, name)

type AppConfig =
    { SecretKeyBase: string
      VapidPublicKey: string option
      VapidPrivateKey: string option
      /// `VAPID_SUBJECT`, or a default (see the module docs).
      VapidSubject: string
      /// `DISABLE_SSL` present: no `assume_ssl`, no `force_ssl`.
      DisableSsl: bool
      /// `Rails.application.config.app_version`
      AppVersion: string
      /// `Rails.application.config.git_revision`
      GitRevision: string option
      Environment: string
      Storage: StoragePaths
      DbReaders: int
      JobConcurrency: int
      LogLevel: string
      /// The fragment store's limit in bytes (`CAMPFIRE_FRAGMENT_CACHE_MB`).
      FragmentCacheBytes: int64 }

module AppConfig =
    /// The install's own HTTPS URL when it has a TLS domain; the project's otherwise.
    let private defaultVapidSubject (tlsDomains: string option) : string =
        let domain =
            tlsDomains
            |> Option.bind (fun domains -> domains.Split ',' |> Array.map (fun d -> d.Trim()) |> Array.tryFind (fun d -> d <> ""))
        match domain with
        | Some domain -> $"https://{domain}"
        | None -> "https://github.com/ckluis/once-campfire-fsharp"

    let private dummySecret () : string = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes 64)

    /// Rust's `usize::from_str`: an optional `+` and decimal digits.
    let private parseNumber (value: string) : int64 option =
        let digits = if value.StartsWith('+') then value.Substring 1 else value
        if digits.Length > 0 && Seq.forall Char.IsAsciiDigit digits then
            match Int64.TryParse digits with
            | true, n -> Some n
            | _ -> None
        else
            None

    /// Rust's `{value:?}` for a string: quoted, with quotes, backslashes and control characters escaped.
    let private debugString (value: string) : string =
        let out = Text.StringBuilder("\"")
        for c in value do
            match c with
            | '"' -> out.Append "\\\"" |> ignore
            | '\\' -> out.Append "\\\\" |> ignore
            | '\n' -> out.Append "\\n" |> ignore
            | '\r' -> out.Append "\\r" |> ignore
            | '\t' -> out.Append "\\t" |> ignore
            | c when c < ' ' || c = '\127' -> out.Append($"\\u{{{int c:x}}}") |> ignore
            | c -> out.Append c |> ignore
        out.Append('"').ToString()

    /// Builds the config from any variable lookup (tests pass a map).
    let fromLookup (get: string -> string | null) : Result<AppConfig, string> =
        let present (name: string) : string option =
            match get name with
            | null -> None
            | value -> if String.IsNullOrWhiteSpace value then None else Some value
        let number (name: string) (fallback: int64) : Result<int64, string> =
            match present name with
            | Some value ->
                match parseNumber (value.Trim()) with
                | Some n -> Ok n
                | None -> Error $"{name}={debugString value} is not a number"
            | None -> Ok fallback
        let secretKeyBase =
            match present "SECRET_KEY_BASE" with
            | Some secret -> Ok secret
            | None when (present "SECRET_KEY_BASE_DUMMY").IsSome -> Ok(dummySecret ())
            | None -> Error "Missing `secret_key_base` for 'production' environment, set SECRET_KEY_BASE"
        match secretKeyBase with
        | Error e -> Error e
        | Ok secretKeyBase ->
            let environment = defaultArg (present "RAILS_ENV") "production"
            let storageRoot = defaultArg (present "CAMPFIRE_STORAGE_PATH") "storage"
            let defaults = StoragePaths.create storageRoot environment
            let storage =
                { Database = defaultArg (present "CAMPFIRE_DATABASE_PATH") defaults.Database
                  Files = defaultArg (present "CAMPFIRE_FILES_PATH") defaults.Files
                  Backups = defaultArg (present "CAMPFIRE_BACKUPS_PATH") defaults.Backups }
            match
                number "RAILS_MAX_THREADS" 5L,
                number "JOB_CONCURRENCY" 2L,
                number "CAMPFIRE_FRAGMENT_CACHE_MB" (int64 (FragmentCacheLimits.DefaultMaxBytes >>> 20))
            with
            | Error e, _, _
            | _, Error e, _
            | _, _, Error e -> Error e
            | Ok readers, Ok concurrency, Ok megabytes ->
                Ok
                    { SecretKeyBase = secretKeyBase
                      VapidPublicKey = present "VAPID_PUBLIC_KEY"
                      VapidPrivateKey = present "VAPID_PRIVATE_KEY"
                      VapidSubject =
                        (match present "VAPID_SUBJECT" with
                         | Some subject -> subject
                         | None -> defaultVapidSubject (present "TLS_DOMAIN"))
                      DisableSsl = (present "DISABLE_SSL").IsSome
                      AppVersion =
                        (match present "APP_VERSION" |> Option.orElse (present "GIT_REVISION") with
                         | Some version -> version
                         | None -> "0")
                      GitRevision = (match get "GIT_REVISION" with null -> None | revision -> Some revision)
                      Environment = environment
                      Storage = storage
                      DbReaders = int (max readers 1L)
                      JobConcurrency = int (max concurrency 1L)
                      LogLevel = defaultArg (present "RAILS_LOG_LEVEL") "info"
                      FragmentCacheBytes = megabytes * (1L <<< 20) }

    let fromEnv () : Result<AppConfig, string> = fromLookup Environment.GetEnvironmentVariable
