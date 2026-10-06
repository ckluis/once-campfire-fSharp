// Port of rust/crates/db/src/sql.rs
namespace Campfire.Db

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open Microsoft.Data.Sqlite
open SQLitePCL

/// A value bound to a `?` of a query.
[<Struct; NoComparison; NoEquality>]
type SqlArg =
    | Null
    | I of i: int64
    | R of r: float
    | S of s: string
    | B of b: byte[]
    /// A datetime, written the way Active Record writes it (`Timestamp.ToDb`).
    | T of t: Timestamp

module internal RowErrors =
    /// A stored value that can't be read as asked, which rusqlite returns from `row.get` as
    /// `Error::InvalidColumnType` or `FromSqlConversionFailure` (an `Err` the caller handles, so a
    /// `DbException`, not a bug in the closure).
    let nullColumn (i: int) : 'a =
        raise (DbException(Other(InvalidCastException $"column {i} is NULL")))

    let invalidDatetime (text: string) : 'a =
        raise (DbException(Other(FormatException $"invalid datetime \"{text}\"")))

/// SQLite's result and column type codes, as `SQLitePCL.raw` has them (not as `[<Literal>]`s there).
module internal Codes =
    [<Literal>]
    let Row = 100

    [<Literal>]
    let Done = 101

    [<Literal>]
    let TypeInteger = 1

    [<Literal>]
    let TypeFloat = 2

    [<Literal>]
    let TypeText = 3

    [<Literal>]
    let TypeNull = 5

    /// `SQLITE_PREPARE_PERSISTENT`: the statement lives in the connection's cache, not for one use.
    [<Literal>]
    let PreparePersistent = 1u

/// The columns of a row being read, by position (see `Columns`), straight from the prepared statement the way
/// rusqlite's `Row` reads them. An accessor raises a `DbException` (`Error::Sqlite` in Rust) for a value it can't
/// read: a NULL where a value is required, or a datetime that doesn't parse. Only valid inside the callback that
/// got it, while the statement is on its row.
[<Struct; NoComparison; NoEquality>]
type Row internal (stmt: sqlite3_stmt) =
    member _.Int64(i: int) : int64 =
        let value = raw.sqlite3_column_int64 (stmt, i)
        if value = 0L && raw.sqlite3_column_type (stmt, i) = Codes.TypeNull then RowErrors.nullColumn i else value

    /// NULL is the null pointer `sqlite3_column_text` answers with, so one call tells it from the empty string.
    member _.Text(i: int) : string =
        match (raw.sqlite3_column_text (stmt, i)).utf8_to_string () with
        | null -> RowErrors.nullColumn i
        | text -> text

    member _.IsNull(i: int) : bool = raw.sqlite3_column_type (stmt, i) = Codes.TypeNull

    member _.OptInt64(i: int) : int64 option =
        let value = raw.sqlite3_column_int64 (stmt, i)
        if value = 0L && raw.sqlite3_column_type (stmt, i) = Codes.TypeNull then None else Some value

    member _.OptText(i: int) : string option =
        match (raw.sqlite3_column_text (stmt, i)).utf8_to_string () with
        | null -> None
        | text -> Some text

    /// A stored datetime, read the way Rails wrote it (`Timestamp.ParseDb`).
    member this.Timestamp(i: int) : Timestamp =
        let text = this.Text i
        match Timestamp.ParseDbValue text with
        | ValueSome ts -> ts
        | ValueNone -> RowErrors.invalidDatetime text

    member this.OptTimestamp(i: int) : Timestamp option =
        match this.OptText i with
        | None -> None
        | Some text ->
            match Timestamp.ParseDbValue text with
            | ValueSome ts -> Some ts
            | ValueNone -> RowErrors.invalidDatetime text

    member _.ColumnName(i: int) : string = (raw.sqlite3_column_name (stmt, i)).utf8_to_string ()
    member _.FieldCount : int = raw.sqlite3_column_count stmt

    /// The column's value as SQLite stores it.
    member _.Arg(i: int) : SqlArg =
        match raw.sqlite3_column_type (stmt, i) with
        | Codes.TypeNull -> Null
        | Codes.TypeInteger -> I(raw.sqlite3_column_int64 (stmt, i))
        | Codes.TypeFloat -> R(raw.sqlite3_column_double (stmt, i))
        | Codes.TypeText -> S((raw.sqlite3_column_text (stmt, i)).utf8_to_string ())
        | _ -> B((raw.sqlite3_column_blob (stmt, i)).ToArray())

    /// The ordinal of a named column.
    member _.Ordinal(name: string) : int =
        let count = raw.sqlite3_column_count stmt
        let mutable found = -1
        for i in 0 .. count - 1 do
            if found < 0 && String.Equals((raw.sqlite3_column_name (stmt, i)).utf8_to_string (), name, StringComparison.OrdinalIgnoreCase) then
                found <- i
        if found < 0 then raise (ArgumentOutOfRangeException(nameof name, name, "no such column")) else found

module internal Library =
    /// What rusqlite's `SQLITE_OPEN_NO_MUTEX` and the build it bundles give Rust, for every connection of the process: each
    /// connection is used by one thread at a time (`Conn` is not safe for concurrent use, a reader is handed from thread to
    /// thread under the read queue's lock), so SQLite's per-connection mutex (`SQLITE_CONFIG_MULTITHREAD`, taken on every API
    /// call) and the memory statistics' global mutex (`SQLITE_CONFIG_MEMSTATUS` off, taken on every allocation, shared by all
    /// readers) are pure cost. Microsoft.Data.Sqlite opens the connections (`Conn.Raw` has to stay its connection) without
    /// the flag, so it is set library-wide, before the first connection initializes SQLite; a library already initialized
    /// refuses it and the connections keep SQLite's defaults.
    let private configure =
        lazy
            (Batteries_V2.Init()
             SQLitePCL.raw.sqlite3_config 2 |> ignore
             SQLitePCL.raw.sqlite3_config (9, 0) |> ignore)

    let ensureConfigured () : unit = configure.Force()

/// A prepared statement and the number of `?` it has.
[<Sealed; AllowNullLiteral>]
type internal Prepared(stmt: sqlite3_stmt, parameters: int) =
    member _.Stmt: sqlite3_stmt = stmt
    member _.Parameters: int = parameters

module internal Timeouts =
    /// `ExecuteBatch` still runs through a `Microsoft.Data.Sqlite` command, which retries a statement that SQLite reports as
    /// busy for `CommandTimeout` seconds (30 by default, and 0 means for ever), on top of SQLite's own busy handler. One
    /// second ends that retrying as soon as the busy handler has given up, so that a lock is waited for as long as
    /// `PRAGMA busy_timeout` says (`Schema.BusyTimeoutMs`) and no longer, as with rusqlite.
    [<Literal>]
    let CommandTimeoutSeconds = 1

/// A SQLite connection and the prepared statements it keeps (`rusqlite`'s `prepare_cached`): a query is compiled once per
/// connection rather than on every call, as Active Record keeps a prepared-statement cache per connection too. Not safe for
/// concurrent use. The connection is opened and owned by `Microsoft.Data.Sqlite` (`Raw`, which `Campfire.Storage` is written
/// against), but every statement the models run goes through SQLitePCLRaw directly, the way rusqlite drives SQLite: bound by
/// position, columns read by ordinal from the statement, no command, parameter or reader objects.
type Conn(raw: SqliteConnection, capacity: int) =
    let db = raw.Handle
    // Each statement with the tick at which it was last given back, for evicting the least recently used
    // (rusqlite's cache is an LRU too). A scan at eviction beats a list node per use.
    let cache = Dictionary<string, struct (Prepared * int64)>()
    let mutable tick = 0L
    let mutable closed = false
    // Text is encoded here to be bound (copied by SQLite), so a bind allocates nothing.
    let mutable scratch: byte[] = Array.zeroCreate 512

    let fail (rc: int) : exn =
        try
            SqliteException.ThrowExceptionForRC(rc, db)
            InvalidOperationException $"SQLite returned {rc}"
        with e ->
            e

    let create (sql: string) : Prepared =
        let mutable stmt = Unchecked.defaultof<sqlite3_stmt>
        let rc = SQLitePCL.raw.sqlite3_prepare_v3 (db, sql, Codes.PreparePersistent, &stmt)
        if rc <> 0 then raise (fail rc)
        Prepared(stmt, SQLitePCL.raw.sqlite3_bind_parameter_count stmt)

    let rent (sql: string) : Prepared =
        match cache.TryGetValue sql with
        | true, struct (prepared, _) ->
            cache.Remove sql |> ignore
            prepared
        | _ -> create sql

    let finalize (prepared: Prepared) : unit = SQLitePCL.raw.sqlite3_finalize prepared.Stmt |> ignore

    let giveBack (sql: string) (prepared: Prepared) : unit =
        if cache.ContainsKey sql then
            finalize prepared
        else
            if cache.Count >= capacity && cache.Count > 0 then
                let mutable oldest = sql
                let mutable oldestTick = Int64.MaxValue
                for KeyValue(key, struct (_, at)) in cache do
                    if at < oldestTick then
                        oldest <- key
                        oldestTick <- at
                let struct (evicted, _) = cache[oldest]
                finalize evicted
                cache.Remove oldest |> ignore
            tick <- tick + 1L
            cache[sql] <- struct (prepared, tick)

    let bindText (stmt: sqlite3_stmt) (index: int) (text: string) : int =
        if text.Length = 0 then
            // An empty span pins as a null pointer, which SQLite binds as NULL.
            SQLitePCL.raw.sqlite3_bind_text (stmt, index, "")
        else
            let needed = Encoding.UTF8.GetMaxByteCount text.Length
            if needed > scratch.Length then scratch <- Array.zeroCreate (max needed (scratch.Length * 2))
            let length = Encoding.UTF8.GetBytes(text, 0, text.Length, scratch, 0)
            SQLitePCL.raw.sqlite3_bind_text (stmt, index, ReadOnlySpan<byte>(scratch, 0, length))

    let bind (prepared: Prepared) (sql: string) (args: SqlArg[]) : unit =
        if args.Length <> prepared.Parameters then
            invalidArg "args" $"{args.Length} arguments for {prepared.Parameters} placeholders in: {sql}"
        let stmt = prepared.Stmt
        for i in 0 .. args.Length - 1 do
            let rc =
                match args[i] with
                | Null -> SQLitePCL.raw.sqlite3_bind_null (stmt, i + 1)
                | I v -> SQLitePCL.raw.sqlite3_bind_int64 (stmt, i + 1, v)
                | R v -> SQLitePCL.raw.sqlite3_bind_double (stmt, i + 1, v)
                | S v -> bindText stmt (i + 1) v
                | B v -> if v.Length = 0 then SQLitePCL.raw.sqlite3_bind_zeroblob (stmt, i + 1, 0) else SQLitePCL.raw.sqlite3_bind_blob (stmt, i + 1, ReadOnlySpan<byte>(v))
                | T v -> bindText stmt (i + 1) (v.ToDb())
            if rc <> 0 then raise (fail rc)

    /// `sqlite3_step`, raising what SQLite reports for anything but a row or the end.
    let step (stmt: sqlite3_stmt) : bool =
        match SQLitePCL.raw.sqlite3_step stmt with
        | Codes.Row -> true
        | Codes.Done -> false
        | rc -> raise (fail rc)

    /// The statement for `sql` with `args` bound.
    let start (sql: string) (args: SqlArg[]) : Prepared =
        let prepared = rent sql
        try
            bind prepared sql args
            prepared
        with _ ->
            finalize prepared
            reraise ()

    /// Resets the statement (SQLite releases its read lock then) and gives it back to the cache, unless the call failed:
    /// a statement that raised is finalized, as a failed `CachedStatement` is not reused.
    let finish (sql: string) (prepared: Prepared) (succeeded: bool) : unit =
        SQLitePCL.raw.sqlite3_reset prepared.Stmt |> ignore
        if succeeded then giveBack sql prepared else finalize prepared

    /// Opens a connection to the database file at `path` (created if it doesn't exist), or an
    /// in-memory database for `":memory:"`. The statement cache holds `capacity` statements.
    static member Open(path: string, ?capacity: int) : Conn =
        Library.ensureConfigured ()
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- path
        builder.Mode <- SqliteOpenMode.ReadWriteCreate
        builder.Pooling <- false
        builder.Cache <- SqliteCacheMode.Private
        let raw = new SqliteConnection(builder.ToString())
        raw.Open()
        new Conn(raw, defaultArg capacity 16)

    static member OpenInMemory() : Conn = Conn.Open ":memory:"

    member _.Raw : SqliteConnection = raw

    /// `Connection::is_autocommit`: no transaction is open.
    member _.IsAutocommit : bool = SQLitePCL.raw.sqlite3_get_autocommit db <> 0

    /// `Connection::execute_batch`: statements run as they are, not cached. A statement
    /// that answers with rows (a `PRAGMA`) is run to its end.
    member _.ExecuteBatch(sql: string) : unit =
        use cmd = raw.CreateCommand()
        cmd.CommandText <- sql
        cmd.CommandTimeout <- Timeouts.CommandTimeoutSeconds
        cmd.ExecuteNonQuery() |> ignore

    /// `Connection::execute` through the statement cache; the number of rows changed.
    member _.Execute(sql: string, args: SqlArg[]) : int =
        let prepared = start sql args
        let mutable succeeded = false
        try
            while step prepared.Stmt do
                ()
            let changed = SQLitePCL.raw.sqlite3_changes db
            succeeded <- true
            changed
        finally
            finish sql prepared succeeded

    /// `Connection::prepare` then `execute`: not cached, for SQL that is built per call.
    member _.ExecuteUncached(sql: string, args: SqlArg[]) : int =
        let prepared = create sql
        try
            bind prepared sql args
            while step prepared.Stmt do
                ()
            SQLitePCL.raw.sqlite3_changes db
        finally
            finalize prepared

    /// `query_row_cached`: the first row, which must exist (an `INSERT ... RETURNING`).
    member _.QueryRow(sql: string, args: SqlArg[], map: Row -> 'T) : 'T =
        let prepared = start sql args
        let mutable succeeded = false
        try
            let value = if step prepared.Stmt then map (Row prepared.Stmt) else failwith $"no rows returned by: {sql}"
            succeeded <- true
            value
        finally
            finish sql prepared succeeded

    /// `query_one`: the first row, if there is one.
    member _.QueryOne(sql: string, args: SqlArg[], map: Row -> 'T) : 'T option =
        let prepared = start sql args
        let mutable succeeded = false
        try
            let value = if step prepared.Stmt then Some(map (Row prepared.Stmt)) else None
            succeeded <- true
            value
        finally
            finish sql prepared succeeded

    /// `query_all`
    member _.QueryAll(sql: string, args: SqlArg[], map: Row -> 'T) : 'T list =
        let prepared = start sql args
        let mutable succeeded = false
        try
            let rows = ResizeArray<'T>()
            let row = Row prepared.Stmt
            while step prepared.Stmt do
                rows.Add(map row)
            succeeded <- true
            List.ofSeq rows
        finally
            finish sql prepared succeeded

    /// `sql::exists`
    member this.Exists(sql: string, args: SqlArg[]) : bool = (this.QueryOne(sql, args, fun _ -> ())).IsSome

    /// `sql::count`
    member this.Count(sql: string, args: SqlArg[]) : int64 = this.QueryRow(sql, args, fun r -> r.Int64 0)

    /// Prepared statements waiting in the cache.
    member internal _.CachedStatementCount : int = cache.Count

    /// The SQL of the statements waiting in the cache.
    member internal _.CachedStatements : string list = cache.Keys |> List.ofSeq

    member _.IsClosed = closed

    interface IDisposable with
        member _.Dispose() =
            if not closed then
                closed <- true
                for struct (prepared, _) in cache.Values do
                    finalize prepared
                cache.Clear()
                raw.Dispose()

/// Helpers for writing queries.
module Sql =
    /// `?, ?, ?`
    let placeholders (n: int) : string = String.Join(", ", Array.create n "?")

    /// A nullable text column's value.
    let optS (value: string option) : SqlArg =
        match value with
        | Some s -> S s
        | None -> Null

    let optI (value: int64 option) : SqlArg =
        match value with
        | Some i -> I i
        | None -> Null

    let optT (value: Timestamp option) : SqlArg =
        match value with
        | Some t -> T t
        | None -> Null

    let private pick (chars: string) (n: int) : string =
        let out = Array.zeroCreate<char> n
        for i in 0 .. n - 1 do
            out[i] <- chars[RandomNumberGenerator.GetInt32 chars.Length]
        String out

    /// `SecureRandom.alphanumeric(n)`
    let alphanumeric (n: int) : string = pick "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789" n

    /// `SecureRandom.base58(n)`, as `has_secure_token` uses it.
    let base58 (n: int) : string = pick "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz" n

    /// `SecureRandom.uuid` / `Random.uuid`
    let uuid () : string = Guid.NewGuid().ToString()

    let count (conn: Conn) (sql: string) (args: SqlArg[]) : int64 = conn.Count(sql, args)
    let exists (conn: Conn) (sql: string) (args: SqlArg[]) : bool = conn.Exists(sql, args)
    let queryAll (conn: Conn) (sql: string) (args: SqlArg[]) (map: Row -> 'T) : 'T list = conn.QueryAll(sql, args, map)
    let queryOne (conn: Conn) (sql: string) (args: SqlArg[]) (map: Row -> 'T) : 'T option = conn.QueryOne(sql, args, map)
    let execute (conn: Conn) (sql: string) (args: SqlArg[]) : int = conn.Execute(sql, args)
