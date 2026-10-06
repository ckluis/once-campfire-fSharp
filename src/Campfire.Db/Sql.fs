// Port of rust/crates/db/src/sql.rs
namespace Campfire.Db

#nowarn "9" // native pointers: the C API's strings and statements

open System
open System.Collections.Generic
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open Microsoft.Data.Sqlite
open Microsoft.FSharp.NativeInterop
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

/// SQLite's result and column type codes.
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

/// The few `sqlite3_*` functions a query's every row goes through, called on the library SQLitePCLRaw ships (`e_sqlite3`, the
/// one `Microsoft.Data.Sqlite` loads) by their C names with the statement as a plain pointer, as rusqlite calls them: no
/// wrapper object, no `SafeHandle` reference counting, and for the ones that only read or store a value and return at once
/// (they take no lock but SQLite's own, call nothing back, never wait) no transition out of managed code, which would cost
/// more than the call. Preparing, finalizing and the connection stay with SQLitePCLRaw. Every function here is only valid on
/// a statement of a connection that is open and not in use by another thread.
module internal Native =
    open System.Runtime.InteropServices

    [<Literal>]
    let private Lib = "e_sqlite3"

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern int sqlite3_step(nativeint stmt)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl)>]
    extern int sqlite3_reset(nativeint stmt)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl); SuppressGCTransition>]
    extern int sqlite3_changes(nativeint db)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl); SuppressGCTransition>]
    extern int sqlite3_column_type(nativeint stmt, int column)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl); SuppressGCTransition>]
    extern int64 sqlite3_column_int64(nativeint stmt, int column)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl); SuppressGCTransition>]
    extern double sqlite3_column_double(nativeint stmt, int column)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl); SuppressGCTransition>]
    extern nativeint sqlite3_column_text(nativeint stmt, int column)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl); SuppressGCTransition>]
    extern nativeint sqlite3_column_blob(nativeint stmt, int column)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl); SuppressGCTransition>]
    extern int sqlite3_column_bytes(nativeint stmt, int column)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl); SuppressGCTransition>]
    extern int sqlite3_column_count(nativeint stmt)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl); SuppressGCTransition>]
    extern nativeint sqlite3_column_name(nativeint stmt, int column)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl); SuppressGCTransition>]
    extern int sqlite3_bind_null(nativeint stmt, int index)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl); SuppressGCTransition>]
    extern int sqlite3_bind_int64(nativeint stmt, int index, int64 value)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl); SuppressGCTransition>]
    extern int sqlite3_bind_double(nativeint stmt, int index, double value)

    /// With `destructor` `SQLITE_TRANSIENT` (-1): SQLite copies the bytes before returning.
    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl); SuppressGCTransition>]
    extern int sqlite3_bind_text(nativeint stmt, int index, nativeint text, int length, nativeint destructor)

    [<DllImport(Lib, CallingConvention = CallingConvention.Cdecl); SuppressGCTransition>]
    extern int sqlite3_bind_blob(nativeint stmt, int index, nativeint bytes, int length, nativeint destructor)

    /// `SQLITE_TRANSIENT`
    let Transient = nativeint -1

    /// The UTF-8 bytes SQLite holds for a text column, as a string; its length, not a NUL, ends it (as rusqlite reads it).
    let utf8 (pointer: nativeint) (length: int) : string =
        if length = 0 then String.Empty else Encoding.UTF8.GetString(NativePtr.ofNativeInt<byte> pointer, length)

/// The columns of a row being read, by position (see `Columns`), straight from the prepared statement the way
/// rusqlite's `Row` reads them. An accessor raises a `DbException` (`Error::Sqlite` in Rust) for a value it can't
/// read: a NULL where a value is required, or a datetime that doesn't parse. Only valid inside the callback that
/// got it, while the statement is on its row.
[<Struct; NoComparison; NoEquality>]
type Row internal (stmt: nativeint) =
    member _.Int64(i: int) : int64 =
        let value = Native.sqlite3_column_int64 (stmt, i)
        if value = 0L && Native.sqlite3_column_type (stmt, i) = Codes.TypeNull then RowErrors.nullColumn i else value

    /// NULL is the null pointer `sqlite3_column_text` answers with, so one call tells it from the empty string.
    member _.Text(i: int) : string =
        match Native.sqlite3_column_text (stmt, i) with
        | 0n -> RowErrors.nullColumn i
        | text -> Native.utf8 text (Native.sqlite3_column_bytes (stmt, i))

    member _.IsNull(i: int) : bool = Native.sqlite3_column_type (stmt, i) = Codes.TypeNull

    member _.OptInt64(i: int) : int64 option =
        let value = Native.sqlite3_column_int64 (stmt, i)
        if value = 0L && Native.sqlite3_column_type (stmt, i) = Codes.TypeNull then None else Some value

    member _.OptText(i: int) : string option =
        match Native.sqlite3_column_text (stmt, i) with
        | 0n -> None
        | text -> Some(Native.utf8 text (Native.sqlite3_column_bytes (stmt, i)))

    /// A stored datetime, read the way Rails wrote it (`Timestamp.ParseDb`), from the bytes SQLite holds when they are in
    /// the form Rails writes, without making a string of them.
    member _.Timestamp(i: int) : Timestamp =
        match Native.sqlite3_column_text (stmt, i) with
        | 0n -> RowErrors.nullColumn i
        | text ->
            let length = Native.sqlite3_column_bytes (stmt, i)
            match Timestamp.ParseDbUtf8(ReadOnlySpan<byte>(NativePtr.toVoidPtr (NativePtr.ofNativeInt<byte> text), length)) with
            | ValueSome ts -> ts
            | ValueNone ->
                let text = Native.utf8 text length
                match Timestamp.ParseDbValue text with
                | ValueSome ts -> ts
                | ValueNone -> RowErrors.invalidDatetime text

    member this.OptTimestamp(i: int) : Timestamp option = if this.IsNull i then None else Some(this.Timestamp i)

    member _.ColumnName(i: int) : string = Marshal.PtrToStringUTF8(Native.sqlite3_column_name (stmt, i)) |> string
    member _.FieldCount : int = Native.sqlite3_column_count stmt

    /// The column's value as SQLite stores it.
    member _.Arg(i: int) : SqlArg =
        match Native.sqlite3_column_type (stmt, i) with
        | Codes.TypeNull -> Null
        | Codes.TypeInteger -> I(Native.sqlite3_column_int64 (stmt, i))
        | Codes.TypeFloat -> R(Native.sqlite3_column_double (stmt, i))
        | Codes.TypeText ->
            let text = Native.sqlite3_column_text (stmt, i)
            S(Native.utf8 text (Native.sqlite3_column_bytes (stmt, i)))
        | _ ->
            let blob = Native.sqlite3_column_blob (stmt, i)
            let length = Native.sqlite3_column_bytes (stmt, i)
            B(if length = 0 then [||] else ReadOnlySpan<byte>(NativePtr.toVoidPtr (NativePtr.ofNativeInt<byte> blob), length).ToArray())

    /// The ordinal of a named column.
    member _.Ordinal(name: string) : int =
        let count = Native.sqlite3_column_count stmt
        let mutable found = -1
        for i in 0 .. count - 1 do
            if found < 0 && String.Equals(Marshal.PtrToStringUTF8(Native.sqlite3_column_name (stmt, i)), name, StringComparison.OrdinalIgnoreCase) then
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

/// A prepared statement (its pointer, and the object that owns it) and the number of `?` it has.
[<Sealed; AllowNullLiteral>]
type internal Prepared(owner: sqlite3_stmt, parameters: int) =
    member _.Owner: sqlite3_stmt = owner
    member val Handle: nativeint = owner.DangerousGetHandle()
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
/// against), but every statement the models run goes through SQLite's C API directly, the way rusqlite drives SQLite: bound by
/// position, columns read by ordinal from the statement, no command, parameter or reader objects.
type Conn(raw: SqliteConnection, capacity: int) =
    let owner = (match raw.Handle with null -> invalidOp "the connection is not open" | handle -> handle)
    let db = owner.DangerousGetHandle()
    // Each statement with the tick at which it was last given back, for evicting the least recently used
    // (rusqlite's cache is an LRU too). A scan at eviction beats a list node per use.
    let cache = Dictionary<string, struct (Prepared * int64)>()
    let mutable tick = 0L
    let mutable closed = false
    // Text is encoded here to be bound (SQLite copies it), so a bind allocates nothing. Pinned, because its address is bound.
    let mutable scratch: byte[] = GC.AllocateArray<byte>(512, true)
    let mutable scratchAt: nativeint = Marshal.UnsafeAddrOfPinnedArrayElement(scratch, 0)

    let fail (rc: int) : exn =
        try
            SqliteException.ThrowExceptionForRC(rc, owner)
            InvalidOperationException $"SQLite returned {rc}"
        with e ->
            e

    let create (sql: string) : Prepared =
        let mutable stmt = Unchecked.defaultof<sqlite3_stmt>
        let rc = SQLitePCL.raw.sqlite3_prepare_v3 (owner, sql, Codes.PreparePersistent, &stmt)
        if rc <> 0 then raise (fail rc)
        Prepared(stmt, SQLitePCL.raw.sqlite3_bind_parameter_count stmt)

    let rent (sql: string) : Prepared =
        match cache.TryGetValue sql with
        | true, struct (prepared, _) ->
            cache.Remove sql |> ignore
            prepared
        | _ -> create sql

    let finalize (prepared: Prepared) : unit = SQLitePCL.raw.sqlite3_finalize prepared.Owner |> ignore

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

    /// An empty text still binds as text: SQLite binds a null pointer as NULL, so the scratch buffer's address stands in.
    let bindText (stmt: nativeint) (index: int) (text: string) : int =
        let needed = Encoding.UTF8.GetMaxByteCount text.Length
        if needed > scratch.Length then
            scratch <- GC.AllocateArray<byte>(max needed (scratch.Length * 2), true)
            scratchAt <- Marshal.UnsafeAddrOfPinnedArrayElement(scratch, 0)
        let length = Encoding.UTF8.GetBytes(text, 0, text.Length, scratch, 0)
        Native.sqlite3_bind_text (stmt, index, scratchAt, length, Native.Transient)

    let bindBlob (stmt: nativeint) (index: int) (bytes: byte[]) : int =
        if bytes.Length = 0 then
            Native.sqlite3_bind_blob (stmt, index, scratchAt, 0, Native.Transient)
        else
            use pinned = fixed bytes
            Native.sqlite3_bind_blob (stmt, index, NativePtr.toNativeInt pinned, bytes.Length, Native.Transient)

    let bind (prepared: Prepared) (sql: string) (args: SqlArg[]) : unit =
        if args.Length <> prepared.Parameters then
            invalidArg "args" $"{args.Length} arguments for {prepared.Parameters} placeholders in: {sql}"
        let stmt = prepared.Handle
        for i in 0 .. args.Length - 1 do
            let rc =
                match args[i] with
                | Null -> Native.sqlite3_bind_null (stmt, i + 1)
                | I v -> Native.sqlite3_bind_int64 (stmt, i + 1, v)
                | R v -> Native.sqlite3_bind_double (stmt, i + 1, v)
                | S v -> bindText stmt (i + 1) v
                | B v -> bindBlob stmt (i + 1) v
                | T v -> bindText stmt (i + 1) (v.ToDb())
            if rc <> 0 then raise (fail rc)

    /// `sqlite3_step`, raising what SQLite reports for anything but a row or the end.
    let step (stmt: nativeint) : bool =
        match Native.sqlite3_step stmt with
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
        Native.sqlite3_reset prepared.Handle |> ignore
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
    member _.IsAutocommit : bool = SQLitePCL.raw.sqlite3_get_autocommit owner <> 0

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
            while step prepared.Handle do
                ()
            let changed = Native.sqlite3_changes db
            succeeded <- true
            changed
        finally
            finish sql prepared succeeded

    /// `Connection::prepare` then `execute`: not cached, for SQL that is built per call.
    member _.ExecuteUncached(sql: string, args: SqlArg[]) : int =
        let prepared = create sql
        try
            bind prepared sql args
            while step prepared.Handle do
                ()
            Native.sqlite3_changes db
        finally
            finalize prepared

    /// `query_row_cached`: the first row, which must exist (an `INSERT ... RETURNING`).
    member _.QueryRow(sql: string, args: SqlArg[], map: Row -> 'T) : 'T =
        let prepared = start sql args
        let mutable succeeded = false
        try
            let value = if step prepared.Handle then map (Row prepared.Handle) else failwith $"no rows returned by: {sql}"
            succeeded <- true
            value
        finally
            finish sql prepared succeeded

    /// `query_one`: the first row, if there is one.
    member _.QueryOne(sql: string, args: SqlArg[], map: Row -> 'T) : 'T option =
        let prepared = start sql args
        let mutable succeeded = false
        try
            let value = if step prepared.Handle then Some(map (Row prepared.Handle)) else None
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
            let row = Row prepared.Handle
            while step prepared.Handle do
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
