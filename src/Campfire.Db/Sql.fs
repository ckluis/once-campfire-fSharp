// Port of rust/crates/db/src/sql.rs
namespace Campfire.Db

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open Microsoft.Data.Sqlite

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

/// The columns of a row being read, by position (see `Columns`).
type Row internal (reader: SqliteDataReader) =
    member _.Int64(i: int) : int64 = reader.GetInt64 i
    member _.Text(i: int) : string = reader.GetString i
    member _.IsNull(i: int) : bool = reader.IsDBNull i
    member _.OptInt64(i: int) : int64 option = if reader.IsDBNull i then None else Some(reader.GetInt64 i)
    member _.OptText(i: int) : string option = if reader.IsDBNull i then None else Some(reader.GetString i)

    /// A stored datetime, read the way Rails wrote it (`Timestamp.ParseDb`).
    member _.Timestamp(i: int) : Timestamp =
        let text = reader.GetString i
        match Timestamp.ParseDbValue text with
        | ValueSome ts -> ts
        | ValueNone -> raise (FormatException $"invalid datetime \"{text}\"")

    member this.OptTimestamp(i: int) : Timestamp option = if reader.IsDBNull i then None else Some(this.Timestamp i)
    member _.ColumnName(i: int) : string = reader.GetName i
    member _.FieldCount : int = reader.FieldCount

    /// The column's value as SQLite stores it.
    member this.Arg(i: int) : SqlArg =
        if reader.IsDBNull i then
            Null
        else
            match reader.GetFieldType i with
            | t when t = typeof<int64> -> I(reader.GetInt64 i)
            | t when t = typeof<double> -> R(reader.GetDouble i)
            | t when t = typeof<string> -> S(reader.GetString i)
            | _ -> B(reader.GetFieldValue<byte[]> i)

    /// The ordinal of a named column.
    member _.Ordinal(name: string) : int = reader.GetOrdinal name

module internal Binding =
    let toValue (arg: SqlArg) : objnull =
        match arg with
        | Null -> DBNull.Value
        | I i -> box i
        | R r -> box r
        | S s -> box s
        | B b -> box b
        | T t -> box (t.ToDb())

    /// Microsoft.Data.Sqlite binds by name, so each `?` becomes `?1`, `?2`, ... (outside quotes).
    let numberPlaceholders (sql: string) : string * int =
        let out = StringBuilder(sql.Length + 8)
        let mutable count = 0
        let mutable quote = '\000'
        for c in sql do
            if quote <> '\000' then
                out.Append c |> ignore
                if c = quote then quote <- '\000'
            elif c = '\'' || c = '"' then
                quote <- c
                out.Append c |> ignore
            elif c = '?' then
                count <- count + 1
                out.Append('?').Append(count) |> ignore
            else
                out.Append c |> ignore
        out.ToString(), count

module internal Timeouts =
    /// Microsoft.Data.Sqlite retries a statement that SQLite reports as busy for `CommandTimeout` seconds
    /// (30 by default, and 0 means for ever), on top of SQLite's own busy handler. One second ends that
    /// retrying as soon as the busy handler has given up, so that a lock is waited for as long as
    /// `PRAGMA busy_timeout` says (`Schema.BusyTimeoutMs`) and no longer, as with rusqlite.
    [<Literal>]
    let CommandTimeoutSeconds = 1

/// A SQLite connection and the prepared statements it keeps (`rusqlite`'s `prepare_cached`):
/// a query is compiled once per connection rather than on every call, as Active Record keeps
/// a prepared-statement cache per connection too. Not safe for concurrent use.
type Conn(raw: SqliteConnection, capacity: int) =
    let cache = Dictionary<string, SqliteCommand>()
    let mutable closed = false

    let create (sql: string) : SqliteCommand =
        let numbered, count = Binding.numberPlaceholders sql
        let cmd = raw.CreateCommand()
        cmd.CommandText <- numbered
        cmd.CommandTimeout <- Timeouts.CommandTimeoutSeconds
        for i in 1..count do
            cmd.Parameters.Add(SqliteParameter($"?{i}", DBNull.Value)) |> ignore
        cmd.Prepare()
        cmd

    let rent (sql: string) : SqliteCommand =
        match cache.TryGetValue sql with
        | true, cmd ->
            cache.Remove sql |> ignore
            cmd
        | _ -> create sql

    let giveBack (sql: string) (cmd: SqliteCommand) : unit =
        if cache.ContainsKey sql then
            cmd.Dispose()
        else
            if cache.Count >= capacity then
                let oldest = Seq.head cache.Keys
                cache[oldest].Dispose()
                cache.Remove oldest |> ignore
            cache[sql] <- cmd

    let bind (cmd: SqliteCommand) (args: SqlArg[]) : unit =
        if args.Length <> cmd.Parameters.Count then
            invalidArg "args" $"{args.Length} arguments for {cmd.Parameters.Count} placeholders in: {cmd.CommandText}"
        for i in 0 .. args.Length - 1 do
            cmd.Parameters[i].Value <- Binding.toValue args[i]

    /// Opens a connection to the database file at `path` (created if it doesn't exist), or an
    /// in-memory database for `":memory:"`. The statement cache holds `capacity` statements.
    static member Open(path: string, ?capacity: int) : Conn =
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
    member _.IsAutocommit : bool = SQLitePCL.raw.sqlite3_get_autocommit raw.Handle <> 0

    /// `Connection::execute_batch`: statements run as they are, not cached. A statement that
    /// answers with rows (a `PRAGMA`) is run to its end.
    member _.ExecuteBatch(sql: string) : unit =
        use cmd = raw.CreateCommand()
        cmd.CommandText <- sql
        cmd.CommandTimeout <- Timeouts.CommandTimeoutSeconds
        cmd.ExecuteNonQuery() |> ignore

    /// `Connection::execute` through the statement cache; the number of rows changed.
    member _.Execute(sql: string, args: SqlArg[]) : int =
        let cmd = rent sql
        try
            bind cmd args
            let changed = cmd.ExecuteNonQuery()
            giveBack sql cmd
            changed
        with _ ->
            cmd.Dispose()
            reraise ()

    /// `Connection::prepare` then `execute`: not cached, for SQL that is built per call.
    member _.ExecuteUncached(sql: string, args: SqlArg[]) : int =
        let numbered, _ = Binding.numberPlaceholders sql
        use cmd = raw.CreateCommand()
        cmd.CommandText <- numbered
        cmd.CommandTimeout <- Timeouts.CommandTimeoutSeconds
        args |> Array.iteri (fun i a -> cmd.Parameters.Add(SqliteParameter($"?{i + 1}", Binding.toValue a)) |> ignore)
        cmd.ExecuteNonQuery()

    /// `query_row_cached`: the first row, which must exist (an `INSERT ... RETURNING`).
    member _.QueryRow(sql: string, args: SqlArg[], map: Row -> 'T) : 'T =
        let cmd = rent sql
        try
            bind cmd args
            let value =
                use reader = cmd.ExecuteReader()
                if reader.Read() then map (Row reader) else failwith $"no rows returned by: {sql}"
            giveBack sql cmd
            value
        with _ ->
            cmd.Dispose()
            reraise ()

    /// `query_one`: the first row, if there is one.
    member _.QueryOne(sql: string, args: SqlArg[], map: Row -> 'T) : 'T option =
        let cmd = rent sql
        try
            bind cmd args
            let value =
                use reader = cmd.ExecuteReader()
                if reader.Read() then Some(map (Row reader)) else None
            giveBack sql cmd
            value
        with _ ->
            cmd.Dispose()
            reraise ()

    /// `query_all`
    member _.QueryAll(sql: string, args: SqlArg[], map: Row -> 'T) : 'T list =
        let cmd = rent sql
        try
            bind cmd args
            let rows = ResizeArray<'T>()
            use reader = cmd.ExecuteReader()
            let row = Row reader
            while reader.Read() do
                rows.Add(map row)
            reader.Dispose()
            giveBack sql cmd
            List.ofSeq rows
        with _ ->
            cmd.Dispose()
            reraise ()

    /// `sql::exists`
    member this.Exists(sql: string, args: SqlArg[]) : bool = (this.QueryOne(sql, args, fun _ -> ())).IsSome

    /// `sql::count`
    member this.Count(sql: string, args: SqlArg[]) : int64 = this.QueryRow(sql, args, fun r -> r.Int64 0)

    /// Prepared statements waiting in the cache.
    member internal _.CachedStatementCount : int = cache.Count

    member _.IsClosed = closed

    interface IDisposable with
        member _.Dispose() =
            if not closed then
                closed <- true
                for cmd in cache.Values do
                    cmd.Dispose()
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
