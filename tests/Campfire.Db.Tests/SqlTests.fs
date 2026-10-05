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
