// Port of rust/crates/campfire/src/controllers/presenters/accounts/tests.rs
//
// Request-level tests for the session, account and user controllers (controllers A), through the
// whole stack (`Boot.boot`, the Rails route table, kit) over a private copy of the reference-built
// `default` parity seed. Skipped (with a note) when the seed hasn't been built
// (`parity/bin/seed build default`). Parity against the running reference lives in
// `reference-tools/campfire/controllers_a/replay.py`.
module Campfire.App.Tests.PresenterAccountsTests

open System
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Campfire.App
open Campfire.App.Tests.Support
open Campfire.Db
open Campfire.Kit
open Campfire.Tests

let private host = "campfire.test"
let private password = "secret123456"

/// Asserts the page has a form posting to `action`.
let private assertForm (reply: Reply) (action: string) : unit =
    let html = reply.Text
    Assert.True(
        (html.Contains $"action=\"{action}\"" || html.Contains $"action=\"http://{host}{action}\""),
        $"no form for {action} in {html}"
    )

/// Asserts the page has a `button_to` form for `action` that sends `meth`.
let private assertButton (reply: Reply) (action: string) (meth: string) : unit =
    let html = reply.Text
    let methodField = $"name=\"_method\" value=\"{meth}\""
    let found =
        html.Split("<form")
        |> Array.skip 1
        |> Array.exists (fun form ->
            let form = match form.IndexOf "</form>" with -1 -> form | at -> form.Substring(0, at)
            form.Contains $"action=\"{action}\"" && form.Contains methodField)
    Assert.True(found, $"no {meth} button for {action}")

let private assertRedirect (reply: Reply) (location: string) : unit =
    Assert.True(((reply.Status, reply.Location) = (302, Some location)), $"{reply.Status} {reply.Location}: {reply.Text}")

let private jsonString (value: JsonElement) (name: string) : string = str (value.GetProperty name)

// --- Sessions ------------------------------------------------------------------------------------

[<Fact>]
let ``signs in with a password and out again`` () =
    task {
        use! test = bootSeeded "default"
        let b = browser test "198.51.100.1"

        // Unauthenticated requests remember where they were going.
        let! first = b.Get "/account/edit"
        assertRedirect first "http://campfire.test/session/new"

        let! page = b.Get "/session/new"
        Assert.Equal(200, page.Status)
        Assert.Contains("<title>Sign in</title>", page.Text)
        Assert.True((page.Header "link" |> Option.exists (fun link -> link.Contains "rel=preload; as=style")))
        assertForm page "/session"
        let! signedIn = b.Form("post", "/session", [ "email_address", test.Label "emails.david"; "password", password ])
        assertRedirect signedIn "http://campfire.test/account/edit"
        let sessionCookie = signedIn.SetCookies |> List.find (fun c -> c.StartsWith "session_token=")
        Assert.True(
            (sessionCookie.Contains "httponly" && sessionCookie.Contains "samesite=lax" && sessionCookie.Contains "expires="),
            sessionCookie
        )

        // Signed in: sign-in and join pages send you home.
        let! join = b.Get("/join/" + test.Label "join_codes.signal")
        assertRedirect join "http://campfire.test/"
        let! root = b.Get "/"
        Assert.Equal(302, root.Status)
        Assert.True((root.Location |> Option.exists (fun l -> l.StartsWith "http://campfire.test/rooms/")))

        // Sign out from the profile page's form.
        let! profile = b.Get "/users/me/profile"
        Assert.True((profile.Status = 200), profile.Text)
        assertForm profile "/session"
        let! signedOut = b.Form("delete", "/session", [])
        assertRedirect signedOut "http://campfire.test/"
        Assert.True((signedOut.SetCookies |> List.exists (fun c -> c.StartsWith "session_token=;")), $"{signedOut.SetCookies}")
        let! after = b.Get "/users/me/profile"
        assertRedirect after "http://campfire.test/session/new"
    }

[<Fact>]
let ``rejects bad passwords and rate limits sign ins`` () =
    task {
        use! test = bootSeeded "default"
        let b = browser test "198.51.100.2"
        let! form = b.Get "/session/new"
        assertForm form "/session"
        for attempt in 1..11 do
            let! reply = b.Form("post", "/session", [ "email_address", "david@37signals.com"; "password", "wrong" ])
            let expected = if attempt <= 10 then 401 else 429
            Assert.True((reply.Status = expected), $"attempt {attempt}")
            let html = reply.Text
            Assert.True((html.Contains "Too many requests or unauthorized." && html.Contains "shake"), html)
            Assert.Contains("value=\"david@37signals.com\"", html)
        // Deactivated users can't sign in.
        let other = browser test "198.51.100.3"
        let! page = other.Get "/session/new"
        assertForm page "/session"
        let! reply = other.Form("post", "/session", [ "email_address", test.Label "emails.rita"; "password", password ])
        Assert.Equal(401, reply.Status)
    }

[<Fact>]
let ``a rails issued session cookie continues on rust`` () =
    task {
        use! test = bootSeeded "default"
        let sessions, _, _ = sessionVectors ()
        let b = browser test "198.51.100.4"
        b.AbsorbCookieHeader sessions[0].CookieHeader
        let! profile = b.Get "/users/me/profile"
        Assert.Equal(200, profile.Status)
        Assert.Contains("David", profile.Text)
    }

[<Fact>]
let ``direct uploads are refused past the body limit`` () =
    task {
        use! test = bootSeeded "default"
        let b = browser test "198.51.100.10"
        do! b.SignIn "david@37signals.com"
        let create (byteSize: int) =
            $"""{{"blob":{{"filename":"a.bin","byte_size":{byteSize},"checksum":"1B2M2Y8AsgTpgAmY7PhCfg==","content_type":"application/octet-stream"}}}}"""
        let post (body: string) =
            (request "POST" "/rails/active_storage/direct_uploads").With("content-type", "application/json").WithBody body
        let! small = b.Write(post (create 5))
        Assert.True((small.Status = 200), small.Text)
        Assert.True(small.Text.Contains "/rails/active_storage/disk/", small.Text)
        let! large = b.Write(post (create (RequestBody.MaxBufferedBody + 1)))
        Assert.Equal(413, large.Status)
    }

[<Fact>]
let ``edge gets its install instructions`` () =
    task {
        // EdgeHTML's token: the useragent gem reports Chromium Edge (`Edg/`) as Chrome.
        let edge =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Edge/124.0.0.0"
        use! test = bootSeeded "default"
        let b = browser test "198.51.100.9"
        do! b.SignIn "david@37signals.com"
        let! profile = b.Send((get "/users/me/profile").With("user-agent", edge))
        Assert.Equal(200, profile.Status)
        Assert.True(profile.Text.Contains "/assets/install-edge-", profile.Text)
    }

[<Fact>]
let ``transfers sign in on another device`` () =
    task {
        use! test = bootSeeded "default"
        let admin = browser test "198.51.100.5"
        do! admin.SignIn(test.Label "emails.david")
        let kevinId = test.Label "users.kevin"
        let! page = admin.Get $"/users/{kevinId}"
        Assert.True((page.Status = 200), page.Text)
        let html = page.Text
        let marker = "/session/transfers/"
        let at = html.IndexOf marker + marker.Length
        let transferId = html.Substring(at).Split('"')[0]

        let phone = browser test "198.51.100.6"
        let path = $"/session/transfers/{transferId}"
        let! show = phone.Get path
        Assert.Equal(200, show.Status)
        assertForm show path
        let! updated = phone.Form("put", path, [])
        assertRedirect updated "http://campfire.test/"
        let! profile = phone.Get "/users/me/profile"
        Assert.Equal(200, profile.Status)

        let stranger = browser test "198.51.100.7"
        let bogus = "/session/transfers/bogus"
        let! page = stranger.Get bogus
        assertForm page bogus
        let! rejected = stranger.Form("put", bogus, [])
        Assert.Equal(400, rejected.Status)
    }

// --- Joining and first run -------------------------------------------------------------------------

[<Fact>]
let ``joins with the join code`` () =
    task {
        use! test = bootSeeded "default"
        let b = browser test "198.51.100.8"
        let! nope = b.Get "/join/nope"
        Assert.Equal(404, nope.Status)
        let path = "/join/" + test.Label "join_codes.signal"
        let! page = b.Get path
        Assert.Equal(200, page.Status)
        assertForm page path
        let fields = [ "user[name]", "New Person"; "user[email_address]", "new@example.com"; "user[password]", password ]
        let! joined = b.Form("post", path, fields)
        assertRedirect joined "http://campfire.test/"
        let! profile = b.Get "/users/me/profile"
        Assert.Equal(200, profile.Status)

        // A taken email address goes to sign in instead.
        let other = browser test "198.51.100.9"
        let! page = other.Get path
        assertForm page path
        let fields = [ "user[name]", "Imposter"; "user[email_address]", "new@example.com"; "user[password]", password ]
        let! taken = other.Form("post", path, fields)
        assertRedirect taken "http://campfire.test/session/new?email_address=new%40example.com"
    }

[<Fact>]
let ``first run sets up the account`` () =
    task {
        use! test = bootSeeded "first_run"
        let b = browser test "198.51.100.10"
        let! signIn = b.Get "/session/new"
        assertRedirect signIn "http://campfire.test/first_run"
        let! page = b.Get "/first_run"
        Assert.True((page.Status = 200), page.Text)
        assertForm page "/first_run"
        let fields = [ "user[name]", "Owner"; "user[email_address]", "owner@example.com"; "user[password]", password ]
        let! created = b.Form("post", "/first_run", fields)
        assertRedirect created "http://campfire.test/"
        let! again = b.Get "/first_run"
        assertRedirect again "http://campfire.test/"
        let! root = b.Get "/"
        Assert.True((root.Location |> Option.exists (fun l -> l.StartsWith "http://campfire.test/rooms/")))
    }

// --- Account ---------------------------------------------------------------------------------------

[<Fact>]
let ``administers the account`` () =
    task {
        use! test = bootSeeded "default"
        let admin = browser test "198.51.100.11"
        do! admin.SignIn(test.Label "emails.david")
        let accountId = test.Label "accounts.signal"

        let! edit = admin.Get "/account/edit"
        Assert.True((edit.Status = 200), edit.Text)
        let action = $"/account.{accountId}"
        assertForm edit action
        let! updated = admin.Form("patch", action, [ "account[name]", "Renamed" ])
        assertRedirect updated "http://campfire.test/account/edit"
        let! edit = admin.Get "/account/edit"
        Assert.Contains("Renamed", edit.Text)
        Assert.True(edit.Text.Contains "flash", "the ✓ notice shows once")

        // Join code reset.
        assertForm edit "/account/join_code"
        let! reset = admin.Form("post", "/account/join_code", [])
        assertRedirect reset "http://campfire.test/account/edit"
        let! after = admin.Get "/account/edit"
        Assert.DoesNotContain(test.Label "join_codes.signal", after.Text)

        // Custom styles.
        let! page = admin.Get "/account/custom_styles/edit"
        Assert.Equal(200, page.Status)
        assertForm page "/account/custom_styles"
        let! reply = admin.Form("patch", "/account/custom_styles", [ "account[custom_styles]", "body { --x: 1 }" ])
        assertRedirect reply "http://campfire.test/account/custom_styles/edit"
        let! styles = admin.Get "/account/custom_styles/edit"
        Assert.Contains("<style data-turbo-track=\"reload\">body { --x: 1 }</style>", styles.Text)

        // The next page of people, as a turbo stream.
        let! page = admin.Send((get "/account/users?page=2").With("accept", "text/vnd.turbo-stream.html"))
        Assert.Equal(200, page.Status)
        Assert.True((page.Header "content-type" |> Option.exists (fun t -> t.StartsWith "text/vnd.turbo-stream.html")))
        let! plain = admin.Get "/account/users"
        Assert.Equal(406, plain.Status)

        // Members can see the account but not change it.
        let member' = browser test "198.51.100.12"
        do! member'.SignIn(test.Label "emails.kevin")
        let! edit = member'.Get "/account/edit"
        Assert.Equal(200, edit.Status)
        Assert.False(edit.Text.Contains $"action=\"{action}\"", "members get no account form")
        let! forbidden = member'.Form("patch", action, [ "account[name]", "Mine" ])
        Assert.Equal(403, forbidden.Status)
        let! bots = member'.Get "/account/bots"
        Assert.Equal(403, bots.Status)
    }

[<Fact>]
let ``manages bots`` () =
    task {
        use! test = bootSeeded "default"
        let admin = browser test "198.51.100.13"
        do! admin.SignIn(test.Label "emails.david")
        let! index = admin.Get "/account/bots"
        Assert.Equal(200, index.Status)
        Assert.Contains(test.Label "bot_keys.bender", index.Text)

        let! ``new`` = admin.Get "/account/bots/new"
        assertForm ``new`` "/account/bots"
        let! reply = admin.Form("post", "/account/bots", [ "user[name]", "Robo"; "user[webhook_url]", "https://example.com/robo" ])
        assertRedirect reply "http://campfire.test/account/bots"
        let! index = admin.Get "/account/bots"
        Assert.Contains("Robo", index.Text)

        let bender = test.Label "users.bender"
        let! edit = admin.Get $"/account/bots/{bender}/edit"
        Assert.Equal(200, edit.Status)
        let action = $"/account/bots/{bender}"
        assertForm edit action
        let! renamed = admin.Form("patch", action, [ "user[name]", "Bender 2" ])
        assertRedirect renamed "http://campfire.test/account/bots"

        let! edit = admin.Get $"/account/bots/{bender}/edit"
        let keyAction = $"/account/bots/{bender}/key"
        assertButton edit keyAction "put"
        let! reset = admin.Form("put", keyAction, [])
        assertRedirect reset "http://campfire.test/account/bots"
        let! index = admin.Get "/account/bots"
        Assert.DoesNotContain(test.Label "bot_keys.bender", index.Text)

        let! edit = admin.Get $"/account/bots/{bender}/edit"
        assertButton edit action "delete"
        let! deleted = admin.Form("delete", action, [])
        assertRedirect deleted "http://campfire.test/account/bots"
        let! gone = admin.Get $"/account/bots/{bender}/edit"
        Assert.Equal(404, gone.Status)
    }

[<Fact>]
let ``serves the account logo and avatars`` () =
    task {
        use! test = bootSeeded "default"
        let b = browser test "198.51.100.14"
        let! logo = b.Get "/account/logo?size=small"
        Assert.Equal(200, logo.Status)
        Assert.Equal(Some "image/png", logo.Header "content-type")
        Assert.Equal(Some "max-age=300, public, stale-while-revalidate=604800", logo.Header "cache-control")
        let etag = (logo.Header "etag").Value
        let! again = b.Send((get "/account/logo?size=small").With("if-none-match", etag))
        Assert.Equal(304, again.Status)

        // Avatars need a session.
        let davidToken = test.Label "avatar_tokens.david"
        let! anonymous = b.Get $"/users/{davidToken}/avatar"
        Assert.Equal(302, anonymous.Status)
        do! b.SignIn(test.Label "emails.kevin")
        let! avatar = b.Get $"/users/{davidToken}/avatar"
        Assert.Equal(200, avatar.Status)
        Assert.Equal(Some "image/svg+xml; charset=utf-8", avatar.Header "content-type")
        Assert.True(avatar.Text.Contains "\n      D\n    </text>", avatar.Text)
        Assert.Equal(Some "max-age=1800, public, stale-while-revalidate=604800", avatar.Header "cache-control")
        let! jason = b.Get("/users/" + test.Label "avatar_tokens.jason" + "/avatar")
        Assert.Equal((200, Some "image/webp"), (jason.Status, jason.Header "content-type"))
        let! bad = b.Get "/users/bogus/avatar"
        Assert.Equal((404, 0), (bad.Status, bad.Body.Length))
    }

// --- Users -----------------------------------------------------------------------------------------

[<Fact>]
let ``profile sidebar and user pages`` () =
    task {
        use! test = bootSeeded "default"
        let b = browser test "198.51.100.15"
        do! b.SignIn(test.Label "emails.kevin")

        let! profile = b.Get "/users/me/profile"
        Assert.Equal(200, profile.Status)
        assertForm profile "/users/me/profile"
        let! reply = b.Form("patch", "/users/me/profile", [ "user[name]", "Kev"; "user[bio]", "Hi" ])
        assertRedirect reply "http://campfire.test/users/me/profile"
        let! profile = b.Get "/users/me/profile"
        Assert.Contains("Kev", profile.Text)

        let! sidebar = b.Get "/users/me/sidebar"
        Assert.Equal(200, sidebar.Status)
        Assert.Contains("<!DOCTYPE html>", sidebar.Text)
        let! frame = b.Send((get "/users/me/sidebar").With("turbo-frame", "user_sidebar"))
        Assert.Equal(200, frame.Status)
        Assert.True((not (frame.Text.Contains "<!DOCTYPE html>") && frame.Text.Contains "<turbo-frame"))

        let! david = b.Get("/users/" + test.Label "users.david")
        Assert.Equal(200, david.Status)
        let! missing = b.Get "/users/999999999"
        Assert.Equal(404, missing.Status)

        let! subscriptions = b.Get "/users/me/push_subscriptions"
        Assert.Equal(200, subscriptions.Status)
        let body = """{"push_subscription":{"endpoint":"http://example.com/push","p256dh_key":"a","auth_key":"b"}}"""
        let! reply = b.Write(((request "POST" "/users/me/push_subscriptions").With("content-type", "application/json")).WithBody body)
        Assert.True((reply.Status = 422), "an http endpoint fails validation")
    }

[<Fact>]
let ``bans and unbans`` () =
    task {
        use! test = bootSeeded "default"
        let admin = browser test "198.51.100.16"
        do! admin.SignIn(test.Label "emails.david")
        let jz = test.Label "users.jz"
        let! page = admin.Get $"/users/{jz}"
        let action = $"/users/{jz}/ban"
        assertForm page action
        let! banned = admin.Form("post", action, [])
        assertRedirect banned $"http://campfire.test/users/{jz}"
        let! page = admin.Get $"/users/{jz}"
        assertButton page action "delete"
        let! unbanned = admin.Form("delete", action, [])
        assertRedirect unbanned $"http://campfire.test/users/{jz}"
    }

/// `bans.create!` refuses a private address, which Rails answers with a 422 (`RecordInvalid`), and
/// nothing of the ban is kept.
[<Fact>]
let ``a ban from a private address is unprocessable`` () =
    task {
        use! test = bootSeeded "default"
        let jz = int64 (test.Label "users.jz")
        let db = test.App.Db
        let! started = db.Write(fun tx -> Session.start tx jz None (Some "192.168.1.20") |> ignore)
        started |> Result.defaultWith (fun e -> failwith (DbError.display e))

        let admin = browser test "198.51.100.18"
        do! admin.SignIn(test.Label "emails.david")
        let! reply = admin.Form("post", $"/users/{jz}/ban", [])
        Assert.Equal(422, reply.Status)
        Assert.True(reply.Text.Contains "That didn’t work (422)", reply.Text)

        let! user = db.Read(fun conn -> User.find conn jz)
        Assert.Equal(Status.Active, (user |> Result.defaultWith (fun e -> failwith (DbError.display e))).Status)
        let! bans = db.Read(fun conn -> conn.Count("SELECT COUNT(*) FROM bans WHERE user_id = ?", [| I jz |]))
        Assert.Equal(0L, bans |> Result.defaultWith (fun e -> failwith (DbError.display e)))
    }

[<Fact>]
let ``autocompletes users`` () =
    task {
        use! test = bootSeeded "default"
        let b = browser test "198.51.100.17"
        do! b.SignIn(test.Label "emails.david")
        let! html = b.Get "/autocompletable/users?filter=a"
        Assert.Equal(200, html.Status)
        Assert.True((html.Text.Contains "<lexxy-prompt-item" && not (html.Text.Contains "<!DOCTYPE")))
        let! json = b.Get "/autocompletable/users.json?query=a"
        Assert.Equal(Some "application/json; charset=utf-8", json.Header "content-type")
        Assert.True((json.Header "x-total-count").IsSome)
        use users = json.Json
        for user in users.RootElement.EnumerateArray() do
            Assert.StartsWith("http://campfire.test/users/", jsonString user "avatar_url")
        let! missing = b.Get "/autocompletable/users?room_id=999999"
        Assert.Equal(404, missing.Status)
    }

[<Fact>]
let ``qr codes and the pwa`` () =
    task {
        use! test = bootSeeded "default"
        let b = browser test "198.51.100.18"
        let! qr = b.Get "/qr_code/aHR0cDovL2NhbXBmaXJlLnRlc3Q"
        Assert.Equal((200, Some "image/svg+xml; charset=utf-8"), (qr.Status, qr.Header "content-type"))
        Assert.Equal(Some "max-age=31556952, public", qr.Header "cache-control")
        Assert.StartsWith("<?xml version=\"1.0\" standalone=\"yes\"?><svg", qr.Text)

        let! manifest = b.Get "/webmanifest.json"
        Assert.Equal((200, Some "application/json; charset=utf-8"), (manifest.Status, manifest.Header "content-type"))
        let! worker = b.Get "/service-worker.js"
        Assert.Equal((200, Some "text/javascript; charset=utf-8"), (worker.Status, worker.Header "content-type"))
    }
