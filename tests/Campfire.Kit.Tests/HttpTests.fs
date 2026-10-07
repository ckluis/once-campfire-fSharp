// Port of rust/crates/kit/tests/http.rs
//
// End-to-end behavior of the HTTP layer through a real Kestrel and the kit's pipeline. The Rust tests
// call an axum router in process; these send HTTP/1.1 to a loopback port. The last test of http.rs
// goes through the front server (`front::serve_plain`); its counterpart here serves with Kestrel
// itself, and checks the peer address and a graceful stop.
module Campfire.Kit.Tests.HttpTests

open System
open System.IO
open System.Text
open System.Threading.Tasks
open Falco
open Xunit
open Campfire.RailsCompat
open Campfire.Kit
open Campfire.Kit.Tests.Helpers
open Campfire.Kit.Tests.Harness
open Campfire.Kit.Tests.HttpApp

let private startApp (config: KitConfig) : Task<TestApp> =
    let kit = kitFor config
    start kit (routes kit)

let private defaultApp () = startApp plainConfig

/// The test app as Campfire runs it in production: behind TLS (`assume_ssl`) with `force_ssl`.
let private sslApp () = startApp sslConfig

let private header (reply: Reply) (name: string) : string | null =
    match reply.Header name with
    | Some v -> v
    | None -> null

[<Fact>]
let ``renders html with rails headers`` () =
    task {
        use! app = defaultApp ()
        let! reply = app.Send(get "/rooms/5")
        Assert.Equal(200, reply.Status)
        Assert.Equal("<p>Campfire room 5</p>", reply.Text)
        Assert.Equal("text/html; charset=utf-8", header reply "content-type")
        Assert.Equal("SAMEORIGIN", header reply "x-frame-options")
        Assert.Equal("nosniff", header reply "x-content-type-options")
        Assert.Equal("strict-origin-when-cross-origin", header reply "referrer-policy")
        Assert.Equal("max-age=0, private, must-revalidate", header reply "cache-control")
        Assert.StartsWith("W/\"", header reply "etag")
        Assert.Equal(36, (nonNull (header reply "x-request-id")).Length)
        Assert.NotNull(header reply "x-runtime")
        Assert.Null(header reply "server")
        Assert.Empty reply.Cookies
    }

[<Fact>]
let ``conditional get on body etag`` () =
    task {
        use! app = defaultApp ()
        let! first = app.Send(get "/rooms/5")
        let etag = nonNull (header first "etag")
        let! second = app.Send((get "/rooms/5").With("if-none-match", etag))
        Assert.Equal(304, second.Status)
        Assert.Empty second.Body
        Assert.Null(header second "content-type")

        // Lists and `*` count, as they do for `fresh_when` (RFC 9110).
        for ifNoneMatch in [ $"\"other\", {etag}"; "*" ] do
            let! listed = app.Send((get "/rooms/5").With("if-none-match", ifNoneMatch))
            Assert.True((listed.Status = 304), ifNoneMatch)
        let! other = app.Send((get "/rooms/5").With("if-none-match", "\"other\""))
        Assert.Equal(200, other.Status)
    }

[<Fact>]
let ``head requests drop the body`` () =
    task {
        use! app = defaultApp ()
        let! reply = app.Send((get "/rooms/5").AsMethod "HEAD")
        Assert.Equal(200, reply.Status)
        Assert.Empty reply.Body
        Assert.Equal("22", header reply "content-length")
    }

[<Fact>]
let ``unknown routes and methods are rails 404s`` () =
    task {
        use! app = defaultApp ()
        let! reply = app.Send(get "/nope")
        Assert.Equal(404, reply.Status)
        Assert.Equal("<h1>Not found</h1>", reply.Text)
        let! reply = app.Send(request "PUT" "/rooms/1")
        Assert.Equal(404, reply.Status)
        let! reply = app.Send(get "/nope.json")
        Assert.Equal("""{"status":404,"error":"Not Found"}""", reply.Text)
    }

[<Fact>]
let ``params merge query over body and path over both`` () =
    task {
        use! app = defaultApp ()
        let! reply = app.Send(formPost "/echo/9?b=query&id=q" "a[b][]=1&a[b][]=2&b=body&id=body")
        let json = reply.Json
        Assert.Equal(Some(Value.Object [ "a", Value.Object [ "b", Value.Array [ Value.String "1"; Value.String "2" ] ]; "b", Value.String "query"; "id", Value.String "9" ]), json.TryGet "params")
        Assert.Equal(Some(Value.String "a[b][]=1&a[b][]=2&b=body&id=body"), json.TryGet "raw")
    }

[<Fact>]
let ``malformed params are 400`` () =
    task {
        use! app = defaultApp ()
        let! reply = app.Send(get "/echo/1?a=1&a[b]=2")
        Assert.Equal(400, reply.Status)
        let! reply = app.Send(formPost "/echo/1" "a=%")
        Assert.Equal(400, reply.Status)
        let! reply = app.Send((post "/echo/1").With("content-type", "application/json").WithBody "{nope")
        Assert.Equal(400, reply.Status)
    }

let private member' (name: string) (value: Value) : Value option = value.TryGet name

[<Fact>]
let ``method override from form param and header`` () =
    task {
        use! app = defaultApp ()
        let! reply = app.Send(formPost "/echo/1" "_method=patch&x=1")
        Assert.Equal(Some(Value.String "PATCH"), member' "method" reply.Json)
        Assert.Equal(Some(Value.String "POST"), member' "original_method" reply.Json)

        let! reply = app.Send(formPost "/echo/1" "_method=delete")
        Assert.Equal(Some(Value.String "DELETE"), member' "method" reply.Json)

        let! reply = app.Send((post "/echo/1").With("x-http-method-override", "patch").WithBody "")
        Assert.Equal(Some(Value.String "PATCH"), member' "method" reply.Json)

        // JSON bodies aren't forms: `_method` there doesn't count.
        let! reply =
            app.Send((post "/echo/1").With("content-type", "application/json").WithBody """{"_method":"patch","a":[1,null,2]}""")
        Assert.Equal(Some(Value.String "POST"), member' "method" reply.Json)
        Assert.Equal(Some(json "[1, 2]"), (member' "params" reply.Json) |> Option.bind (member' "a"))

        let! reply = app.Send(formPost "/echo/1" "_method=bogus")
        Assert.Equal(Some(Value.String "POST"), member' "method" reply.Json)
    }

[<Fact>]
let ``multipart uploads with method override`` () =
    task {
        use! app = defaultApp ()
        let boundary = "----campfire"
        // Unique to this run, so that finding it on disk afterwards means this request left it there.
        let content = "PNGDATA-" + Guid.NewGuid().ToString("N")
        let body =
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"_method\"\r\n\r\npatch\r\n\
              --{boundary}\r\nContent-Disposition: form-data; name=\"user[name]\"\r\n\r\nJo\r\n\
              --{boundary}\r\nContent-Disposition: form-data; name=\"user[avatar]\"; filename=\"me.png\"\r\nContent-Type: image/png\r\n\r\n{content}\r\n\
              --{boundary}--\r\n"
        let! reply = app.Send((post "/upload").With("content-type", $"multipart/form-data; boundary={boundary}").WithBody body)
        let expected = """{"method": "PATCH", "name": "Jo", "filename": "me.png", "content": "%CONTENT%"}""".Replace("%CONTENT%", content)
        Assert.Equal(json expected, reply.Json)
        // The spooled file is gone with the request.
        let spooled = Directory.GetFiles(Path.GetTempPath(), "RackMultipart*") |> Array.filter (fun f -> (try File.ReadAllText f = content with _ -> false))
        Assert.Empty spooled
    }

[<Fact>]
let ``missing required param is 400`` () =
    task {
        use! app = defaultApp ()
        let! reply = app.Send(formPost "/upload" "other=1")
        Assert.Equal(400, reply.Status)
    }

let private postFrom (site: string option) : Req =
    let request = post "/form"
    let request = match site with Some site -> request.With("sec-fetch-site", site) | None -> request
    request.Form "x=1"

[<Fact>]
let ``forgery protection trusts same site requests by sec fetch site`` () =
    task {
        use! app = sslApp ()
        for site in [ "same-origin"; "same-site" ] do
            let! ok = app.Send(postFrom (Some site))
            Assert.True((ok.Status = 200), site)
            Assert.Equal(Some(json """{"x": "1"}"""), (member' "params" ok.Json))
        for site in [ Some "cross-site"; Some "none"; Some "bogus"; None ] do
            let! forged = app.Send(postFrom site)
            Assert.True((forged.Status = 422), $"{site}")
            Assert.Equal("<h1>Unprocessable</h1>", forged.Text)
            Assert.True(forged.Cookies.IsEmpty, "errors don't commit cookies")
    }

[<Fact>]
let ``forgery protection allows a missing header only without ssl`` () =
    task {
        // Browsers send `Sec-Fetch-Site` only to secure origins, so plain HTTP can't require it.
        use! app = defaultApp ()
        let! reply = app.Send(postFrom None)
        Assert.Equal(200, reply.Status)
        let! reply = app.Send(postFrom (Some "cross-site"))
        Assert.Equal(422, reply.Status)
    }

[<Fact>]
let ``pages carry no forgery token or session`` () =
    task {
        use! app = defaultApp ()
        let! page = app.Send(get "/form")
        Assert.DoesNotContain("authenticity_token", page.Text)
        Assert.True(page.Cookies.IsEmpty, "rendering a form doesn't start a session")
    }

[<Fact>]
let ``forgery protection checks the origin`` () =
    task {
        use! app = sslApp ()
        let withOrigin (origin: string) = (post "/form").With("sec-fetch-site", "same-origin").With("origin", origin).WithBody ""
        let! reply = app.Send(withOrigin "https://chat.example.com")
        Assert.Equal(200, reply.Status)
        let! reply = app.Send(withOrigin "https://evil.example")
        Assert.Equal(422, reply.Status)
        let! reply = app.Send(withOrigin "null")
        Assert.Equal(422, reply.Status)
    }

[<Fact>]
let ``session cookie is written only when the session changes`` () =
    task {
        use! app = defaultApp ()
        let! untouched = app.Send(get "/noop")
        Assert.Empty untouched.Cookies

        let! set = app.Send(formPost "/session" "value=%2Frooms%2F1")
        Assert.Equal(200, set.Status)
        let cookie = set.CookieJar
        Assert.StartsWith("_campfire_session=", cookie)

        // Reading it, or not touching it, sends no cookie back: the one the browser has stays.
        let! read = app.Send((get "/session").With("cookie", cookie))
        Assert.Equal(Some(Value.String "/rooms/1"), member' "value" read.Json)
        Assert.True(read.Cookies.IsEmpty, $"{read.Cookies}")
        let id = (member' "id" read.Json).Value.AsString.Value
        Assert.Equal(32, id.Length)
        let! noop = app.Send((get "/noop").With("cookie", cookie))
        Assert.Empty noop.Cookies
        let! same = app.Send((formPost "/session" "value=%2Frooms%2F1").With("cookie", cookie))
        Assert.True(same.Cookies.IsEmpty, "writing the value it already holds changes nothing")
        let! again = app.Send((get "/session").With("cookie", cookie))
        Assert.Equal(Some(Value.String id), member' "id" again.Json)

        // Resetting leaves nothing to keep, so the cookie goes.
        let! reset = app.Send((request "DELETE" "/session").With("cookie", cookie))
        Assert.True((reset.Cookies |> List.exists (fun c -> c.StartsWith "_campfire_session=;")), $"{reset.Cookies}")
        let! after = app.Send((get "/session").With("cookie", reset.CookieJar))
        Assert.Equal(Some Value.Null, member' "value" after.Json)
        Assert.NotEqual(Some(Value.String id), member' "id" after.Json)

        // A tampered cookie reads as an empty session.
        let! bogus = app.Send((get "/session").With("cookie", "_campfire_session=garbage"))
        Assert.Equal(Some Value.Null, member' "value" bogus.Json)
    }

[<Fact>]
let ``flash survives exactly one redirect`` () =
    task {
        use! app = defaultApp ()
        let! redirect = app.Send(get "/notice")
        Assert.Equal(302, redirect.Status)
        Assert.Equal(Some "http://chat.example.com/flash", redirect.Header "location")
        let cookie = redirect.CookieJar

        let! shown = app.Send((get "/flash").With("cookie", cookie))
        Assert.Equal(Some(Value.String "✓"), member' "notice" shown.Json)
        // Shown, the flash is swept, and with it the only thing the session held.
        Assert.True((shown.Cookies |> List.exists (fun c -> c.StartsWith "_campfire_session=;")), $"{shown.Cookies}")
        let! gone = app.Send((get "/flash").With("cookie", shown.CookieJar))
        Assert.Equal(Some Value.Null, member' "notice" gone.Json)
    }

[<Fact>]
let ``signed permanent cookies and deletion`` () =
    task {
        use! app = defaultApp ()
        let! signedIn = app.Send(get "/sign_in")
        let cookies = signedIn.Cookies
        Assert.True(
            cookies
            |> List.exists (fun c ->
                c.StartsWith "session_token="
                && c.EndsWith "; path=/; expires=Wed, 01 Jun 2044 12:00:00 GMT; httponly; samesite=lax")
        )
        Assert.Contains("last_room=7; path=/; expires=Wed, 01 Jun 2044 12:00:00 GMT; samesite=lax", cookies)

        // Rails' Live responses send the action's cookies twice; this sends them once.
        let! live = app.Send(get "/live_sign_in")
        Assert.True((live.Cookies.Length = 2), $"{live.Cookies}")

        let jar = signedIn.CookieJar
        let! me = app.Send((get "/whoami").With("cookie", jar))
        Assert.Equal(json """{"token": "tok123", "last_room": "7"}""", me.Json)

        let! forged = app.Send((get "/whoami").With("cookie", "session_token=tok123"))
        Assert.Equal(Some Value.Null, member' "token" forged.Json)

        let! out = app.Send((get "/sign_out").With("cookie", jar))
        Assert.Equal<string list>(
            [ "session_token=; path=/; max-age=0; expires=Thu, 01 Jan 1970 00:00:00 GMT; samesite=lax" ],
            out.Cookies
        )
        let! nothing = app.Send(get "/sign_out")
        Assert.Empty nothing.Cookies
    }

[<Fact>]
let ``before actions halt the chain`` () =
    task {
        use! app = defaultApp ()
        let! anonymous = app.Send(get "/admin")
        Assert.Equal(302, anonymous.Status)
        Assert.Equal(Some "http://chat.example.com/session/new", anonymous.Header "location")
        Assert.Equal(Some "42", anonymous.Header "x-version")

        let! member' = app.Send((get "/admin").With("x-user", "jo"))
        Assert.Equal(403, member'.Status)
        Assert.Equal(Some "text/html", member'.Header "content-type")
        Assert.Empty member'.Body

        let! admin = app.Send((get "/admin").With("x-user", "admin"))
        Assert.Equal("hi admin", admin.Text)
    }

[<Fact>]
let ``respond to negotiates like rails`` () =
    task {
        use! app = defaultApp ()
        let browser = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
        let! html = app.Send((get "/messages").With("accept", browser))
        Assert.Equal(Some "text/html; charset=utf-8", html.Header "content-type")

        let turbo = "text/vnd.turbo-stream.html, text/html, application/xhtml+xml"
        let! stream = app.Send((post "/messages").With("accept", turbo).WithBody "")
        Assert.Equal(Some "text/vnd.turbo-stream.html; charset=utf-8", stream.Header "content-type")

        let! json = app.Send(get "/messages.json")
        Assert.Equal(Some "application/json; charset=utf-8", json.Header "content-type")
        let! json = app.Send(get "/messages?format=json")
        Assert.Equal(Some "application/json; charset=utf-8", json.Header "content-type")

        let! unacceptable = app.Send((get "/messages").With("accept", "image/png"))
        Assert.Equal(406, unacceptable.Status)
        let! invalid = app.Send((get "/messages").With("accept", "garbage"))
        Assert.Equal(406, invalid.Status)

        let! any = app.Send((get "/autocomplete").With("accept", "*/*"))
        Assert.Equal(Some "text/html; charset=utf-8", any.Header "content-type")
    }

[<Fact>]
let ``redirects like rails`` () =
    task {
        use! app = defaultApp ()
        let! plain = app.Send(get "/redirect")
        Assert.Equal(302, plain.Status)
        Assert.Equal(Some "http://chat.example.com/rooms/1?x=1", plain.Header "location")
        Assert.Equal(Some "text/html; charset=utf-8", plain.Header "content-type")
        Assert.Equal(Some "no-cache", plain.Header "cache-control")

        let! seeOther = app.Send(get "/redirect?to=see_other")
        Assert.Equal(303, seeOther.Status)

        let! relative = app.Send(get "/redirect?to=relative")
        Assert.Equal(500, relative.Status)
        let! other = app.Send(get "/redirect?to=other")
        Assert.Equal(500, other.Status)
        let! allowed = app.Send(get "/redirect?to=other_allowed")
        Assert.Equal(Some "https://docs.example/", allowed.Header "location")

        let! back = app.Send((get "/redirect?to=back").With("referer", "http://chat.example.com/rooms/3"))
        Assert.Equal(Some "http://chat.example.com/rooms/3", back.Header "location")
        let! foreign = app.Send((get "/redirect?to=back").With("referer", "https://evil.example/x"))
        Assert.Equal(Some "http://chat.example.com/fallback", foreign.Header "location")
    }

[<Fact>]
let ``head with location`` () =
    task {
        use! app = defaultApp ()
        let! reply = app.Send(get "/created")
        Assert.Equal(201, reply.Status)
        Assert.Equal(Some "http://chat.example.com/rooms/1/messages/2", reply.Header "location")
        Assert.Equal(Some "text/html", reply.Header "content-type")
    }

[<Fact>]
let ``send file with disposition and the whole file`` () =
    task {
        let dir = Directory.CreateTempSubdirectory("kit-send-file")
        try
            let path = Path.Combine(dir.FullName, "logo.png")
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes "0123456789")
            let encoded = CookieJar.escape path
            use! app = defaultApp ()

            let! whole = app.Send(get $"/file?path={encoded}")
            Assert.Equal("0123456789", whole.Text)
            Assert.Equal(Some "image/png", whole.Header "content-type")
            Assert.Equal(Some "inline; filename=\"logo.png\"; filename*=UTF-8''logo.png", whole.Header "content-disposition")
            Assert.Equal(Some "binary", whole.Header "content-transfer-encoding")
            Assert.Equal(Some "10", whole.Header "content-length")

            // Rails' `send_file` ignores Range.
            let! ignored = app.Send((get $"/file?path={encoded}").With("range", "bytes=2-4"))
            Assert.Equal(200, ignored.Status)
            Assert.Equal("0123456789", ignored.Text)

            let! missing = app.Send(get "/file?path=%2Fnope")
            Assert.Equal(500, missing.Status)
        finally
            dir.Delete true
    }

[<Fact>]
let ``stale and expires in`` () =
    task {
        use! app = defaultApp ()
        let! first = app.Send(get "/logo")
        Assert.Equal(200, first.Status)
        Assert.Equal(Some "max-age=300, public, stale-while-revalidate=604800", first.Header "cache-control")
        let etag = (first.Header "etag").Value
        Assert.StartsWith("W/\"", etag)

        let! second = app.Send((get "/logo").With("if-none-match", etag))
        Assert.Equal(304, second.Status)

        let! fresh = app.Send(get "/fresh")
        Assert.Equal(Some "max-age=0, private, must-revalidate", fresh.Header "cache-control")
        let etag = (fresh.Header "etag").Value
        let! cached = app.Send((get "/fresh").With("if-none-match", etag))
        Assert.Equal(304, cached.Status)
        let! listed = app.Send((get "/fresh").With("if-none-match", $"\"other\", {etag}"))
        Assert.Equal(304, listed.Status)

        // Turbo Frame requests get a different ETag (turbo-rails' frame etagger).
        let! frame = app.Send((get "/fresh").With("turbo-frame", "x"))
        Assert.NotEqual(Some etag, frame.Header "etag")
    }

[<Fact>]
let ``remote ip from peer and proxies`` () =
    task {
        use! app = defaultApp ()
        let! reply = app.Send((get "/echo/1").With("x-forwarded-for", "203.0.113.9, 10.0.0.1").With("x-test-peer", "127.0.0.1"))
        Assert.Equal(Some(Value.String "203.0.113.9"), member' "remote_ip" reply.Json)

        let! reply = app.Send((get "/echo/1").With("x-test-peer", "198.51.100.4"))
        Assert.Equal(Some(Value.String "198.51.100.4"), member' "remote_ip" reply.Json)

        let! reply = app.Send((get "/echo/1").With("client-ip", "1.1.1.1").With("x-forwarded-for", "2.2.2.2"))
        Assert.Equal(500, reply.Status)
    }

[<Fact>]
let ``force ssl redirects and hardens`` () =
    task {
        use! app = startApp { KitConfig.Default with ForceSsl = true }
        let! plain = app.Send(get "/rooms/1?x=1")
        Assert.Equal(301, plain.Status)
        Assert.Equal(Some "https://chat.example.com/rooms/1?x=1", plain.Header "location")
        let! plainPost = app.Send(formPost "/echo/1" "")
        Assert.Equal(308, plainPost.Status)

        use! production = startApp (KitConfig.Production false)
        let! secure = production.Send(get "/sign_in")
        Assert.Equal(200, secure.Status)
        Assert.Equal(Some "max-age=63072000; includeSubDomains", secure.Header "strict-transport-security")
        Assert.True(secure.Cookies |> List.forall (fun c -> c.EndsWith "; secure"))

        let! https = production.Send(get "/redirect")
        Assert.Equal(Some "https://chat.example.com/rooms/1?x=1", https.Header "location")
    }

[<Fact>]
let ``body limit is 413`` () =
    task {
        use! app = startApp { KitConfig.Default with MaxBodyBytes = ValueSome 16 }
        let! reply = app.Send(formPost "/echo/1" (String.replicate 20 "a=1&"))
        Assert.Equal(413, reply.Status)
        let! reply = app.Send((request "PATCH" "/echo/1").WithBody(String('x', 40)))
        Assert.Equal(413, reply.Status)
    }

[<Fact>]
let ``request ids are sanitized passthroughs`` () =
    task {
        use! app = defaultApp ()
        let! reply = app.Send((get "/rooms/1").With("x-request-id", "abc-123<script>"))
        Assert.Equal(Some "abc-123script", reply.Header "x-request-id")
    }

[<Fact>]
let ``serves with peer addresses and shuts down gracefully`` () =
    task {
        let app = defaultApp ()
        let! running = app
        let! reply = running.Send(get "/echo/1")
        Assert.Equal(200, reply.Status)
        Assert.Equal(Some(Value.String "127.0.0.1"), member' "remote_ip" reply.Json)
        let stopping = (running :> IAsyncDisposable).DisposeAsync().AsTask()
        let! finished = Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds 5.0))
        Assert.True(obj.ReferenceEquals(finished, stopping), "the server stopped")
    }
