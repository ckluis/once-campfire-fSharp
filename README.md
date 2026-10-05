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

## License

MIT. Campfire, the Rust port, its parity harness and vectors are © 37signals, LLC.
