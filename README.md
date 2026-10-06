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

## Development

```sh
git submodule update --init
bin/verify          # Release build with warnings as errors, then every test
```

See [`AGENTS.md`](AGENTS.md) for the layout and working rules.

## Known differences

The differences the Rust port lists from Rails apply here too (see its README). Differences
specific to this port are listed below, each citing the reference file it departs from.

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
  - Cleartext HTTP/2 (`H2C_ENABLED`) on a port that also serves HTTP/1.1 reads each connection's first
    bytes for HTTP/2's preface and tells Kestrel which protocol to speak through an internal Kestrel
    feature (`Campfire.Kit/Front/Conn.fs`, `FrontH2c`); if a Kestrel release changes it, the port serves
    HTTP/1.1 only and logs a warning. Tested by `FrontTests` ("speaks h2c only when enabled").
  - Compressed responses carry the same decoded bytes as Rust's, not the same compressed bytes (.NET's
    deflate and ZstdSharp are not flate2 and libzstd); the Rust test that pins the SHA-256 of a spliced gzip
    member checks the ETag and the decoded body instead.
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

## License

MIT. Campfire, the Rust port, its parity harness and vectors are © 37signals, LLC.
