// Tests for the statement cache and binding of src/Campfire.Db/Sql.fs (rust/crates/db/src/sql.rs's
// `CachedStatements`, which rusqlite's `prepare_cached` does there).
module Campfire.Db.Tests.SqlTests

open System
open Xunit
open Campfire.Db

let private table (conn: Conn) : unit =
    conn.ExecuteBatch "CREATE TABLE things (id INTEGER PRIMARY KEY, name TEXT, at TEXT, n REAL, data BLOB)"

[<Fact>]
let ``a statement is compiled once per connection and reused`` () =
    use conn = Conn.OpenInMemory()
    table conn
    for n in 1L .. 5L do
        conn.Execute("INSERT INTO things (id, name) VALUES (?, ?)", [| I n; S $"thing {n}" |]) |> ignore
    Assert.Equal(1, conn.CachedStatementCount)
    for n in 1L .. 5L do
        Assert.Equal($"thing {n}", conn.QueryRow("SELECT name FROM things WHERE id = ?", [| I n |], fun r -> r.Text 0))
    Assert.Equal(2, conn.CachedStatementCount)
    // A query run while the same query is open runs on a statement of its own.
    let nested =
        conn.QueryAll(
            "SELECT id FROM things ORDER BY id LIMIT 2",
            [||],
            fun r -> r.Int64 0, conn.QueryAll("SELECT id FROM things ORDER BY id LIMIT 2", [||], fun r -> r.Int64 0)
        )
    Assert.Equal(2, List.length nested)
    Assert.Equal(3, conn.CachedStatementCount)

[<Fact>]
let ``the statement cache holds only its capacity`` () =
    use conn = Conn.Open(":memory:", 4)
    table conn
    for n in 1..10 do
        conn.Execute($"INSERT INTO things (id) VALUES ({n})", [||]) |> ignore
    Assert.Equal(4, conn.CachedStatementCount)
    Assert.Equal(10L, conn.Count("SELECT COUNT(*) FROM things", [||]))

[<Fact>]
let ``the statement cache evicts the least recently used statement`` () =
    use conn = Conn.Open(":memory:", 3)
    table conn
    let a = "SELECT id FROM things WHERE id = ?"
    let b = "SELECT name FROM things WHERE id = ?"
    let c = "SELECT at FROM things WHERE id = ?"
    let d = "SELECT n FROM things WHERE id = ?"
    let run (sql: string) = conn.QueryAll(sql, [| I 1L |], fun _ -> ()) |> ignore
    for sql in [ a; b; c ] do
        run sql
    // `a` was the first in, and is used again: `b` is now the one unused for longest.
    run a
    run d
    Assert.Equal<string list>(List.sort [ a; c; d ], conn.CachedStatements |> List.sort)
    // A statement a read has borrowed and returned counts as used then, as in rusqlite.
    run c
    run b
    Assert.Equal<string list>(List.sort [ b; c; d ], conn.CachedStatements |> List.sort)

[<Fact>]
let ``a failed statement is dropped from the cache`` () =
    use conn = Conn.OpenInMemory()
    table conn
    conn.Execute("INSERT INTO things (id) VALUES (?)", [| I 1L |]) |> ignore
    Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(fun () -> conn.Execute("INSERT INTO things (id) VALUES (?)", [| I 1L |]) |> ignore)
    |> ignore
    Assert.Equal(0, conn.CachedStatementCount)
    // The same statement then works again, with a fresh compile.
    Assert.Equal(1, conn.Execute("INSERT INTO things (id) VALUES (?)", [| I 2L |]))

[<Fact>]
let ``a question mark inside quotes is not a placeholder`` () =
    use conn = Conn.OpenInMemory()
    let value =
        conn.QueryRow("""SELECT '?', ?, 'it''s ?' || ?""", [| I 41L; S "!" |], fun r -> r.Text 0, r.Int64 1, r.Text 2)
    Assert.Equal(("?", 41L, "it's ?!"), value)
    Assert.Throws<ArgumentException>(fun () -> conn.QueryRow("SELECT ?, ?", [| I 1L |], fun r -> r.Int64 0) |> ignore) |> ignore

[<Fact>]
let ``an empty string binds and reads back as text, not NULL`` () =
    use conn = Conn.OpenInMemory()
    table conn
    conn.Execute("INSERT INTO things (id, name) VALUES (?, ?)", [| I 1L; S "" |]) |> ignore
    conn.Execute("INSERT INTO things (id, name) VALUES (?, ?)", [| I 2L; Null |]) |> ignore
    conn.Execute("INSERT INTO things (id, name, data) VALUES (?, ?, ?)", [| I 3L; S "héllo \u00e9\U0001F600"; B [||] |]) |> ignore
    let read (id: int64) = conn.QueryRow("SELECT name, typeof(name), data, typeof(data) FROM things WHERE id = ?", [| I id |], fun r -> r.OptText 0, r.Text 1, r.Arg 2, r.Text 3)
    Assert.Equal((Some "", "text", Null, "null"), read 1L)
    Assert.Equal((None, "null", Null, "null"), read 2L)
    let name, _, data, dataType = read 3L
    Assert.Equal(Some "héllo \u00e9\U0001F600", name)
    Assert.Equal("blob", dataType)
    Assert.Equal<byte[]>([||], (match data with B v -> v | _ -> [| 9uy |]))
    // Required accessors refuse a NULL; a stored zero is not a NULL.
    Assert.Throws<DbException>(fun () -> conn.QueryRow("SELECT name FROM things WHERE id = 2", [||], fun r -> r.Text 0) |> ignore) |> ignore
    Assert.Throws<DbException>(fun () -> conn.QueryRow("SELECT n FROM things WHERE id = 2", [||], fun r -> r.Int64 0) |> ignore) |> ignore
    Assert.Equal(0L, conn.QueryRow("SELECT 0", [||], fun r -> r.Int64 0))
    Assert.Equal(Some 0L, conn.QueryRow("SELECT 0", [||], fun r -> r.OptInt64 0))
    Assert.Equal(None, conn.QueryRow("SELECT NULL", [||], fun r -> r.OptInt64 0))

[<Fact>]
let ``every kind of value binds and reads back`` () =
    use conn = Conn.OpenInMemory()
    table conn
    let at = (Timestamp.ParseDb "2026-09-26 12:34:56.123456").Value
    conn.Execute(
        "INSERT INTO things (id, name, at, n, data) VALUES (?, ?, ?, ?, ?)",
        [| I 7L; Sql.optS (Some "seven"); T at; R 7.5; B [| 1uy; 2uy; 3uy |] |]
    )
    |> ignore
    conn.Execute("INSERT INTO things (id, name, at, n, data) VALUES (?, ?, ?, ?, ?)", [| I 8L; Sql.optS None; Sql.optT None; Null; Null |]) |> ignore
    let row (id: int64) =
        conn.QueryRow(
            "SELECT name, at, n, data FROM things WHERE id = ?",
            [| I id |],
            fun r -> r.OptText 0, r.OptTimestamp 1, r.Arg 2, r.Arg 3
        )
    let name, stamp, real, blob = row 7L
    Assert.Equal(Some "seven", name)
    Assert.Equal(Some at, stamp)
    Assert.Equal<float>(7.5, (match real with R v -> v | _ -> nan))
    Assert.Equal<byte[]>([| 1uy; 2uy; 3uy |], (match blob with B v -> v | _ -> [||]))
    let name, stamp, real, blob = row 8L
    Assert.Equal(None, name)
    Assert.Equal(None, stamp)
    Assert.True((match real, blob with Null, Null -> true | _ -> false))

[<Fact>]
let ``the wrong number of arguments is refused`` () =
    use conn = Conn.OpenInMemory()
    table conn
    Assert.Throws<ArgumentException>(fun () -> conn.Execute("INSERT INTO things (id, name) VALUES (?, ?)", [| I 1L |]) |> ignore) |> ignore

let private isConversionError (error: DbError) : bool =
    match error with
    | Other(:? FormatException)
    | Other(:? InvalidCastException) -> true
    | _ -> false

[<Fact>]
let ``a stored value that cannot be converted is an error, not an exception`` () =
    use conn = Conn.OpenInMemory()
    table conn
    conn.Execute("INSERT INTO things (id, name, at) VALUES (?, ?, ?)", [| I 1L; S "garbage"; S "garbage" |]) |> ignore
    conn.Execute("INSERT INTO things (id) VALUES (?)", [| I 2L |]) |> ignore
    // rusqlite's `row.get` fails for a NULL read as `i64` or `String`, and for a text that is no datetime.
    let failure (id: int64) (read: Row -> unit) =
        let ex = Assert.ThrowsAny<exn>(fun () -> conn.QueryOne("SELECT id, name, at FROM things WHERE id = ?", [| I id |], read) |> ignore)
        match ex with
        | DbException e -> Assert.True(isConversionError e, DbError.display e)
        | other -> failwith $"escaped as {other.GetType().Name}: {other.Message}"
    failure 1L (fun r -> r.Timestamp 2 |> ignore)
    failure 1L (fun r -> r.OptTimestamp 2 |> ignore)
    failure 2L (fun r -> r.Text 1 |> ignore)
    failure 2L (fun r -> r.Text 2 |> ignore)
    failure 2L (fun r -> r.Timestamp 2 |> ignore)
    // `row.get::<Option<_>>` is fine with NULL, and an ordinary value still reads.
    Assert.Equal(None, conn.QueryRow("SELECT name FROM things WHERE id = 2", [||], fun r -> r.OptText 0))
    Assert.Equal(2L, conn.QueryRow("SELECT id FROM things WHERE id = 2", [||], fun r -> r.Int64 0))
    let nullInt = Assert.ThrowsAny<exn>(fun () -> conn.QueryRow("SELECT NULL", [||], fun r -> r.Int64 0) |> ignore)
    match nullInt with
    | DbException e -> Assert.True(isConversionError e, DbError.display e)
    | other -> failwith $"escaped as {other.GetType().Name}: {other.Message}"
    // The statement that raised is dropped and the connection still works.
    Assert.Equal(1L, conn.Count("SELECT COUNT(*) FROM things WHERE id = 1", [||]))

/// `SuppressGCTransition` is for a short, non-allocating call: a thread in one blocks a GC's suspension until it returns. Binding with
/// `SQLITE_TRANSIENT` mallocs and copies the whole value, so only a value of at most `Native.ShortBind` bytes goes through the
/// import that suppresses it.
[<Fact>]
let ``binding a long value does not suppress the GC transition`` () =
    let native = nonNull (typeof<Conn>.Assembly.GetType "Campfire.Db.Native")
    let suppresses (name: string) =
        let flags = Reflection.BindingFlags.Static ||| Reflection.BindingFlags.Public ||| Reflection.BindingFlags.NonPublic
        let methodInfo = nonNull (native.GetMethod(name, flags))
        methodInfo.GetCustomAttributes(typeof<System.Runtime.InteropServices.SuppressGCTransitionAttribute>, false).Length > 0
    Assert.False(suppresses "sqlite3_bind_text")
    Assert.False(suppresses "sqlite3_bind_blob")
    Assert.True(suppresses "sqlite3_bind_text_short")
    Assert.True(suppresses "sqlite3_bind_blob_short")
    // the readers and the scalar binds stay as they were
    for name in [ "sqlite3_column_int64"; "sqlite3_column_type"; "sqlite3_column_bytes"; "sqlite3_bind_int64" ] do
        Assert.True(suppresses name, name)

[<Fact>]
let ``text and blobs bind and read back at every size around the short threshold`` () =
    use conn = Conn.OpenInMemory()
    table conn
    let short = Native.ShortBind
    let sizes = [ 0; 1; short - 1; short; short + 1; 4096; 1_000_000; 8_000_000 ]
    let mutable id = 0L
    for size in sizes do
        id <- id + 1L
        // text of `size` UTF-8 bytes (ASCII), with a multi-byte tail when there is room, and a blob of `size` bytes
        let text = String('t', size)
        let blob = Array.init size (fun i -> byte (i * 31 % 251))
        conn.Execute("INSERT INTO things (id, name, data) VALUES (?, ?, ?)", [| I id; S text; B blob |]) |> ignore
        let name, data = conn.QueryRow("SELECT name, data FROM things WHERE id = ?", [| I id |], fun r -> r.Text 0, r.Arg 1)
        Assert.Equal(text.Length, name.Length)
        Assert.True(String.Equals(text, name, StringComparison.Ordinal))
        match data with
        | B read -> Assert.True(Span<byte>(blob).SequenceEqual(ReadOnlySpan<byte>(read)), $"blob of {size}")
        | other -> if size = 0 then Assert.Equal(B [||], other) else failwith $"blob of {size} read as {other}"
    // multi-byte text across the threshold counts bytes, not characters
    for chars in [ short / 4 - 1; short / 4; short / 4 + 1 ] do
        id <- id + 1L
        let text = String.replicate chars "\u00e9\u00e9\u00e9\u00e9"
        conn.Execute("INSERT INTO things (id, name) VALUES (?, ?)", [| I id; S text |]) |> ignore
        Assert.Equal(text, conn.QueryRow("SELECT name FROM things WHERE id = ?", [| I id |], fun r -> r.Text 0))
        Assert.Equal(int64 (text.Length * 2), conn.QueryRow("SELECT length(CAST(name AS BLOB)) FROM things WHERE id = ?", [| I id |], fun r -> r.Int64 0))

[<Fact>]
let ``the library runs without its memory statistics mutex`` () =
    use conn = Conn.OpenInMemory()
    table conn
    conn.Execute("INSERT INTO things (id, name) VALUES (?, ?)", [| I 1L; S "x" |]) |> ignore
    // With SQLITE_CONFIG_MEMSTATUS off (Library.ensureConfigured, before the first connection) SQLite keeps no count.
    Assert.Equal(0L, SQLitePCL.raw.sqlite3_memory_used ())
