// Tier 1 micro-benchmark of the database path (Phase 7, unit 7.1).
//
//   dotnet run -c Release --project bench/db-micro -- [SEED_DB] [SECONDS_PER_SCENARIO]
//
// SEED_DB defaults to parity/.seed/default/db/production.sqlite3 (copied to a temporary directory first). Each scenario is the
// Campfire.Db side of one benchmarked page: the same model calls, on one connection opened the way a reader is, mapping rows to the
// same records. It prints best-of-5 ns per iteration and bytes allocated per iteration, after a warm-up long enough for tiered
// compilation. The numbers compare two builds of Campfire.Db on this machine and are never reported as results.
module DbMicro

open System
open System.Diagnostics
open System.IO
open Campfire.Db

let private time (seconds: float) (f: unit -> unit) : float * float =
    // warm-up: the JIT tiers up in about 1 s of calls
    let warm = Stopwatch.StartNew()
    while warm.Elapsed.TotalSeconds < 2.0 do
        for _ in 1..100 do f ()
    let mutable best = Double.MaxValue
    let mutable alloc = 0.0
    for _ in 1..5 do
        let a0 = GC.GetAllocatedBytesForCurrentThread()
        let sw = Stopwatch.StartNew()
        let mutable n = 0
        while sw.Elapsed.TotalSeconds < seconds / 5.0 do
            for _ in 1..50 do f ()
            n <- n + 50
        let ns = sw.Elapsed.TotalMilliseconds * 1e6 / float n
        if ns < best then
            best <- ns
            alloc <- float (GC.GetAllocatedBytesForCurrentThread() - a0) / float n
    best, alloc

[<EntryPoint>]
let main argv =
    let root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."))
    let seed = if argv.Length > 0 then argv[0] else Path.Combine(root, "parity/.seed/default/db/production.sqlite3")
    let seconds = if argv.Length > 1 then float argv[1] else 3.0
    let dir = Directory.CreateTempSubdirectory("db-micro").FullName
    for suffix in [ ""; "-wal"; "-shm" ] do
        if File.Exists(seed + suffix) then File.Copy(seed + suffix, Path.Combine(dir, "production.sqlite3" + suffix))
    let conn = Conn.Open(Path.Combine(dir, "production.sqlite3"), 256)
    Schema.configureConnection conn
    conn.ExecuteBatch "PRAGMA query_only = ON"

    let token = conn.QueryRow("SELECT token FROM sessions ORDER BY id LIMIT 1", [||], fun r -> r.Text 0)
    let user = (User.findById conn (conn.QueryRow("SELECT user_id FROM sessions ORDER BY id LIMIT 1", [||], fun r -> r.Int64 0))).Value
    let room = conn.QueryRow("SELECT room_id FROM messages GROUP BY room_id ORDER BY count(*) DESC LIMIT 1", [||], fun r -> r.Int64 0)
    let middle = (Message.lastPage conn room)[10]

    let scenarios: (string * (unit -> unit)) list =
        [ "1 statement: Account.first", (fun () -> Account.first conn |> ignore)
          "session + user (2 lookups)", (fun () -> Session.findByToken conn token |> ignore; User.findById conn user.Id |> ignore)
          "room page db (room, last 40 messages, account, last room)",
          (fun () ->
              Room.findForUser conn user.Id room |> ignore
              Message.lastPage conn room |> ignore
              Account.first conn |> ignore
              Room.lastForUser conn user.Id |> ignore)
          "messages page db (page before)", (fun () -> Message.pageBefore conn room middle |> ignore)
          "sidebar db (memberships+rooms, members of directs, placeholders)",
          (fun () ->
              let all = Membership.visibleWithOrderedRoom conn user.Id
              for (_, r) in all do
                  if Room.isDirect r then Room.users conn r |> ignore
              Room.forUserOfType conn user.Id RoomType.Direct |> ignore
              User.findBySql conn """SELECT "users"."id", "users"."name", "users"."email_address", "users"."password_digest", "users"."role", "users"."status", "users"."bio", "users"."bot_token", "users"."created_at", "users"."updated_at" FROM "users" WHERE "users"."status" = 0 AND "users"."id" NOT IN (?, ?, ?) ORDER BY "users"."created_at" ASC LIMIT 17""" [| I 1L; I 2L; I user.Id |] |> ignore
              Account.first conn |> ignore)
          "search db (FTS match, recent searches)",
          (fun () ->
              Message.searchReachable conn user.Id "coffee" |> ignore
              Search.orderedForUser conn user.Id |> ignore) ]
    // What each scenario reads, so two builds can be checked to have read the same thing.
    printfn "rows: search %d, last page %d, page before %d, memberships %d" (Message.searchReachable conn user.Id "coffee").Length (Message.lastPage conn room).Length (Message.pageBefore conn room middle).Length (Membership.visibleWithOrderedRoom conn user.Id).Length
    printfn "%-70s %12s %12s" "scenario" "ns/iter" "bytes/iter"
    for (name, f) in scenarios do
        let ns, bytes = time seconds f
        printfn "%-70s %12.0f %12.0f" name ns bytes
    (conn :> IDisposable).Dispose()
    0
