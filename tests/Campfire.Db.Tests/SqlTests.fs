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
let ``placeholders are numbered outside quotes`` () =
    let numbered, count = Binding.numberPlaceholders """SELECT '?', "?", ?, ? FROM t WHERE a = 'it''s ?' AND b = ?"""
    Assert.Equal("""SELECT '?', "?", ?1, ?2 FROM t WHERE a = 'it''s ?' AND b = ?3""", numbered)
    Assert.Equal(3, count)

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
