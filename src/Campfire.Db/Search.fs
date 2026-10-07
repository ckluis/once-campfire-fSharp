// Port of rust/crates/db/src/models/search.rs
// `reference/app/models/search.rb`
namespace Campfire.Db

type Search =
    { Id: int64
      UserId: int64
      Query: string
      CreatedAt: Timestamp
      UpdatedAt: Timestamp }

module Search =
    /// How many recent searches `trim_recent_searches` keeps.
    [<Literal>]
    let RecentSearches = 10L

    [<Literal>]
    let private select = "SELECT " + Columns.Search + " FROM \"searches\""

    let private fromRow (r: Row) : Search =
        { Id = r.Int64 0
          UserId = r.Int64 1
          Query = r.Text 2
          CreatedAt = r.Timestamp 3
          UpdatedAt = r.Timestamp 4 }

    /// `user.searches.ordered`: most recent first.
    let orderedForUser (conn: Conn) (userId: int64) : Search list =
        conn.QueryAll(select + " WHERE \"searches\".\"user_id\" = ? ORDER BY \"searches\".\"updated_at\" DESC", [| I userId |], fromRow)

    let count (conn: Conn) : int64 = conn.Count("""SELECT COUNT(*) FROM "searches" """, [||])

    let countForUser (conn: Conn) (userId: int64) : int64 =
        conn.Count("""SELECT COUNT(*) FROM "searches" WHERE "searches"."user_id" = ?""", [| I userId |])

    /// `user.searches.excluding(user.searches.ordered.limit(10)).destroy_all`
    let private trimRecentSearches (tx: Tx) (userId: int64) : unit =
        let keep =
            tx.Conn.QueryAll(
                """SELECT "searches"."id" FROM "searches" WHERE "searches"."user_id" = ? ORDER BY "searches"."updated_at" DESC LIMIT ?""",
                [| I userId; I RecentSearches |],
                fun r -> r.Int64 0
            )
        let sql =
            "SELECT \"searches\".\"id\" FROM \"searches\" WHERE \"searches\".\"user_id\" = ? AND \"searches\".\"id\" NOT IN ("
            + Sql.placeholders (max (List.length keep) 1)
            + ")"
        let kept = if List.isEmpty keep then [ 0L ] else keep
        let doomed = tx.Conn.QueryAll(sql, Array.ofList (I userId :: (kept |> List.map I)), fun r -> r.Int64 0)
        for id in doomed do
            tx.Conn.Execute("""DELETE FROM "searches" WHERE "searches"."id" = ?""", [| I id |]) |> ignore

    let private create (tx: Tx) (userId: int64) (query: string) : Search =
        let now = tx.Now()
        let id =
            tx.Conn.QueryRow(
                """INSERT INTO "searches" ("created_at", "query", "updated_at", "user_id") VALUES (?, ?, ?, ?) RETURNING "id" """,
                [| T now; S query; T now; I userId |],
                fun r -> r.Int64 0
            )
        trimRecentSearches tx userId
        { Id = id
          UserId = userId
          Query = query
          CreatedAt = now
          UpdatedAt = now }

    /// `user.searches.record(query)`: `find_or_create_by(query:).touch`. Creating trims the
    /// user's searches to the ten most recent.
    let record (tx: Tx) (userId: int64) (query: string) : Search =
        let existing =
            tx.Conn.QueryOne(
                select + " WHERE \"searches\".\"user_id\" = ? AND \"searches\".\"query\" = ? LIMIT 1",
                [| I userId; S query |],
                fromRow
            )
        let search =
            match existing with
            | Some search -> search
            | None -> create tx userId query
        let now = tx.Now()
        tx.Conn.Execute("""UPDATE "searches" SET "updated_at" = ? WHERE "searches"."id" = ?""", [| T now; I search.Id |]) |> ignore
        { search with UpdatedAt = now }

    /// `user.searches.destroy_all`
    let destroyAllForUser (tx: Tx) (userId: int64) : unit =
        let ids =
            tx.Conn.QueryAll("""SELECT "searches"."id" FROM "searches" WHERE "searches"."user_id" = ?""", [| I userId |], fun r -> r.Int64 0)
        for id in ids do
            tx.Conn.Execute("""DELETE FROM "searches" WHERE "searches"."id" = ?""", [| I id |]) |> ignore
