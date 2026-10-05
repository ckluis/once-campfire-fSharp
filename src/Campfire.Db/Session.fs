// Port of rust/crates/db/src/models/session.rs
// `reference/app/models/session.rb`
namespace Campfire.Db

open System

type Session =
    { Id: int64
      UserId: int64
      Token: string
      IpAddress: string option
      UserAgent: string option
      LastActiveAt: Timestamp
      CreatedAt: Timestamp
      UpdatedAt: Timestamp }

module Session =
    /// `Session::ACTIVITY_REFRESH_RATE`
    let activityRefreshRate : TimeSpan = TimeSpan.FromHours 1.0

    let private fromRow (r: Row) : Session =
        { Id = r.Int64 0
          UserId = r.Int64 1
          Token = r.Text 2
          IpAddress = r.OptText 3
          UserAgent = r.OptText 4
          LastActiveAt = r.Timestamp 5
          CreatedAt = r.Timestamp 6
          UpdatedAt = r.Timestamp 7 }

    let find (conn: Conn) (id: int64) : Session =
        conn.QueryOne($"SELECT {Columns.Session} FROM \"sessions\" WHERE \"sessions\".\"id\" = ? LIMIT 1", [| I id |], fromRow)
        |> Err.orNotFound "Session"

    /// `Session.find_by(token:)`
    let findByToken (conn: Conn) (token: string) : Session option =
        conn.QueryOne($"SELECT {Columns.Session} FROM \"sessions\" WHERE \"sessions\".\"token\" = ? LIMIT 1", [| S token |], fromRow)

    let forUser (conn: Conn) (userId: int64) : Session list =
        conn.QueryAll($"SELECT {Columns.Session} FROM \"sessions\" WHERE \"sessions\".\"user_id\" = ?", [| I userId |], fromRow)

    let countForUser (conn: Conn) (userId: int64) : int64 =
        conn.Count("""SELECT COUNT(*) FROM "sessions" WHERE "sessions"."user_id" = ?""", [| I userId |])

    /// `user.sessions.start!(user_agent:, ip_address:)`: a new 24-character base58
    /// `has_secure_token`, and `last_active_at ||= Time.now` in `before_create`.
    let start (tx: Tx) (userId: int64) (userAgent: string option) (ipAddress: string option) : Session =
        let now = tx.Now()
        let lastActiveAt = tx.Now()
        let token = Sql.base58 24
        let id =
            tx.Conn.QueryRow(
                """INSERT INTO "sessions" ("created_at", "ip_address", "last_active_at", "token", "updated_at", "user_agent", "user_id") VALUES (?, ?, ?, ?, ?, ?, ?) RETURNING "id" """,
                [| T now; Sql.optS ipAddress; T lastActiveAt; S token; T now; Sql.optS userAgent; I userId |],
                fun r -> r.Int64 0
            )
        { Id = id
          UserId = userId
          Token = token
          IpAddress = ipAddress
          UserAgent = userAgent
          LastActiveAt = lastActiveAt
          CreatedAt = now
          UpdatedAt = now }

    /// Whether `resume` would refresh the session at `now`: its activity is over an hour old.
    let needsResume (session: Session) (now: Timestamp) : bool = session.LastActiveAt < now.Ago activityRefreshRate

    /// `resume`: refreshes activity, user agent and IP at most once an hour.
    let resume (tx: Tx) (session: Session) (userAgent: string option) (ipAddress: string option) : Session =
        let now = tx.Now()
        if not (needsResume session now) then
            session
        else
            let resumed =
                { session with
                    UserAgent = userAgent
                    IpAddress = ipAddress
                    LastActiveAt = now
                    UpdatedAt = tx.Now() }
            tx.Conn.Execute(
                """UPDATE "sessions" SET "ip_address" = ?, "last_active_at" = ?, "updated_at" = ?, "user_agent" = ? WHERE "sessions"."id" = ?""",
                [| Sql.optS resumed.IpAddress; T resumed.LastActiveAt; T resumed.UpdatedAt; Sql.optS resumed.UserAgent; I resumed.Id |]
            )
            |> ignore
            resumed

    /// `destroy!`
    let destroy (tx: Tx) (session: Session) : unit =
        tx.Conn.Execute("""DELETE FROM "sessions" WHERE "sessions"."id" = ?""", [| I session.Id |]) |> ignore
