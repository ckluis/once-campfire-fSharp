// Port of the tests of rust/crates/campfire/src/config.rs
module Campfire.App.Tests.ConfigTests

open System.Collections.Generic
open System.IO
open Xunit
open Campfire.App

let private config (vars: (string * string) list) : Result<AppConfig, string> =
    let vars = Dictionary<string, string>(dict vars)
    AppConfig.fromLookup (fun name ->
        match vars.TryGetValue name with
        | true, value -> value
        | _ -> null)

let private unwrap (result: Result<AppConfig, string>) : AppConfig =
    match result with
    | Ok config -> config
    | Error e -> failwith e

[<Fact>]
let ``requires a secret key base`` () =
    Assert.True((config []).IsError)
    Assert.Equal(128, (unwrap (config [ "SECRET_KEY_BASE_DUMMY", "1" ])).SecretKeyBase.Length)

[<Fact>]
let ``production defaults`` () =
    let config = unwrap (config [ "SECRET_KEY_BASE", "abc" ])
    Assert.False config.DisableSsl
    Assert.Equal("0", config.AppVersion)
    Assert.Equal(None, config.GitRevision)
    Assert.Equal(Path.Combine("storage", "db", "production.sqlite3"), config.Storage.Database)
    Assert.Equal(Path.Combine("storage", "files"), config.Storage.Files)
    Assert.Equal(Path.Combine("storage", "backups", "production.sqlite3"), StoragePaths.backupFile config.Storage)
    Assert.Equal(32L * 1024L * 1024L, config.FragmentCacheBytes)

[<Fact>]
let ``fragment cache size in megabytes`` () =
    let bytes = (unwrap (config [ "SECRET_KEY_BASE", "abc"; "CAMPFIRE_FRAGMENT_CACHE_MB", "64" ])).FragmentCacheBytes
    Assert.Equal(64L * 1024L * 1024L, bytes)
    Assert.True((config [ "SECRET_KEY_BASE", "abc"; "CAMPFIRE_FRAGMENT_CACHE_MB", "lots" ]).IsError)

[<Fact>]
let ``a number that is not one names the variable and quotes the value`` () =
    match config [ "SECRET_KEY_BASE", "abc"; "JOB_CONCURRENCY", "two \"many\"" ] with
    | Error message -> Assert.Equal("JOB_CONCURRENCY=\"two \\\"many\\\"\" is not a number", message)
    | Ok _ -> failwith "a bad number is refused"

[<Fact>]
let ``version falls back to the revision`` () =
    let config = unwrap (config [ "SECRET_KEY_BASE", "abc"; "APP_VERSION", ""; "GIT_REVISION", "abc123" ])
    Assert.Equal("abc123", config.AppVersion)
    Assert.Equal(Some "abc123", config.GitRevision)

[<Fact>]
let ``disable ssl is any non blank value`` () =
    Assert.True((unwrap (config [ "SECRET_KEY_BASE", "abc"; "DISABLE_SSL", "false" ])).DisableSsl)
    Assert.False((unwrap (config [ "SECRET_KEY_BASE", "abc"; "DISABLE_SSL", " " ])).DisableSsl)

[<Fact>]
let ``vapid keys must not be blank`` () =
    let config = unwrap (config [ "SECRET_KEY_BASE", "abc"; "VAPID_PUBLIC_KEY", ""; "VAPID_PRIVATE_KEY", " " ])
    Assert.Equal((None, None), (config.VapidPublicKey, config.VapidPrivateKey))

[<Fact>]
let ``vapid subject defaults to the tls domain`` () =
    let subject (vars: (string * string) list) = (unwrap (config ([ "SECRET_KEY_BASE", "abc" ] @ vars))).VapidSubject
    Assert.Equal("mailto:ops@example.com", subject [ "VAPID_SUBJECT", "mailto:ops@example.com"; "TLS_DOMAIN", "chat.example.com" ])
    Assert.Equal("https://chat.example.com", subject [ "TLS_DOMAIN", " , chat.example.com,other.example.com" ])
    // Without a TLS domain it is this port's project, where Rust's names its own (a divergence in README.md).
    Assert.Equal("https://github.com/ckluis/once-campfire-fsharp", subject [ "VAPID_SUBJECT", " " ])

[<Fact>]
let ``storage overrides`` () =
    let config =
        unwrap (config [ "SECRET_KEY_BASE", "abc"; "CAMPFIRE_STORAGE_PATH", "/rails/storage"; "CAMPFIRE_FILES_PATH", "/seed/storage" ])
    Assert.Equal("/rails/storage/db/production.sqlite3", config.Storage.Database)
    Assert.Equal("/seed/storage", config.Storage.Files)
