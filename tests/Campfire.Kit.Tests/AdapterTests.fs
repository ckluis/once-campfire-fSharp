// What the adapter does that rust/crates/kit/tests/http.rs reaches only through axum: writing every kind
// of body (buffer, pooled buffer, file, stream), `Rack::Deflater` around them, the responses that
// bypass the kit's headers, and how a request reaches an action over the wire.
module Campfire.Kit.Tests.AdapterTests

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Threading.Tasks
open Falco
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Xunit
open Campfire.RailsCompat
open Campfire.Kit
open Campfire.Kit.Tests.Helpers
open Campfire.Kit.Tests.Harness

let private page = String.replicate 200 "<div class=\"message\">Hello there</div>\n"

let private gunzip (bytes: byte[]) : string =
    use input = new MemoryStream(bytes)
    use gzip = new GZipStream(input, CompressionMode.Decompress)
    use output = new MemoryStream()
    gzip.CopyTo output
    Encoding.UTF8.GetString(output.ToArray())

let private tempFile (content: string) : string =
    let path = Path.Combine(Path.GetTempPath(), "kit-adapter-" + Guid.NewGuid().ToString("N"))
    File.WriteAllText(path, content)
    path

let private text (c: Ctx) = act { return c.Html page }

let private pooled (c: Ctx) =
    act {
        return c.JsonWith(Status.Ok, (fun w ->
            w.WriteStartObject()
            w.WriteString("page", page)
            w.WriteEndObject()))
    }

let private streamed (_: Ctx) =
    act {
        let response = Response(Status.Ok).ContentType "text/plain"
        response.Body <- Body.Stream(new MemoryStream(Encoding.UTF8.GetBytes page))
        return response
    }

let private fileIn (path: string) (c: Ctx) = act { return! c.SendFile(path, SendOptions.Inline "text/plain") }

let private empty (c: Ctx) = act { return c.Html "" }

let private routes (kit: Kit) (path: string) : HttpEndpoint list =
    let route = Adapter.route kit
    [ route "/page" [ "GET", text ]
      route "/pooled" [ "GET", pooled ]
      route "/stream" [ "GET", streamed ]
      route "/file" [ "GET", fileIn path ]
      route "/empty" [ "GET", empty ]
      route "/blank-line" [ "GET", (fun c -> act { return c.Head(Status.Ok).Header("content-disposition", "\nattachment; filename=\"a.txt\"") }) ]
      route "/boom" [ "GET", (fun _ -> failwith "boom") ]
      route "/missing" [ "GET", (fun _ -> Task.FromResult(Error NotFound)) ]
      route "/teapot" [ "GET", (fun _ -> Task.FromResult(Error(Status 418))) ]
      route "/echo" [ "GET", (fun c -> act { return c.Json(Status.Ok, Value.Object [ "url", Value.String c.Request.Url; "ssl", Value.Bool c.Request.IsSsl; "path", Value.String c.Request.Fullpath; "cookie", Value.String(match c.Cookies.Get "b" with null -> "" | v -> v) ]) })
                      "POST", (fun c -> act { return c.Json(Status.Ok, Value.Object [ "params", c.Params.ToJson() ]) }) ] ]

/// A page served the way the app serves public files: its own response, marked as static.
let private staticFiles (kit: Kit) (app: IApplicationBuilder) : unit =
    app.Use(
        Func<HttpContext, RequestDelegate, Task>(fun http next ->
            match http.Request.Path.Value with
            | "/static.css" ->
                let response = Response(Status.Ok).ContentType("text/css").Header(Hdr.LastModified, "Thu, 01 Jan 1970 00:01:40 GMT")
                response.SetBody(page) |> ignore
                response.StaticFile <- true
                Adapter.write kit http response (http.Request.Method = "HEAD")
            | "/static-empty.css" ->
                let response = Response(Status.Ok).ContentType("text/css").Header(Hdr.ContentLength, "0")
                response.StaticFile <- true
                Adapter.write kit http response false
            | _ -> next.Invoke http)
    )
    |> ignore

let private startApp (deflate: bool) (config: KitConfig) : Task<TestApp * string> =
    task {
        let path = tempFile page
        let kit = kitWith { config with ErrorPages = ErrorPages.Of [ 404, bytesOf "<h1>Not found</h1>"; 500, bytesOf "<h1>Boom</h1>" ] } (obj ())
        let! app =
            startWith kit (routes kit path) (fun builder ->
                // config.ru: the deflater is the outermost layer.
                if deflate then Adapter.useDeflater kit builder |> ignore
                staticFiles kit builder)
        return app, path
    }

let private gz (req: Req) = req.With("accept-encoding", "gzip, deflate")

[<Fact>]
let ``every kind of body is written whole`` () =
    task {
        let! app, path = startApp false KitConfig.Default
        use _ = app
        try
            for target in [ "/page"; "/stream"; "/file" ] do
                let! reply = app.Send(get target)
                Assert.Equal(200, reply.Status)
                Assert.True((reply.Text = page), target)
            let! pooled = app.Send(get "/pooled")
            Assert.Equal(page, (pooled.Json.TryGet "page").Value.AsString.Value)
            Assert.Equal(Some "application/json; charset=utf-8", pooled.Header "content-type")
            let! empty = app.Send(get "/empty")
            Assert.Equal(200, empty.Status)
            Assert.Empty empty.Body
            // Not a deflater app: nothing is gzipped, whatever the client accepts.
            let! plain = app.Send(gz (get "/page"))
            Assert.Null(plain.Header "content-encoding" |> Option.toObj)
            Assert.Equal(Some(string page.Length), plain.Header "content-length")
        finally
            File.Delete path
    }

[<Fact>]
let ``the deflater gzips every kind of body for a client that accepts it`` () =
    task {
        let! app, path = startApp true KitConfig.Default
        use _ = app
        try
            for target in [ "/page"; "/stream"; "/file" ] do
                let! reply = app.Send(gz (get target))
                Assert.Equal(200, reply.Status)
                Assert.Equal(Some "gzip", reply.Header "content-encoding")
                Assert.Equal(None, reply.Header "content-length")
                Assert.Equal(Some "chunked", reply.Header "transfer-encoding")
                Assert.True(reply.HeaderValues("vary") |> List.exists (fun v -> v.Contains "Accept-Encoding"), target)
                Assert.True((gunzip reply.Body = page), target)
            let! pooled = app.Send(gz (get "/pooled"))
            Assert.Equal(Some "gzip", pooled.Header "content-encoding")
            Assert.Equal(page, (json (gunzip pooled.Body)).TryGet("page").Value.AsString.Value)
            // A response with an empty body is gzipped too: its length is the server's doing.
            let! empty = app.Send(gz (get "/empty"))
            Assert.Equal(Some "gzip", empty.Header "content-encoding")
            Assert.Equal("", gunzip empty.Body)
            // Without Accept-Encoding it goes out plain, but still varies on it.
            let! plain = app.Send(get "/page")
            Assert.Equal(None, plain.Header "content-encoding")
            Assert.Equal(page, plain.Text)
            Assert.True(plain.HeaderValues("vary") |> List.exists (fun v -> v.Contains "Accept-Encoding"))
        finally
            File.Delete path
    }

[<Fact>]
let ``a gzipped page keeps its etag and answers conditional gets`` () =
    task {
        let! app, path = startApp true KitConfig.Default
        use _ = app
        try
            let! plain = app.Send(get "/page")
            let! gzipped = app.Send(gz (get "/page"))
            Assert.Equal(plain.Header "etag", gzipped.Header "etag")
            // The second gzip of the same body comes from the cache, and is the same bytes.
            let! again = app.Send(gz (get "/page"))
            Assert.Equal<byte[]>(gzipped.Body, again.Body)
            let! conditional = app.Send((gz (get "/page")).With("if-none-match", nonNull (plain.Header "etag" |> Option.toObj)))
            Assert.Equal(304, conditional.Status)
            Assert.Empty conditional.Body
        finally
            File.Delete path
    }

[<Fact>]
let ``a HEAD answered 304 has no content length, and an unknown method is a 404 without Allow`` () =
    task {
        let! app, path = startApp false KitConfig.Default
        use _ = app
        try
            let! plain = app.Send(get "/page")
            let etag = nonNull (plain.Header "etag" |> Option.toObj)
            // Kestrel doesn't write `Content-Length: 0` for a HEAD that answers 304 (or 204), where Rust's hyper
            // does; Puma writes none either (README: Known differences).
            let! head = app.Send(((get "/page").AsMethod "HEAD").With("if-none-match", etag))
            Assert.Equal(304, head.Status)
            Assert.Equal(None, head.Header "content-length")
            // `route` answers any other method with Rails' 404; axum's would carry `Allow: GET,HEAD`.
            let! deleted = app.Send((get "/page").AsMethod "DELETE")
            Assert.Equal(404, deleted.Status)
            Assert.Equal(None, deleted.Header "allow")
            // Endpoint routing matches literal segments without regard to case and ignores a trailing slash:
            // Rails matches case-sensitively (and ignores the slash); axum is strict about both.
            let! upper = app.Send(get "/PAGE")
            Assert.Equal(200, upper.Status)
            let! slash = app.Send(get "/page/")
            Assert.Equal(200, slash.Status)
        finally
            File.Delete path
    }

[<Fact>]
let ``the deflater on HEAD, and when nothing is acceptable`` () =
    task {
        let! app, path = startApp true KitConfig.Default
        use _ = app
        try
            let! head = app.Send(gz ((get "/page").AsMethod "HEAD"))
            Assert.Equal(200, head.Status)
            Assert.Equal(Some "gzip", head.Header "content-encoding")
            Assert.Equal(None, head.Header "content-length")
            Assert.Empty head.Body

            let! refused = app.Send((get "/page").With("accept-encoding", "identity;q=0"))
            Assert.Equal(406, refused.Status)
            Assert.Equal(Some "text/plain", refused.Header "content-type")
            Assert.Equal("An acceptable encoding for the requested resource /page could not be found.", refused.Text)
            // It comes from outside the kit's own middleware.
            Assert.Equal(None, refused.Header "x-request-id")
            Assert.Equal(None, refused.Header "strict-transport-security")
        finally
            File.Delete path
    }

[<Fact>]
let ``static files skip the request headers and are gzipped with their mtime`` () =
    task {
        let! app, path = startApp true KitConfig.Default
        use _ = app
        try
            let! plain = app.Send(get "/static.css")
            Assert.Equal(200, plain.Status)
            Assert.Equal(None, plain.Header "x-request-id")
            Assert.Equal(None, plain.Header "x-runtime")
            let! gzipped = app.Send(gz (get "/static.css"))
            Assert.Equal(Some "gzip", gzipped.Header "content-encoding")
            // The gzip header carries the file's Last-Modified (100 seconds after the epoch).
            Assert.Equal<byte[]>([| 100uy; 0uy; 0uy; 0uy |], gzipped.Body[4..7])
            Assert.Equal(page, gunzip gzipped.Body)
            // A length of 0 the app set is left alone (error pages and empty files).
            let! emptied = app.Send(gz (get "/static-empty.css"))
            Assert.Equal(None, emptied.Header "content-encoding")
            Assert.Equal(Some "0", emptied.Header "content-length")
            // The kit's own responses do have them.
            let! kits = app.Send(get "/page")
            Assert.True(kits.Header("x-request-id").IsSome)
            Assert.True(kits.Header("x-runtime").IsSome)
        finally
            File.Delete path
    }

[<Fact>]
let ``errors in actions are rails error pages`` () =
    task {
        let! app, path = startApp false KitConfig.Default
        use _ = app
        try
            let! boom = app.Send(get "/boom")
            Assert.Equal(500, boom.Status)
            Assert.Equal("<h1>Boom</h1>", boom.Text)
            let! missing = app.Send(get "/missing")
            Assert.Equal(404, missing.Status)
            Assert.Equal("<h1>Not found</h1>", missing.Text)
            let! teapot = app.Send(get "/teapot")
            Assert.Equal(418, teapot.Status)
            // JSON clients get the hash PublicExceptions renders.
            let! json = app.Send((get "/missing").With("accept", "application/json"))
            Assert.Equal("""{"status":404,"error":"Not Found"}""", json.Text)
            Assert.Equal(Some "application/json; charset=UTF-8", json.Header "content-type")
            // And HEAD gets the format's content type and no body.
            let! head = app.Send((get "/missing").AsMethod "HEAD")
            Assert.Equal(404, head.Status)
            Assert.Equal(Some "0", head.Header "content-length")
        finally
            File.Delete path
    }

[<Fact>]
let ``requests reach the action as the proxy headers and the target describe them`` () =
    task {
        let! app, path = startApp false KitConfig.Default
        use _ = app
        try
            let! reply = app.Send((get "/echo?x=1").With("x-forwarded-proto", "https").With("host", "chat.example.com"))
            Assert.Equal(Some(Value.String "https://chat.example.com/echo?x=1"), reply.Json.TryGet "url")
            Assert.Equal(Some(Value.Bool true), reply.Json.TryGet "ssl")

            // An absolute-form target, as a proxy sends it.
            let! absolute = app.Send({ get "http://proxied.example/echo?y=2" with Headers = [ "host", "proxied.example" ] })
            Assert.Equal(200, absolute.Status)
            Assert.Equal(Some(Value.String "/echo?y=2"), absolute.Json.TryGet "path")

            // Cookie headers split over several lines are one jar.
            let! cookies = app.Send({ get "/echo" with Headers = [ "host", "chat.example.com"; "cookie", "a=1"; "cookie", "b=two" ] })
            Assert.Equal(Some(Value.String "two"), cookies.Json.TryGet "cookie")

            // The query keeps its bytes: a plus is a space, and %2B is a plus.
            let! encoded = app.Send(formPost "/echo?q=a+b%2Bc" "k=v+w")
            Assert.Equal(Some(json """{"k": "v w", "q": "a b+c"}"""), encoded.Json.TryGet "params")
        finally
            File.Delete path
    }

[<Fact>]
let ``request bodies are bounded by the limit whether or not their length is announced`` () =
    task {
        let! app, path = startApp false { KitConfig.Default with MaxBodyBytes = ValueSome 64 }
        use _ = app
        try
            let! announced = app.Send(formPost "/echo" (String.replicate 100 "a=1&"))
            Assert.Equal(413, announced.Status)
            let! small = app.Send(formPost "/echo" "a=1")
            Assert.Equal(200, small.Status)
            // Chunked, with no length announced: read up to the limit, then refused.
            // (`Send` adds a Content-Length to a body, so the chunked request is written by hand.)
            use client = new System.Net.Sockets.TcpClient()
            do! client.ConnectAsync(System.Net.IPAddress.Loopback, app.Port)
            let stream = client.GetStream()
            let write (s: string) = stream.WriteAsync(Encoding.UTF8.GetBytes s).AsTask()
            do! write "POST /echo HTTP/1.1\r\nhost: chat.example.com\r\ncontent-type: application/x-www-form-urlencoded\r\ntransfer-encoding: chunked\r\nconnection: close\r\n\r\n"
            let payload = String.replicate 100 "a=1&"
            do! write $"{payload.Length:x}\r\n{payload}\r\n0\r\n\r\n"
            use received = new MemoryStream()
            do! stream.CopyToAsync received
            Assert.StartsWith("HTTP/1.1 413", Encoding.UTF8.GetString(received.ToArray()))
        finally
            File.Delete path
    }

[<Fact>]
let ``a multipart body over the limit is 413 and leaves no temp files`` () =
    task {
        let! app, path = startApp false { KitConfig.Default with MaxBodyBytes = ValueSome 2000 }
        use _ = app
        try
            let marker = "kit-413-" + Guid.NewGuid().ToString("N")
            let body =
                $"--B\r\nContent-Disposition: form-data; name=\"f\"; filename=\"x\"\r\n\r\n{marker}{String('x', 5000)}\r\n--B--\r\n"
            let! reply = app.Send((post "/echo").With("content-type", "multipart/form-data; boundary=B").WithBody body)
            Assert.Equal(413, reply.Status)
            let leftovers = Directory.GetFiles(Path.GetTempPath(), "RackMultipart*") |> Array.filter (fun f -> (try File.ReadAllText(f).Contains marker with _ -> false))
            Assert.Empty leftovers
        finally
            File.Delete path
    }


[<Fact>]
let ``header values go out as UTF-8 and without control characters`` () =
    task {
        let kit = kitWith KitConfig.Default (obj ())
        let route = Adapter.route kit
        let action (c: Ctx) =
            act {
                c.SetHeader("x-name", "café ✓")
                c.SetHeader("x-lines", "a\nb\u0001c\nd")
                return c.Head Status.Ok
            }
        use! app = start kit [ route "/h" [ "GET", action ] ]
        let! reply = app.Send(get "/h")
        Assert.Equal(Some "café ✓", reply.Header "x-name")
        // Each line on its own, and the one with a control character left out.
        Assert.Equal<string list>([ "a"; "d" ], reply.HeaderValues "x-lines")
    }

/// HTTP/2 can send a body with no Content-Length at all, so "no length" is not "no body".
[<Fact>]
let ``an http2 request body without a content length is read`` () =
    task {
        let kit = kitWith KitConfig.Default (obj ())
        let route = Adapter.route kit
        use! app = startProtocols Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2 kit [ route "/echo" [ "POST", (fun c -> act { return c.Json(Status.Ok, Value.Object [ "params", c.Params.ToJson(); "raw", Value.String(Encoding.UTF8.GetString c.Request.RawPost.Span) ]) }); "GET", (fun c -> act { return c.Html "get" }) ] ] ignore
        use client = new System.Net.Http.HttpClient()
        let send (request: System.Net.Http.HttpRequestMessage) =
            request.Version <- System.Net.HttpVersion.Version20
            request.VersionPolicy <- System.Net.Http.HttpVersionPolicy.RequestVersionExact
            client.SendAsync request
        let uri = $"http://127.0.0.1:{app.Port}/echo"
        // `StreamContent` has no length, so the client sends the body in DATA frames alone.
        use content = new System.Net.Http.StreamContent(new MemoryStream(Encoding.UTF8.GetBytes "a=1&b[]=2&b[]=3"))
        content.Headers.ContentType <- System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded")
        use post = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, uri, Content = content)
        use! response = send post
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode)
        Assert.Equal(System.Net.HttpVersion.Version20, response.Version)
        let! body = response.Content.ReadAsStringAsync()
        Assert.Equal(Some(json """{"a": "1", "b": ["2", "3"]}"""), (json body).TryGet "params")
        Assert.Equal(Some(Value.String "a=1&b[]=2&b[]=3"), (json body).TryGet "raw")
        // And a GET, with no body to read.
        use get' = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, uri)
        use! response = send get'
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode)
        let! text = response.Content.ReadAsStringAsync()
        Assert.Equal("get", text)
    }


[<Fact>]
let ``a post that _method turned into a head sends no body`` () =
    task {
        let! app, path = startApp false KitConfig.Default
        use _ = app
        try
            // The wire says POST, so the host would hold the response to its Content-Length: with the body
            // dropped as for any HEAD, there is none.
            let! reply = app.Send((post "/page").With("content-type", "application/x-www-form-urlencoded").WithBody "_method=head")
            Assert.Equal(200, reply.Status)
            Assert.Empty reply.Body
            // The host says "0" itself where the response had no length: the framing holds.
            Assert.True((reply.Header "content-length" = None || reply.Header "content-length" = Some "0"))
        finally
            File.Delete path
    }

[<Fact>]
let ``a header with anything but visible ascii in it is no header`` () =
    task {
        let! app, path = startApp false KitConfig.Default
        use _ = app
        try
            // As `HeaderValue::to_str` sees it in Rust, a value with a byte from 0x80 isn't there at all, so
            // the request id is made, not taken from it.
            let! reply = app.Send((get "/page").With("x-request-id", "café-123"))
            Assert.Equal(36, (reply.Header "x-request-id").Value.Length)
            let! sane = app.Send((get "/page").With("x-request-id", "cafe-123"))
            Assert.Equal(Some "cafe-123", sane.Header "x-request-id")
        finally
            File.Delete path
    }


[<Fact>]
let ``the deflater's 406 is outside the SSL middleware too`` () =
    task {
        let tls = { KitConfig.Default with ForceSsl = true; Proxy = { ProxyConfig.Default with AssumeSsl = true } }
        let! app, path = startApp true tls
        use _ = app
        try
            let! ok = app.Send(get "/page")
            Assert.Equal(Some "max-age=63072000; includeSubDomains", ok.Header "strict-transport-security")
            let! refused = app.Send((get "/page").With("accept-encoding", "identity;q=0, *;q=0"))
            Assert.Equal(406, refused.Status)
            Assert.Equal(None, refused.Header "strict-transport-security")
        finally
            File.Delete path
    }

[<Fact>]
let ``a header whose first line is empty is written as a line of its own`` () =
    task {
        let! app, path = startApp false KitConfig.Default
        use _ = app
        try
            // Puma writes a value with a line break line by line, so a leading break leaves a header line with
            // nothing after the colon; Kestrel's `Append` would drop it as a lone empty value.
            let! reply = app.Send(get "/blank-line")
            Assert.Equal(200, reply.Status)
            Assert.Equal<string list>([ ""; "attachment; filename=\"a.txt\"" ], reply.HeaderValues "content-disposition")
        finally
            File.Delete path
    }

[<Fact>]
let ``a request head as large as puma and hyper accept is served`` () =
    task {
        let! app, path = startApp false KitConfig.Default
        use _ = app
        try
            // Kestrel's own limits are 32 KB of headers and an 8 KB request line; Rails (Puma: 112 KB, 12 KB of
            // URI) and the Rust port (hyper: 417,792 bytes of headers, a 65,534-byte URI) serve these, and a browser with many cookies on a
            // shared domain would otherwise get 431 on every page.
            let bigCookie = (get "/echo").With("cookie", "a=" + String('x', 40_000) + "; b=seen")
            let! cookie = app.Send bigCookie
            Assert.Equal(200, cookie.Status)
            Assert.Equal(Some(Value.String "seen"), cookie.Json.TryGet "cookie")

            let nineCookies = String.Join("; ", [ for i in 1..9 -> $"c{i}=" + String('y', 4_000) ]) + "; b=seen"
            let! many = app.Send((get "/echo").With("cookie", nineCookies))
            Assert.Equal(200, many.Status)

            let! header = app.Send((get "/echo").With("x-big", String('z', 70_000)))
            Assert.Equal(200, header.Status)

            let! url = app.Send(get ("/echo?q=" + String('q', 10_000)))
            Assert.Equal(200, url.Status)
            Assert.Equal(10_000 + "/echo?q=".Length, (url.Json.TryGet "path").Value.AsString.Value.Length)

            // Past hyper's header buffer the Rust port answers 431; so does this.
            let! tooBig = app.Send((get "/echo").With("x-big", String('z', 450_000)))
            Assert.Equal(431, tooBig.Status)

            // hyper caps the URI at 65,534 bytes and answers 414 past it; so does this.
            let! longUrl = app.Send(get ("/echo?q=" + String('q', 65_000)))
            Assert.Equal(200, longUrl.Status)
            let! tooLongUrl = app.Send(get ("/echo?q=" + String('q', 70_000)))
            Assert.Equal(414, tooLongUrl.Status)
        finally
            File.Delete path
    }

/// Send `request` as written and return the reply's status line.
let private statusLineOf (app: TestApp) (request: string) : Task<string> =
    task {
        use client = new System.Net.Sockets.TcpClient()
        do! client.ConnectAsync(System.Net.IPAddress.Loopback, app.Port)
        let stream = client.GetStream()
        do! stream.WriteAsync(Encoding.ASCII.GetBytes request)
        use received = new MemoryStream()
        do! stream.CopyToAsync received
        let text = Encoding.Latin1.GetString(received.ToArray())
        return text.Substring(0, text.IndexOf "\r\n")
    }

[<Fact>]
let ``kestrel's request line rules differ from puma's and hyper's in three recorded ways`` () =
    task {
        let! app, path = startApp false KitConfig.Default
        use _ = app
        try
            // Each of these is fixed in Kestrel's parser and can't be configured; README "Known differences" lists them
            // (Rails and the Rust port answer 200, 200 and `HTTP/1.0 200 OK`, and 302 for the null byte on /rooms/%00).
            let! nul = statusLineOf app "GET /echo%00 HTTP/1.1\r\nHost: chat.example.com\r\nConnection: close\r\n\r\n"
            Assert.Equal("HTTP/1.1 400 Bad Request", nul)
            let! authority = statusLineOf app "GET http://evil.example/echo HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n"
            Assert.Equal("HTTP/1.1 400 Bad Request", authority)
            let! old = statusLineOf app "GET /echo HTTP/1.0\r\nHost: chat.example.com\r\n\r\n"
            Assert.Equal("HTTP/1.1 200 OK", old)
        finally
            File.Delete path
    }
