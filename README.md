# Campfire in F#

An F# implementation of [ONCE Campfire](https://github.com/basecamp/once-campfire), built on
ASP.NET Core and Falco. It aims to be a drop-in replacement for the Rails app: the same SQLite
database, storage layout and signed or encrypted cookies, with the same pages and behavior.

**Status: under construction.** See [`plans/fsharp-port.md`](plans/fsharp-port.md) for the phases.

It is translated from the [Rust port](https://github.com/basecamp/once-campfire-rust) (pinned in
`rust/`), which reproduces the Rails app (pinned in `reference/`) and ships the parity harness,
golden vectors and benchmark tooling this repository reuses under the MIT license.

The Rails frontend ships with a few
[port-owned overrides](src/Campfire.Assets/OVERRIDES.md): three JavaScript files and one image that
deliberately differ from Rails.

## Running it

With [ONCE](https://github.com/basecamp/once), or Docker directly, as with the Rust port (the image is a drop-in for
the reference's: uid 1000, `/rails`, `/rails/storage/{db,files,backups}`, ports 80 and 443, the ONCE hooks):

```sh
docker build -t campfire-fsharp:app --build-arg APP_VERSION=... --build-arg GIT_REVISION=... .
docker run -d -p 80:80 -p 443:443 \
  -e SECRET_KEY_BASE=... -e VAPID_PUBLIC_KEY=... -e VAPID_PRIVATE_KEY=... \
  -e TLS_DOMAIN=chat.example.com \
  -v campfire:/rails/storage \
  campfire-fsharp:app
```

`TLS_DOMAIN` enables automatic Let's Encrypt certificates; `DISABLE_SSL` enables plain HTTP. The settings are in
[`Config.fs`](src/Campfire.App/Config.fs) (the app's) and [`Front/Config.fs`](src/Campfire.Kit/Front/Config.fs) (Thruster's).
For local development, `dotnet run --project src/Campfire.App -- server` with `SECRET_KEY_BASE` (or
`SECRET_KEY_BASE_DUMMY=1`) set and libvips installed (`HTTP_PORT=3000` avoids port 80).

## Development

```sh
git submodule update --init
bin/verify          # Release build with warnings as errors, then every test
```

See [`AGENTS.md`](AGENTS.md) for the layout and working rules.

## Known differences

The differences the Rust port lists from Rails apply here too (see its README). Differences
specific to this port are listed below, each citing the reference file it departs from.

- **Application** (`Campfire.App`; `rust/crates/campfire` is the port it follows):
  - `VAPID_SUBJECT` without a `TLS_DOMAIN` defaults to this project's URL (`https://github.com/ckluis/once-campfire-fsharp`);
    Rust's names its own. Tested by `ConfigTests` ("vapid subject defaults to the tls domain").
  - A response header whose first line is empty reaches the wire as a line of its own (the first line of a
    `Content-Disposition` that begins with a line break, which Puma writes), where Kestrel's `Append` would drop a
    lone empty value; the kit hands such a name's values over as one list. Tested by `AdapterTests` ("a header whose
    first line is empty is written as a line of its own") and `AppTests` ("proxied blobs write odd dispositions as
    puma does").
  - Route recognition looks a path's routes up by its first segment and tries their regexes in table order (Rust asks a
    `RegexSet` for the first of a verb's patterns that match). The answer is the same for every path of the
    generated corpus. Tested by `RouteTableTests` ("recognizes like a first match scan").
  - The `jemalloc` options of `main.rs` have no counterpart: .NET's GC is configured in the project file (server GC,
    tiered PGO); only the transparent-huge-page opt-out (`prctl`) is kept, on Linux.
  - `GET /rooms/:room_id/refresh?since=` clamps a `since` too large for the clock to the end of the .NET `DateTime` range
    (Rust: the end of jiff's), which only matters to what `created_at >` is compared with: no row is newer than either end,
    and every row is newer than the low one. Reference: `app/controllers/rooms/refreshes_controller.rb`.
  - Logs are `Microsoft.Extensions.Logging`'s console lines (`RAILS_LOG_LEVEL` or `CAMPFIRE_LOG` set the level), not
    `tracing`'s; the request lines come from the front server as Thruster's did.
  - The outbound HTTP clients (opengraph, Web Push, bot webhooks; `Integrations/Http.fs`) write the HTTP/1.1 exchange
    themselves over a stream, as Rust does over hyper's client connection, and verify TLS peers with .NET's `SslStream`
    (the operating system's CA store, no revocation checks) where Rust uses rustls with the system's certificates. The
    request line and headers go out as `Net::HTTP` writes them, in its order and spelling, which `WebhookTests`
    ("delivers like the reference") and `WebPushTests` ("delivers to the pinned address with the gems headers") compare.
    A gzip reply is inflated through .NET's `GZipStream` and `ZLibStream` read in 64 KB steps, so a bomb is stopped
    within 64 KB of the limit (Rust: within the decoder's 32 KB buffer); tested by `HttpTests`.
  - Web Push's VAPID signature is ECDSA with a random nonce (`ECDsa`), where Rust's p256 signs deterministically
    (RFC 6979); both verify under the public key, which `WebPushTests` ("signs the vapid header like the gem") checks.
  - An unfurl, a webhook delivery and a push that run past their deadline have their connection closed under the read
    in progress (Rust drops the future); the answer is the same ("Failed to respond within N seconds", no content, a
    read timeout). Tested by `OpengraphTests` ("gives up on a trickling page"), `WebhookTests` ("gives up on a trickling reply").
  - The opengraph `<meta>` scanner steps through UTF-16 units where Rust's steps through bytes; everything it matches is
    ASCII, so it splits the same places. `FILES_AND_MEDIA_URL_REGEX` is .NET's regex engine (non-backtracking) where
    Rust uses the `regex` crate: `\b` and `\S` can differ from Rust's on characters that are only marks or letter numbers.

- **Response headers set before the response exists** (`Ctx.SetHeader`, e.g. `X-Version` in a before-action)
  keep every line of a multi-line value. The Rust port keeps only the first line; Puma writes them all
  (puma 7.2.1, `Puma::Request#str_headers`, the reference's server). Tested by `AdapterTests`
  ("header values go out as UTF-8 and without control characters").

- **Front server** (`Campfire.Kit`, Thruster's job; Thruster 0.1.23 is the reference, `rust/crates/kit/src/front`
  the port it follows):
  - Idle HTTP/1 connections close at `HTTP_IDLE_TIMEOUT`, as Go's `http.Server` (Thruster) closed them. The
    Rust port closes them at the shorter of that and `HTTP_READ_TIMEOUT`, because hyper's header timer also
    runs while a connection waits; Kestrel's keep-alive timer doesn't. Tested by `FrontTests`
    ("closes idle and slow connections").
  - Kestrel always sends `Date`, so the app's own listener on `TARGET_PORT` sends one, where Puma (and the
    Rust port) send none.
  - Request head limits are hyper's, not Puma's: up to 417,792 bytes of headers (431 past that) and a 65,534-byte
    URI (414 past that; Kestrel limits the whole request line, so the edge moves by the method's length). Kestrel's
    own 8 KB line and 32 KB headers refused cookies and URLs Rails serves. Puma stops at
    112 KB of headers and answers 400 above 12 KB of URI (10 KB of query), where this port and the Rust port serve
    them. Tested by `AdapterTests` ("a request head as large as puma and hyper accept is served").
  - Three request-line rules are fixed in Kestrel's parser and can't be configured, so they differ from Puma's and
    hyper's: a `%00` in the path is 400 (Rails and Rust redirect or route it as any path); an absolute-form target
    whose authority differs from `Host` is 400 (both serve it); an HTTP/1.0 request is answered with an
    `HTTP/1.1` status line (both answer `HTTP/1.0`). Tested by `AdapterTests` ("kestrel's request line rules differ
    from puma's and hyper's in three recorded ways").
  - Cleartext HTTP/2 (`H2C_ENABLED`) on a port that also serves HTTP/1.1 reads each connection's first
    bytes for HTTP/2's preface and tells Kestrel which protocol to speak through an internal Kestrel
    feature (`Campfire.Kit/Front/Conn.fs`, `FrontH2c`); if a Kestrel release changes it, the port serves
    HTTP/1.1 only and logs a warning. Tested by `FrontTests` ("speaks h2c only when enabled").
  - Compressed responses carry the same decoded bytes as Rust's, not the same compressed bytes (.NET's
    deflate and ZstdSharp are not flate2 and libzstd); the Rust test that pins the SHA-256 of a spliced gzip
    member checks the ETag and the decoded body instead.
  - `HTTP_IDLE_TIMEOUT`, `HTTP_READ_TIMEOUT` and `HTTP_WRITE_TIMEOUT` above 49 days are clamped to 49 days,
    the longest .NET's timers can wait. Thruster and the Rust port accept any number of seconds; a value
    that large means "never" in all three. Tested by `FrontConfigTests`.
  - Certificates are ordered by a small RFC 8555 client of our own (`Front/Acme.fs`) in place of
    instant-acme, in autocert's cache layout as Rust writes it. It sends `User-Agent`, which RFC 8555
    requires (Pebble refuses requests without it), and renews at most once an hour for CAs whose
    certificates live less than 30 days.

- **Request and response handling** (`Campfire.Kit`; `rust/crates/kit/src/{adapter,ctx,params,request}.rs` is
  the port it follows, the reference is Rails 8.1 on Puma 7.2.1):
  - A request for a method a route doesn't have is Rails' 404 page (`ActionController::RoutingError`, as in
    `reference/config/routes.rb`: no route, no `Allow`). Rust's axum router also answers with the 404
    (`method_not_allowed_fallback`) but still adds an `Allow` header; ASP.NET's doesn't. Tested by
    `AdapterTests` ("a HEAD answered 304 has no content length, and an unknown method is a 404 without Allow").
  - Kestrel doesn't write `Content-Length: 0` for a HEAD that answers 204 or 304, where Rust's hyper does.
    Puma writes a `Content-Length` only when the response has one (`Puma::Request#prepare_response`, which
    skips it for 204), and Rails sets none on a 304. Tested by the same test.
  - `Ctx.Json(status, Value)` writes floats as the json gem does (`1e+15`, `0.00000015`: `Json.generate`),
    which is what `render json:` gives in Rails (json 2.21.2 in the reference). Rust's `Ctx::json` goes through
    serde_json (`1000000000000000.0`, `1.5e-7`). Tested by `RailsCompat.Tests` ("floats match the json gem").
  - `Adapter.route` and `routeAny` match through ASP.NET endpoint routing, which compares literal segments
    without regard to case (`/PAGE` is `/page`) and ignores a trailing slash. Rails is case-sensitive
    (`/Rooms/1` is a 404; `reference/config/routes.rb`, Journey) and, like ASP.NET, ignores the slash; axum,
    which Rust uses, is strict about both. Pinned by `AdapterTests` (same test as above). The app must
    not depend on the difference: an unknown-case path is a 200 here and a 404 in the references.
  - The files a multipart body spooled are deleted when the request ends (`Adapter.cleanUp`), as Rails'
    `Rack::TempfileReaper` does. In Rust a `TempPath` goes when the `UploadedFile` drops, so an app that
    kept the file past the request could still read it there. Tested by `AdapterTests` ("a multipart body
    over the limit is 413 and leaves no temp files").
  - A request header with a byte outside visible ASCII (0x20-0x7e, and tab) is treated as absent, as Rust's
    `HeaderValue::to_str` does; Rails/Rack would hand the app its bytes. Tested by `AdapterTests` ("a header
    with anything but visible ascii in it is no header").

- **Cable** (`Campfire.Cable`; `rust/crates/cable` is the port it follows, Action Cable 8.1 on Puma, with
  websocket-driver, is the reference): the `101 Switching Protocols` is written by Kestrel, so `Connection` is
  `Upgrade` (as websocket-driver writes it; Rust writes `upgrade`), and it carries `Date` and, unless the
  host turns it off, `Server`, which neither websocket-driver nor Rust sends. Tested by `ProtocolTests` ("the
  101 answers as websocket-driver does, plus the host's Date").

- **Views** (`Campfire.Views`; `rust/crates/views` is the port it follows). None of these changes a byte of
  output, which `bin/views-differential` compares with the Rust crate's on thousands of cases:
  - The view fragment cache counts what the page-splicing layer keeps for each fragment (its SHA-256 and compressed
    pieces) in the fragment's entry, and drops them with it. Rust keeps them in a bounded map in the kit, keyed by
    the address of the fragment's `Arc`, which a .NET object can't be found by (and `Campfire.Views` may not
    reference `Campfire.Kit`). A fragment's entry therefore costs its bytes plus its pieces' as of the last time it
    was used, and a page's fragments that keep being shown stay while the rest age out. Tested by
    `FragmentCacheTests` ("fragments a page keeps showing stay while the rest age out, pieces and SHA included").
  - A template digest in a fragment's key is a hash of the names of the template modules it renders, not of their
    source text (`ActionView::Digestor`, `include_str!` in Rust): the templates are compiled in and the cache doesn't
    outlive the process, so the digest only has to be stable within it.
  - `capitalize`, `User#initials`' word boundary and the case of a name's first letters use tables generated from
    Rust's standard library (`bin/views-differential --unicode`), not .NET's own casing, which leaves out special
    casing (`ß` is `SS`), the Turkish dotless i and characters Unicode added since. Rust's `capitalize` is what
    the references' `String#capitalize` is closest to; a name whose first letter has special casing prints as Rust
    prints it. Tested by `HelpersTests` ("capitalizes like rust") and the differential's every-code-point cases.
  - `String#downcase` of a user's name (the room forms' `data-value`) is Rust's `str::to_lowercase`, final-sigma rule included
    (`ΑΣ` is `ας`, `ΑΣΑ` is `ασα`), from `Case_Ignorable` and `Cased` ranges generated from Rust's standard library with the
    other Unicode tables. .NET's `ToLowerInvariant` writes `σ` for every sigma and leaves out special casing. Tested by
    `HelpersTests` ("to_lowercase ...") and the differential's sigma cases and every code point.
  - Timestamps in cache keys and `to_fs(:epoch)` are `DateTimeOffset`s, which hold 100 ns where jiff holds 1 ns.
    A key shows microseconds, so no key differs; a record dated before 1970 or outside the years 1 to 9999 is not
    representable (no record is).

## License

MIT. Campfire, the Rust port, its parity harness and vectors are © 37signals, LLC.
