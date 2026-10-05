// Port of the tests in rust/crates/db/src/schema.rs
module Campfire.Db.Tests.SchemaTests

open System
open System.IO
open System.Security.Cryptography
open Xunit
open Campfire.Db
open Campfire.RailsCompat.Clock

let private reference = Campfire.Tests.Repo.path "reference/db"

[<Fact>]
let ``schema sha1 matches reference schema rb`` () =
    let contents = File.ReadAllBytes(Path.Combine(reference, "schema.rb"))
    Assert.Equal(Schema.SchemaSha1, Convert.ToHexStringLower(SHA1.HashData contents))

[<Fact>]
let ``migration versions match reference migrations`` () =
    let versions =
        Directory.GetFiles(Path.Combine(reference, "migrate"))
        |> Array.map (fun f -> (nonNull (Path.GetFileName f)).Split('_')[0])
        |> Array.sort
        |> List.ofArray
    Assert.Equal<string list>(versions, Schema.migrationVersions)

[<Fact>]
let ``prepare loads then is idempotent`` () =
    use conn = Conn.OpenInMemory()
    Assert.Equal(Schema.Loaded, Schema.prepare conn "production" (SystemClock()))
    Assert.Equal(Schema.UpToDate, Schema.prepare conn "production" (SystemClock()))

    let first = conn.QueryRow("SELECT version FROM schema_migrations ORDER BY rowid LIMIT 1", [||], fun r -> r.Text 0)
    Assert.Equal("20251212154340", first)
    let sha = conn.QueryRow("SELECT value FROM ar_internal_metadata WHERE key = 'schema_sha1'", [||], fun r -> r.Text 0)
    Assert.Equal(Schema.SchemaSha1, sha)
    conn.Execute("INSERT INTO message_search_index(rowid, body) VALUES (1, 'running dogs')", [||]) |> ignore
    let hit = conn.QueryRow("SELECT rowid FROM message_search_index WHERE body MATCH 'run'", [||], fun r -> r.Int64 0)
    Assert.Equal(1L, hit) // porter tokenizer

/// The plan SQLite makes now: each call has its own SQL, since a cached EXPLAIN keeps the plan it
/// was prepared with.
let private queryPlan (conn: Conn) (sql: string) : string =
    conn.QueryAll($"EXPLAIN QUERY PLAN {sql} /* {Guid.NewGuid()} */", [||], fun r -> r.Text 3) |> String.concat "; "

[<Fact>]
let ``prepare adds the room paging index to new and existing databases`` () =
    let lastPage = """SELECT * FROM "messages" WHERE "room_id" = 1 ORDER BY "created_at" DESC LIMIT 40"""
    use conn = Conn.OpenInMemory()
    Schema.prepare conn "production" (SystemClock()) |> ignore
    let plan = queryPlan conn lastPage
    Assert.True(plan.Contains "index_messages_on_room_id_and_created_at" && not (plan.Contains "TEMP B-TREE"), plan)

    // A database the Rails app created doesn't have it until the app boots on it.
    conn.ExecuteBatch """DROP INDEX "index_messages_on_room_id_and_created_at" """
    Assert.Contains("TEMP B-TREE", queryPlan conn lastPage)
    Assert.Equal(Schema.UpToDate, Schema.prepare conn "production" (SystemClock()))
    Assert.DoesNotContain("TEMP B-TREE", queryPlan conn lastPage)

[<Fact>]
let ``prepare reports pending migrations`` () =
    use conn = Conn.OpenInMemory()
    Schema.prepare conn "production" (SystemClock()) |> ignore
    conn.Execute("DELETE FROM schema_migrations WHERE version = '20251212154340'", [||]) |> ignore
    Assert.Throws<DbException>(fun () -> Schema.prepare conn "production" (SystemClock()) |> ignore) |> ignore
