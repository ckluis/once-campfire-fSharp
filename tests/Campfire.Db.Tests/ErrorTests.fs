// Port of the tests in rust/crates/db/src/error.rs
module Campfire.Db.Tests.ErrorTests

open System.IO
open Microsoft.Data.Sqlite
open Xunit
open Campfire.Db

[<Fact>]
let ``other errors keep their type and message`` () =
    let error = Other(IOException "disk full")
    Assert.Equal("disk full", DbError.display error)
    match error with
    | Other inner -> Assert.IsType<IOException>(inner) |> ignore
    | _ -> failwith "not Other"
    Assert.Equal("no table rooms", DbError.display (DbError.other (sprintf "no table %s" "rooms")))

[<Fact>]
let ``unique violations are record not unique`` () =
    use conn = Conn.OpenInMemory()
    conn.ExecuteBatch "CREATE TABLE users (id INTEGER PRIMARY KEY, email TEXT NOT NULL UNIQUE)"
    conn.ExecuteBatch "INSERT INTO users VALUES (1, 'a@example.com')"
    let insert (sql: string) =
        let e = Assert.Throws<SqliteException>(fun () -> conn.ExecuteBatch sql)
        Sqlite e

    Assert.True(DbError.isRecordNotUnique (insert "INSERT INTO users VALUES (2, 'a@example.com')"))
    Assert.True(DbError.isRecordNotUnique (insert "INSERT INTO users VALUES (1, 'b@example.com')"))
    Assert.False(DbError.isRecordNotUnique (insert "INSERT INTO users VALUES (3, NULL)"))
    Assert.False(DbError.isRecordNotUnique (RecordNotFound "User"))

[<Fact>]
let ``errors are messages in the order they were added`` () =
    let errors = Errors.empty |> Errors.add "endpoint" "must use HTTPS" |> Errors.add "user_id" "can't be blank"
    Assert.Equal<string list>([ "Endpoint must use HTTPS"; "User can't be blank" ], Errors.fullMessages errors)
    Assert.Equal<string list>([ "must use HTTPS" ], Errors.on "endpoint" errors)
    Assert.Equal("Validation failed: Endpoint must use HTTPS, User can't be blank", DbError.display (RecordInvalid errors))
