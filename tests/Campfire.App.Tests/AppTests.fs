// Port of rust/crates/campfire/src/app/tests.rs
//
// Boot-level tests: the whole stack over a copy of the reference-built `default` parity seed
// (`parity/bin/seed build default`), with the parity `SECRET_KEY_BASE`. Skipped (with a message) when the
// seed hasn't been built.
//
// `vectors/campfire_sessions.json` holds session cookies *issued by Rails* for the seed's sessions
// (`reference-tools/campfire/session_cookies.rb`).
module Campfire.App.Tests.AppTests

open System
open System.IO
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Xunit
open Campfire.App
open Campfire.App.Presenters
open Campfire.App.Tests.Support
open Campfire.Assets
open Campfire.Db
open Campfire.Kit
open Campfire.Storage
open Campfire.Tests

let private unwrap (result: Result<'T, DbError>) : 'T =
    match result with
    | Ok value -> value
    | Error e -> failwith (DbError.display e)

[<Fact>]
let ``health check`` () =
    task {
        use! test = bootSeeded "default"
        let! up = test.Send(get "/up")
        Assert.Equal(200, up.Status)
        Assert.Equal("""<!DOCTYPE html><html><body style="background-color: green"></body></html>""", up.Text)
        Assert.Equal(Some "text/html; charset=utf-8", up.Header "content-type")

        let! json = test.Send(get "/up.json")
        Assert.Equal(200, json.Status)
        use value = JsonDocument.Parse json.Body
        Assert.Equal("up", str (value.RootElement.GetProperty "status"))
    }

[<Fact>]
let ``public files are served before routing`` () =
    task {
        use! test = bootSeeded "default"
        let css = Assets.stylesheetPath ((Assets.allStylesheetPaths ()).[0])
        let! reply = test.Send(get css)
        Assert.True((reply.Status = 200), css)
        Assert.Equal(Some "public, max-age=2592000", reply.Header "cache-control")

        let! robots = test.Send(get "/robots.txt")
        Assert.Equal(200, robots.Status)
    }

/// Every file `ActionDispatch::Static` serves: `public/` and the digested assets.
let private staticCorpus () : string list =
    let publicFiles = [ "/robots.txt"; "/404.html"; "/422.html"; "/500.html"; "/502.html"; "/assets/.manifest.json" ]
    let assets = [ for (_, digested) in Assets.manifest () -> $"{Assets.Prefix}/{digested}" ]
    publicFiles @ assets

[<Fact>]
let ``static responses send the embedded bytes without copying them`` () =
    let serve meth path range ifModifiedSince : Serve.StaticResponse option =
        Assets.serve { Method = meth; Path = path; AcceptEncoding = None; Range = range; IfModifiedSince = ifModifiedSince }
    let builtAt = (serve "GET" "/robots.txt" None None).Value |> Serve.StaticResponse.header "last-modified" |> Option.get
    let variants =
        [ "GET", None, None
          "HEAD", None, None
          "GET", Some "bytes=1-10", None
          "GET", Some "bytes=0-1, 4-5", None
          "GET", Some "bytes=999999999-", None
          "GET", None, Some builtAt ]
    for path in staticCorpus () do
        for (meth, range, ifModifiedSince) in variants do
            let served = (serve meth path range ifModifiedSince).Value
            let response = (Boot.staticResponse meth path None range ifModifiedSince).Value
            let case = $"{meth} {path} {range} {ifModifiedSince}"
            Assert.True((response.Status = served.Status), case)
            let headers = [ for i in 0 .. response.Headers.Count - 1 -> response.Headers.NameAt i, response.Headers.ValueAt i ]
            let expected = [ for (name, value) in served.Headers -> name.ToLowerInvariant(), value ]
            Assert.True((headers = expected), case)
            match response.Body with
            | Body.Bytes body ->
                Assert.True(body.Span.SequenceEqual served.Body.Span, case)
                // Sent from the embedded bytes themselves: the same memory, not a copy.
                // (Several ranges, and a 416's message, are assembled into a body of their own.)
                let multipart = served.Status = 206 && (range |> Option.exists (fun r -> r.Contains ','))
                let borrowed = served.Status = 200 || (served.Status = 206 && not multipart)
                if not body.IsEmpty && borrowed then Assert.True(body.Span.Overlaps served.Body.Span, $"{case}: sent from the embedded bytes")
            | Body.Empty -> Assert.True(served.Body.IsEmpty, case)
            | other -> failwith $"{case}: a static response has a buffer body, got {other}"

/// Times `staticResponse` for an identity GET of a small public file, the first stylesheet, the largest
/// script and the largest file; per call, median and range of 7 runs. Set `CAMPFIRE_TIMING=1`: the numbers
/// are in bench/results/campfire-app-boot.md (Rust's are in its bench/results/static-body-20260930).
[<Fact>]
let ``static response timing`` () =
    if Environment.GetEnvironmentVariable "CAMPFIRE_TIMING" = "1" then
        let size (path: string) =
            match Assets.serve (Serve.StaticRequest.create "GET" path) with
            | Some response -> response.Body.Length
            | None -> 0
        let largest (suffix: string) = staticCorpus () |> List.filter (fun p -> p.EndsWith suffix) |> List.maxBy size
        let stylesheet = Assets.stylesheetPath ((Assets.allStylesheetPaths ()).[0])
        for path in [ "/robots.txt"; stylesheet; largest ".js"; largest "" ] do
            let iterations = 20_000
            let runs =
                [ for _ in 1..7 do
                      let started = Diagnostics.Stopwatch.GetTimestamp()
                      for _ in 1..iterations do
                          Boot.staticResponse "GET" path None None None |> ignore
                      Diagnostics.Stopwatch.GetElapsedTime(started).TotalNanoseconds / float iterations ]
                |> List.sort
            Console.Error.WriteLine $"{path} ({size path} bytes): median {runs[3]:F0} ns, range {runs[0]:F0}..{runs[6]:F0} ns"

[<Fact>]
let ``unknown and unported routes`` () =
    task {
        use! test = bootSeeded "default"
        let! missing = test.Send(get "/nope")
        Assert.Equal(404, missing.Status)
        Assert.True(missing.Body.Length > 0, "renders public/404.html")

        let! settings = test.Send(get "/rooms/1/settings")
        Assert.Equal(500, settings.Status)
    }

[<Fact>]
let ``database errors answer as active record rescues them`` () =
    Assert.Equal(404, Error.status (AppErrors.dbError (RecordNotFound "Room")))
    Assert.Equal(500, Error.status (AppErrors.dbError WriterGone))

    let errors = Errors.empty |> Errors.add "endpoint" "must use HTTPS"
    let invalid = AppErrors.dbError (RecordInvalid errors)
    Assert.Equal(422, Error.status invalid)
    Assert.Equal("422 Unprocessable Entity: Validation failed: Endpoint must use HTTPS", Error.display invalid)

/// An action behind `ApplicationController`'s chain that answers with `Current.user`.
let private whoami (c: Ctx) : Task<Result<Response, Error>> =
    act {
        do! Concerns.beforeActions c Before.Default
        let name = Concerns.currentUser c |> Option.map (fun user -> user.Name) |> Option.defaultValue ""
        return c.Html name
    }

let private platformReply (c: Ctx) : Task<Result<Response, Error>> =
    act {
        let kept = (c.Current<ApplicationPlatform>()).IsSome
        let view = ApplicationPlatform.toView (Concerns.platform c)
        let flag = if kept then "true" else "false"
        let text = $"{flag} \"{view.Browser}\" \"{view.OperatingSystem}\""
        return c.Html text
    }

/// An action behind the chain that answers with whether `allow_browser` kept a platform, and the browser and
/// operating system the layout sees.
let private platformAction (c: Ctx) : Task<Result<Response, Error>> =
    act {
        do! Concerns.beforeActions c (Before.allowUnauthenticatedAccess Before.Default)
        return! platformReply c
    }

let private kitFor (app: AppState) : Kit = Kit(KitConfig.Production true, app.Secrets, app.Clock, app)

let private whoamiPipeline (app: AppState) : Microsoft.AspNetCore.Builder.IApplicationBuilder -> unit =
    let kit = kitFor app
    Adapter.app kit [ Adapter.route kit "/whoami" [ "GET", whoami; "POST", whoami ] ]

[<Fact>]
let ``a rails issued session cookie authenticates`` () =
    task {
        use! test = bootSeeded "default"
        let sessions, _, forged = sessionVectors ()
        let session = sessions[0]
        use! host = startPipeline (whoamiPipeline test.App)

        let! signedIn = host.Send(getWithCookie "/whoami" session.CookieHeader)
        Assert.Equal(200, signedIn.Status)
        Assert.Equal(session.UserName, signedIn.Text)
        Assert.Equal(Some "parity", signedIn.Header "x-version")
        Assert.Equal(Some "parity", signedIn.Header "x-rev")
        let cookies = signedIn.HeaderValues "set-cookie"
        Assert.True(
            (cookies |> List.exists (fun c -> c.StartsWith "session_token=" && c.Contains "httponly" && c.Contains "samesite=lax")),
            $"{cookies}"
        )

        // That request refreshed the seed's stale session; for the next hour it isn't touched again, so the
        // cookie isn't re-sent (Rails re-signs it on every request).
        let! again = host.Send(getWithCookie "/whoami" session.CookieHeader)
        Assert.Equal(session.UserName, again.Text)
        Assert.True(again.Header("set-cookie").IsNone, $"{again.Headers}")

        for cookie in [ None; Some forged; Some "session_token=tampered--0000" ] do
            let! anonymous =
                match cookie with
                | Some cookie -> host.Send(getWithCookie "/whoami" cookie)
                | None -> host.Send(get "/whoami")
            Assert.True((anonymous.Status = 302), $"{cookie}")
            Assert.True((anonymous.Header "location" = Some "http://campfire.test/session/new"), $"{cookie}")
    }

[<Fact>]
let ``the application chain blocks banned ips forgeries and old browsers`` () =
    task {
        use! test = bootSeeded "default"
        let sessions, _, _ = sessionVectors ()
        let cookie = sessions[0].CookieHeader
        use! host = startPipeline (whoamiPipeline test.App)
        let post (name: string) (value: string) = (request "POST" "/whoami").With("cookie", cookie).With(name, value)

        // `ips.banned` in the seed's labels.
        let! banned = host.Send(post "x-forwarded-for" "203.0.113.9")
        Assert.Equal(429, banned.Status)
        Assert.Equal(Some "text/html", banned.Header "content-type")

        // A post another site's page makes: the browser says so in `Sec-Fetch-Site`.
        let! forged = host.Send(post "sec-fetch-site" "cross-site")
        Assert.Equal(422, forged.Status)

        let outdated =
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/100.0.0.0 Safari/537.36"
        let! oldBrowser = host.Send((getWithCookie "/whoami" cookie).With("user-agent", outdated))
        Assert.Equal(200, oldBrowser.Status)
        Assert.NotEqual<string>(sessions[0].UserName, oldBrowser.Text)

        // The incompatible-browser page is an explicit `render template:`: HTML whatever the format.
        for (path, accept) in [ "/webmanifest.json", "*/*"; "/service-worker.js", "*/*"; "/session/new", "application/json" ] do
            let! blocked = test.Send((get path).With("user-agent", outdated).With("accept", accept))
            Assert.True(((blocked.Status, blocked.Header "content-type") = (200, Some "text/html; charset=utf-8")), $"{path} {accept}")
        // In a Live controller (`include ActiveStorage::Streaming`) Rack::ETag can't digest the body.
        let! logo = test.Send((get "/account/logo").With("user-agent", outdated))
        Assert.Equal((200, Some "no-cache", None), (logo.Status, logo.Header "cache-control", logo.Header "etag"))
    }

[<Fact>]
let ``allow browser keeps the platform it parsed for the layout`` () =
    task {
        use! test = bootSeeded "default"
        let kit = kitFor test.App
        use! host =
            startPipeline (
                Adapter.app
                    kit
                    [ Adapter.route kit "/platform" [ "GET", platformAction ]
                      Adapter.route kit "/platform/without_the_chain" [ "GET", platformReply ] ]
            )

        let chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36"
        // Without a User-Agent to check, the layout parses the gem's default, "Mozilla/4.0 (compatible)".
        // Where `allow_browser` didn't run, the layout parses the header itself.
        let cases =
            [ "/platform", Some chrome, "true \"Chrome\" \"Windows\""
              "/platform", None, "false \"Mozilla\" \"\""
              "/platform", Some " ", "false \"Mozilla\" \"\""
              "/platform/without_the_chain", Some chrome, "false \"Chrome\" \"Windows\""
              "/platform/without_the_chain", None, "false \"Mozilla\" \"\"" ]
        for (path, userAgent, expected) in cases do
            let req = match userAgent with Some ua -> (get path).With("user-agent", ua) | None -> get path
            let! reply = host.Send req
            Assert.True(((reply.Status, reply.Text) = (200, expected)), $"{path} {userAgent}")
    }

[<Fact>]
let ``blob redirects to a signed disk url`` () =
    task {
        use! test = bootSeeded "default"
        let _, blobs, _ = sessionVectors ()
        let redirectPath = blobs[0]
        let filename = redirectPath.Substring(redirectPath.LastIndexOf '/' + 1)

        let! redirect = test.Send(get redirectPath)
        Assert.Equal(302, redirect.Status)
        Assert.Equal(Some "max-age=300, private", redirect.Header "cache-control")
        let location = (redirect.Header "location").Value
        Assert.StartsWith("http://campfire.test/rails/active_storage/disk/", location)
        Assert.EndsWith($"/{filename}", location)

        let diskPath = location.Substring "http://campfire.test".Length
        let! file = test.Send(get diskPath)
        Assert.Equal(200, file.Status)
        Assert.Equal(Some "max-age=3600, public", file.Header "cache-control")
        Assert.Equal(Some "image/jpeg", file.Header "content-type")
        Assert.True((file.Header "content-disposition" |> Option.map (fun d -> d.StartsWith "inline;")) = Some true)
        Assert.True(file.Body.Length > 0)

        let regex = Text.RegularExpressions.Regex("--")
        let tampered = regex.Replace(redirectPath, "--0", 1)
        let! bad = test.Send(get tampered)
        Assert.Equal(404, bad.Status)
        Assert.Empty bad.Body
        // A before-action's `head` ignores the request format.
        Assert.Equal(Some "text/html", bad.Header "content-type")
    }

[<Fact>]
let ``disk uploads require a session`` () =
    task {
        use! test = bootSeeded "default"
        let! reply = test.Send({ request "PUT" "/rails/active_storage/disk/anything" with Body = Some "x"B })
        Assert.Equal(401, reply.Status)
    }

[<Fact>]
let ``cable handshake with a rails session cookie`` () =
    task {
        use! test = bootSeeded "default"
        let sessions, _, _ = sessionVectors ()
        let connect (cookie: string option) : Task<string> =
            task {
                use socket = new ClientWebSocket()
                socket.Options.AddSubProtocol "actioncable-v1-json"
                socket.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{test.Host.Port}")
                match cookie with
                | Some cookie -> socket.Options.SetRequestHeader("Cookie", cookie)
                | None -> ()
                use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 10.0)
                do! socket.ConnectAsync(Uri $"ws://127.0.0.1:{test.Host.Port}/cable", timeout.Token)
                let buffer = Array.zeroCreate<byte> 4096
                let! received = socket.ReceiveAsync(ArraySegment buffer, timeout.Token)
                return Encoding.UTF8.GetString(buffer, 0, received.Count)
            }
        let! welcome = connect (Some sessions[0].CookieHeader)
        Assert.Equal("""{"type":"welcome"}""", welcome)
        let! unauthorized = connect None
        Assert.Equal("""{"type":"disconnect","reason":"unauthorized","reconnect":false}""", unauthorized)
    }

[<Fact>]
let ``backup snapshots the live database`` () =
    task {
        use! test = bootSeeded "default"
        let config = test.App.Config
        Boot.backup config (Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- StoragePaths.backupFile config.Storage
        builder.Pooling <- false
        use snapshot = new SqliteConnection(builder.ToString())
        snapshot.Open()
        use command = snapshot.CreateCommand()
        command.CommandText <- "SELECT COUNT(*) FROM users"
        let users = Convert.ToInt64(command.ExecuteScalar())
        Assert.True(users > 0L)
        let leftovers =
            Directory.GetFiles(config.Storage.Backups)
            |> Array.filter (fun file -> (nonNull (Path.GetFileName file)).StartsWith ".backup-")
        Assert.Empty leftovers
    }

[<Fact>]
let ``jobs run ad hoc work and purge unattached blobs`` () =
    task {
        let! test = bootSeeded "default"
        let app = test.App

        let finished = TaskCompletionSource()
        app.Jobs.PerformLater("Test", fun () -> task { finished.SetResult(); return Ok() })
        let! done' = Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds 5.0))
        Assert.True(obj.ReferenceEquals(done', finished.Task), "the ad hoc job ran")

        let now = app.Clock.Now()
        let! created =
            app.Db.Write(fun tx ->
                Storage.createAndUpload app.Storage tx.Conn.Raw "hello"B (Filename.create "hello.txt") None now |> Common.raiseStorage)
        let blob = unwrap created
        let path = Storage.pathFor app.Storage blob
        Assert.True(File.Exists path)

        // Blob 5 is attached to a message: purging it is refused (`InvalidForeignKey`).
        (app.Jobs :> EventSink).Emit(Event.PurgeBlob 5L)
        (app.Jobs :> EventSink).Emit(Event.PurgeBlob blob.Id)
        let isGone () =
            task {
                let! gone = app.Db.Read(fun conn -> (Common.raiseStorage (Campfire.Storage.Blob.find conn.Raw blob.Id)).IsNone)
                return unwrap gone && not (File.Exists path)
            }
        let mutable attempts = 0
        let mutable gone = false
        while not gone && attempts < 50 do
            let! g = isGone ()
            gone <- g
            if not gone then do! Task.Delay 20
            attempts <- attempts + 1
        Assert.False(File.Exists path, test.Logs)
        let! attached = app.Db.Read(fun conn -> (Common.raiseStorage (Campfire.Storage.Blob.find conn.Raw 5L)).IsSome)
        Assert.True(unwrap attached)

        do! (test :> IAsyncDisposable).DisposeAsync()
    }

/// Variants are transformed off the database writer: other writes go through while one is.
[<Fact>]
let ``writes proceed while a variant is transformed`` () =
    task {
        use! test = bootSeeded "default"
        let app = test.App
        let blob = unwrap (app.Db.ReadBlocking(fun conn -> (Common.raiseStorage (Campfire.Storage.Blob.find conn.Raw 5L)).Value))
        let variation = Storage.variationFor blob (Variation.resizeToLimit 37L 37L None) |> Result.defaultWith (fun e -> failwith (StorageError.message e))

        use entered = new ManualResetEventSlim(false)
        use released = new ManualResetEventSlim(false)
        let processing =
            ActiveStorage.processedVariantWith app blob variation (fun storage blob variation ->
                entered.Set()
                released.Wait()
                Storage.transformVariant storage blob variation)
        Assert.True(entered.Wait(TimeSpan.FromSeconds 10.0), "the transform started")

        let write = app.Db.Write(fun tx -> tx.Conn.Execute("UPDATE accounts SET name = name", [||]) |> ignore)
        let! first = Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds 5.0))
        Assert.True(obj.ReferenceEquals(first, write), "the write waited on the transform")
        unwrap write.Result

        released.Set()
        match! processing with
        | Error e -> failwith (Error.display e)
        | Ok image ->
            Assert.True(File.Exists(Storage.pathFor app.Storage image))
            let! recorded = app.Db.Read(fun conn -> Common.raiseStorage (Storage.existingVariant conn.Raw blob variation))
            Assert.Equal(Some image.Id, unwrap recorded |> Option.map (fun b -> b.Id))
    }

let private proxyPath () : string =
    let _, blobs, _ = sessionVectors ()
    let regex = Text.RegularExpressions.Regex("/redirect/")
    regex.Replace(blobs[0], "/proxy/", 1)

[<Fact>]
let ``blob byte ranges are served from the file`` () =
    task {
        use! test = bootSeeded "default"
        let proxyPath = proxyPath ()
        let blob = unwrap (test.App.Db.ReadBlocking(fun conn -> (Common.raiseStorage (Campfire.Storage.Blob.find conn.Raw 5L)).Value))
        let file = File.ReadAllBytes(Storage.pathFor test.App.Storage blob)
        let ranged (range: string) = (get proxyPath).With("range", range)

        let! single = test.Send(ranged "bytes=100-")
        Assert.Equal(206, single.Status)
        Assert.Equal(Some $"bytes 100-{file.Length - 1}/{file.Length}", single.Header "content-range")
        Assert.Equal(Some(string (file.Length - 100)), single.Header "content-length")
        Assert.Equal<byte[]>(file[100..], single.Body)

        let! multiple = test.Send(ranged "bytes=0-9,20-29")
        Assert.Equal(206, multiple.Status)
        let contentType = (multiple.Header "content-type").Value
        let boundary = contentType.Substring "multipart/byteranges; boundary=".Length
        let part (start: int) (stop: int) =
            Array.concat
                [ Encoding.UTF8.GetBytes $"\r\n--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Range: bytes {start}-{stop}/{file.Length}\r\n\r\n"
                  file[start..stop] ]
        let expected = Array.concat [ part 0 9; part 20 29; Encoding.UTF8.GetBytes $"\r\n--{boundary}--\r\n" ]
        Assert.Equal(Some(string expected.Length), multiple.Header "content-length")
        Assert.Equal<byte[]>(expected, multiple.Body)

        let! unsatisfiable = test.Send(ranged $"bytes={file.Length + 10}-")
        Assert.Equal(416, unsatisfiable.Status)
    }

/// `ContentDisposition.format` transliterates the ASCII `filename=` with I18n's whole table, not just Latin-1.
[<Fact>]
let ``proxied blobs name their file like rails`` () =
    task {
        use! test = bootSeeded "default"
        unwrap (test.App.Db.WriteBlocking(fun tx -> tx.Conn.Execute("UPDATE active_storage_blobs SET filename = 'Łódź.jpg' WHERE id = 5", [||]) |> ignore))
        let proxyPath = proxyPath ()
        let expected = Some "inline; filename=\"Lodz.jpg\"; filename*=UTF-8''%C5%81%C3%B3d%C5%BA.jpg"
        let! whole = test.Send(get proxyPath)
        Assert.True(((whole.Status, whole.Header "content-disposition") = (200, expected)))
        let! ranged = test.Send((get proxyPath).With("range", "bytes=0-9"))
        Assert.True(((ranged.Status, ranged.Header "content-disposition") = (206, expected)))
    }

/// `send_stream` puts `?disposition=` into Content-Disposition as it is, and Puma writes a value with line
/// breaks line by line, leaving out lines with control characters (probed in the reference, where Thruster
/// answers a DEL with a 502).
[<Fact>]
let ``proxied blobs write odd dispositions as puma does`` () =
    task {
        use! test = bootSeeded "default"
        let proxyPath = proxyPath ()
        let dispositions (disposition: string) =
            task {
                let! reply = test.Send(get $"{proxyPath}?disposition={disposition}")
                return reply.Status, reply.HeaderValues "content-disposition"
            }
        let! _, inline' = dispositions "inline"
        let filename = inline'[0].Substring "inline".Length
        let check (disposition: string) (expected: int * string list) =
            task {
                let! actual = dispositions disposition
                Assert.True((actual = expected), $"{disposition}: {actual}")
            }
        do! check "x%0Ay" (200, [ "x"; $"y{filename}" ])
        do! check "%0Aa" (200, [ ""; $"a{filename}" ])
        do! check "x%09y" (200, [ $"x\ty{filename}" ])
        do! check "%C3%A9" (200, [ $"é{filename}" ])
        for dropped in [ "x%0Dy"; "x%01y"; "x%00y"; "x%7Fy" ] do
            do! check dropped (200, [])
        let! status, _ = dispositions "%FF"
        Assert.Equal(400, status)
    }

/// Regression: every message create runs rich text (plain text for the search index, mentions)
/// inside the writer's transaction. It once checked out a pooled reader for that, so with as many
/// concurrent posts as readers, the writer waited on a reader while the readers' holders waited
/// on the writer, and the server stopped answering for good.
[<Fact>]
let ``concurrent message posts all complete`` () =
    task {
        use! test = bootSeeded "default"
        let sessions, _, _ = sessionVectors ()
        let session = sessions[0]
        let! roomId =
            test.App.Db.Read(fun conn ->
                conn.QueryOne(
                    """SELECT "memberships"."room_id" FROM "memberships" JOIN "users" ON "users"."id" = "memberships"."user_id"
                       JOIN "rooms" ON "rooms"."id" = "memberships"."room_id"
                       WHERE "users"."name" = ? AND "rooms"."type" = 'Rooms::Open' ORDER BY "rooms"."id" LIMIT 1""",
                    [| S session.UserName |],
                    fun r -> r.Int64 0
                ))
        let roomId = (unwrap roomId).Value

        let! page = test.Send(getWithCookie $"/rooms/{roomId}" session.CookieHeader)
        Assert.Equal(200, page.Status)

        let post (n: int) : Task<Reply> =
            let req =
                (request "POST" $"/rooms/{roomId}/messages")
                    .With("cookie", session.CookieHeader)
                    .With("sec-fetch-site", "same-origin")
                    .With("accept", "text/vnd.turbo-stream.html, text/html, application/xhtml+xml")
                    .Form($"message%%5Bbody%%5D=%%3Cp%%3EHello+{n}%%3C%%2Fp%%3E&message%%5Bclient_message_id%%5D=concurrent-{n}")
            test.Send req
        let posts = [| for n in 0..31 -> post n |]
        let all = Task.WhenAll posts
        let! finished = Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds 60.0))
        Assert.True(obj.ReferenceEquals(finished, all), "concurrent message posts deadlocked")
        for reply in all.Result do
            Assert.Equal(200, reply.Status)

        let afterReply = test.Send(getWithCookie $"/rooms/{roomId}" session.CookieHeader)
        let! finished = Task.WhenAny(afterReply, Task.Delay(TimeSpan.FromSeconds 10.0))
        Assert.True(obj.ReferenceEquals(finished, afterReply), "the server stopped answering")
        Assert.Equal(200, afterReply.Result.Status)
    }

/// A rollback-journal database with one row, at a path in a fresh directory.
let private scratchDatabase () : string * string =
    let dir = Path.Combine(Path.GetTempPath(), "campfire-backup-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let path = Path.Combine(dir, "source.sqlite3")
    let builder = SqliteConnectionStringBuilder()
    builder.DataSource <- path
    builder.Pooling <- false
    use connection = new SqliteConnection(builder.ToString())
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- "CREATE TABLE t (n INTEGER); INSERT INTO t VALUES (7)"
    command.ExecuteNonQuery() |> ignore
    dir, path

let private holdExclusive (path: string) : SqliteConnection =
    let builder = SqliteConnectionStringBuilder()
    builder.DataSource <- path
    builder.Pooling <- false
    let holder = new SqliteConnection(builder.ToString())
    holder.Open()
    use command = holder.CreateCommand()
    command.CommandText <- "BEGIN EXCLUSIVE"
    command.ExecuteNonQuery() |> ignore
    holder

[<Fact>]
let ``backup retries a busy source until it is free`` () =
    let dir, source = scratchDatabase ()
    try
        let target = Path.Combine(dir, "copy.sqlite3")
        let holder = holdExclusive source
        // The source's own busy handler is off, so only the step loop can wait the lock out.
        let release = Task.Run(fun () -> Threading.Thread.Sleep 400; holder.Dispose())
        Boot.copyDatabaseWith 0 50 (TimeSpan.FromMilliseconds 100.0) source target
        release.GetAwaiter().GetResult()
        let builder = SqliteConnectionStringBuilder()
        builder.DataSource <- target
        builder.Pooling <- false
        use copy = new SqliteConnection(builder.ToString())
        copy.Open()
        use command = copy.CreateCommand()
        command.CommandText <- "SELECT n FROM t"
        Assert.Equal(7L, Convert.ToInt64(command.ExecuteScalar()))
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``backup gives up on a source that stays busy`` () =
    let dir, source = scratchDatabase ()
    try
        use _holder = holdExclusive source
        let ex =
            Assert.Throws<SqliteException>(fun () ->
                Boot.copyDatabaseWith 0 3 (TimeSpan.FromMilliseconds 10.0) source (Path.Combine(dir, "copy.sqlite3")))
        Assert.Contains("backup did not finish", ex.Message)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``a failed backup prints the error and exits 1 instead of throwing`` () =
    let dir = Path.Combine(Path.GetTempPath(), "campfire-backup-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    try
        let config =
            match AppConfig.fromLookup (fun name -> match name with "SECRET_KEY_BASE" -> "abc" | "CAMPFIRE_STORAGE_PATH" -> dir | _ -> null) with
            | Ok config -> config
            | Error e -> failwith e
        let originalError = Console.Error
        use captured = new StringWriter()
        Console.SetError captured
        let code =
            try
                use loggers = Boot.createLoggerFactory config
                Boot.execute config loggers (Some "backup")
            finally
                Console.SetError originalError
        Assert.Equal(1, code)
        Assert.Contains("Error: ", captured.ToString())
        Assert.DoesNotContain("Unhandled", captured.ToString())
    finally
        Directory.Delete(dir, true)
