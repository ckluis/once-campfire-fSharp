// Stands in for rusqlite's `Connection` methods that rust/crates/storage uses: `execute`,
// `prepare_cached` + `query_row`, `query_map` and `last_insert_rowid`, over a
// `Microsoft.Data.Sqlite` connection the caller owns.
//
// Every function takes the `SqliteConnection`, so callers decide the transaction boundaries the
// way the Rails models' callbacks would. As in `Campfire.Db`, a transaction is a `BEGIN` statement
// on the connection (not `BeginTransaction`, which would make every command ask for it).
module internal Campfire.Storage.Sql

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open Microsoft.Data.Sqlite

/// The prepared statements each connection keeps (`prepare_cached`), for as long as it lives.
let private caches = ConditionalWeakTable<SqliteConnection, Dictionary<string, SqliteCommand>>()

[<Literal>]
let private SqliteConstraintUnique = 2067

/// Microsoft.Data.Sqlite retries a statement SQLite reports as busy for `CommandTimeout` seconds (30 by
/// default) after SQLite's busy handler has given up, so a locked database would fail after
/// `busy_timeout` plus that, where rusqlite fails after `busy_timeout`. One second ends the retrying
/// as soon as the busy handler has. (Campfire.Db sets the same on its commands, `Timeouts` in its
/// Sql.fs; Storage doesn't reference it.)
[<Literal>]
let private CommandTimeoutSeconds = 1

let private create (conn: SqliteConnection) (sql: string) (count: int) : SqliteCommand =
    let command = conn.CreateCommand()
    command.CommandText <- sql
    command.CommandTimeout <- CommandTimeoutSeconds
    for i in 1..count do
        command.Parameters.Add(SqliteParameter($"?{i}", DBNull.Value)) |> ignore
    command.Prepare()
    command

/// Runs `f` with the connection's cached statement for `sql`, compiled on first use.
let private withCommand (conn: SqliteConnection) (sql: string) (args: objnull[]) (f: SqliteCommand -> 'T) : 'T =
    let cache = caches.GetValue(conn, fun _ -> Dictionary<string, SqliteCommand>())
    let command =
        match cache.TryGetValue sql with
        | true, cached ->
            cache.Remove sql |> ignore
            cached
        | _ -> create conn sql args.Length
    try
        for i in 0 .. args.Length - 1 do
            command.Parameters[i].Value <- (if isNull args[i] then box DBNull.Value else args[i])
        let value = f command
        if cache.ContainsKey sql then command.Dispose() else cache[sql] <- command
        value
    with _ ->
        command.Dispose()
        reraise ()

let private attempt (f: unit -> 'a) : StorageResult<'a> =
    try
        Ok(f ())
    with :? SqliteException as e ->
        Error(StorageError.Sql e)

/// `Connection::execute`: the number of rows changed.
let execute (conn: SqliteConnection) (sql: string) (args: objnull[]) : StorageResult<int> =
    attempt (fun () -> withCommand conn sql args (fun command -> command.ExecuteNonQuery()))

/// `query_row(..).optional()`: the first row, if there is one.
let queryOne (conn: SqliteConnection) (sql: string) (args: objnull[]) (map: SqliteDataReader -> 'T) : StorageResult<'T option> =
    attempt (fun () ->
        withCommand conn sql args (fun command ->
            use reader = command.ExecuteReader()
            if reader.Read() then Some(map reader) else None))

/// `query_map(..).collect()`
let queryAll (conn: SqliteConnection) (sql: string) (args: objnull[]) (map: SqliteDataReader -> 'T) : StorageResult<'T list> =
    attempt (fun () ->
        withCommand conn sql args (fun command ->
            use reader = command.ExecuteReader()
            let rows = ResizeArray<'T>()
            while reader.Read() do
                rows.Add(map reader)
            List.ofSeq rows))

/// `Connection::last_insert_rowid`
let lastInsertRowid (conn: SqliteConnection) : int64 = SQLitePCL.raw.sqlite3_last_insert_rowid conn.Handle

/// Whether `error` is the unique index refusing a write (`SQLITE_CONSTRAINT_UNIQUE`).
let isUniqueViolation (error: StorageError) : bool =
    match error with
    | StorageError.Sql(:? SqliteException as e) -> e.SqliteExtendedErrorCode = SqliteConstraintUnique
    | _ -> false
