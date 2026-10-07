// Port of rust/crates/db/src/schema.rs
/// The schema exactly as `bin/rails db:prepare` creates it on a fresh database, and the
/// connection settings from `reference/config/database.yml` plus the sqlite3 adapter's
/// `DEFAULT_PRAGMAS`.
///
/// `schema.sql` is the `sqlite_master` of a database the reference app created with
/// `db:prepare` (loading `reference/db/schema.rb`), minus the objects SQLite derives on its
/// own (FTS5 shadow tables, `sqlite_sequence`, autoindexes). A fresh `db:prepare` loads
/// `schema.rb`, so columns come out in alphabetical order; databases that were migrated keep
/// migration order. All queries in this crate name their columns, so both work: every model
/// selects its own list of them (`Columns`) and reads by position in that list, never by
/// position over `*`.
module Campfire.Db.Schema

open System.IO
open System.Reflection
open Campfire.RailsCompat.Clock

let schemaSql : string =
    match Assembly.GetExecutingAssembly().GetManifestResourceStream "schema.sql" with
    | null -> failwith "schema.sql is not embedded"
    | stream ->
        use stream = stream
        use reader = new StreamReader(stream)
        reader.ReadToEnd()

/// Every migration in `reference/db/migrate`, oldest first.
let migrationVersions : string list =
    [ "20231215043540"
      "20231220143106"
      "20240110071740"
      "20240115124901"
      "20240130003150"
      "20240130213001"
      "20240131105830"
      "20240209110503"
      "20250825100957"
      "20250825100958"
      "20250825100959"
      "20251126092013"
      "20251126115722"
      "20251126130131"
      "20251212154340" ]

/// SHA1 of `reference/db/schema.rb`, which `db:schema:load` records in `ar_internal_metadata`.
[<Literal>]
let SchemaSha1 = "f75da8dad38bfb179ffd757bd7a7c2b3f818bc29"

/// Indexes this app adds to the Rails schema, created on boot when missing (new and existing
/// databases alike). Additive only, so the database still works with the Rails image.
let additions : string list =
    [
      // A room's messages are paged by `created_at` (`last_page`, `page_before`, `page_after`), and
      // with only `index_messages_on_room_id` every room page sorted the room's whole history:
      // 60 ms at 236k messages, against 0.02 ms with this index.
      """CREATE INDEX IF NOT EXISTS "index_messages_on_room_id_and_created_at" ON "messages" ("room_id", "created_at")""" ]

/// `timeout: 5000` in `config/database.yml`.
[<Literal>]
let BusyTimeoutMs = 5000

/// Applies the per-connection settings Rails applies (`SQLite3Adapter#configure_connection`),
/// except `mmap_size`: every reader remaps a memory-mapped database after each commit.
let configureConnection (conn: Conn) : unit =
    conn.ExecuteBatch $"PRAGMA busy_timeout = {BusyTimeoutMs}"
    conn.ExecuteBatch "PRAGMA foreign_keys = ON"
    conn.ExecuteBatch "PRAGMA journal_mode = wal"
    conn.ExecuteBatch "PRAGMA synchronous = normal"
    conn.ExecuteBatch "PRAGMA journal_size_limit = 67108864"
    conn.ExecuteBatch "PRAGMA cache_size = 2000"

type Prepared =
    /// The database was empty; the schema was loaded.
    | Loaded
    /// The schema was already current.
    | UpToDate

let private tableExists (conn: Conn) (name: string) : bool =
    conn.Exists("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = ?", [| S name |])

let pendingMigrations (conn: Conn) : string list =
    migrationVersions |> List.filter (fun version -> not (conn.Exists("SELECT 1 FROM schema_migrations WHERE version = ?", [| S version |])))

let private setInternalMetadata (conn: Conn) (key: string) (value: string) (now: Timestamp) : unit =
    match conn.QueryOne("""SELECT "value" FROM "ar_internal_metadata" WHERE "key" = ?""", [| S key |], fun r -> r.OptText 0) with
    | None ->
        conn.Execute(
            """INSERT INTO "ar_internal_metadata" ("key", "value", "created_at", "updated_at") VALUES (?, ?, ?, ?)""",
            [| S key; S value; T now; T now |]
        )
        |> ignore
    | Some current when current <> Some value ->
        conn.Execute("""UPDATE "ar_internal_metadata" SET "value" = ?, "updated_at" = ? WHERE "key" = ?""", [| S value; T now; S key |])
        |> ignore
    | Some _ -> ()

let private loadSchema (conn: Conn) (environment: string) (clock: Clock) : unit =
    conn.ExecuteBatch "BEGIN IMMEDIATE"
    try
        conn.ExecuteBatch schemaSql

        // `assume_migrated_upto_version` inserts the current version, then the rest newest first.
        for version in List.rev migrationVersions do
            conn.Execute("""INSERT INTO "schema_migrations" ("version") VALUES (?)""", [| S version |]) |> ignore

        setInternalMetadata conn "environment" environment (Timestamp.FromDateTimeOffset(clock.Now()))
        setInternalMetadata conn "schema_sha1" SchemaSha1 (Timestamp.FromDateTimeOffset(clock.Now()))
        conn.ExecuteBatch "COMMIT"
    with _ ->
        if not conn.IsAutocommit then conn.ExecuteBatch "ROLLBACK"
        reraise ()

/// `bin/rails db:prepare`: loads the schema into an empty database, or verifies an
/// existing one is fully migrated. We don't port migrations, so a database with pending
/// migrations is an error (boot the Rails image once to migrate it). Then adds `additions`.
let prepare (conn: Conn) (environment: string) (clock: Clock) : Prepared =
    let prepared =
        if tableExists conn "schema_migrations" then
            match pendingMigrations conn with
            | [] -> UpToDate
            | pending -> Err.fail (DbError.other ("pending migrations: " + String.concat ", " pending))
        else
            loadSchema conn environment clock
            Loaded
    for addition in additions do
        conn.ExecuteBatch addition
    prepared
