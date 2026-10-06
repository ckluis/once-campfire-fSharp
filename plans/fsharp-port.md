# Porting Campfire to F#

Status: in progress (Phase 0 done 2026-10-05)

## Goal

An F# Campfire that can't be told apart from Rails (`reference/` at `90b3300`) or the Rust port
(`rust/` at `ccece30`), and is faster than the Rust port on all five benchmarked HTTP workloads
(room page, messages page, sidebar, search, post a message). Bugs may be fixed rather than copied;
each fix is listed under "Known differences" in `README.md`.

Offered upstream as its own repo, with a PR to basecamp/once-campfire adding it to the "Other
implementations" table, for the maintainers to review, re-measure and host.

## Decisions (2026-10-05)

| # | Decision |
|---|---|
| D1 | Own repo (`ckluis/once-campfire-fsharp`, MIT), then a README PR upstream for them to review and host |
| D2 | Rust's bar: pass its parity harness (HTML, DOM, accessibility, pixels, Cable frames) and stay a drop-in replacement (schema, storage, cookies) |
| D3 | ASP.NET Core (Kestrel) + Falco routing. The Rails frontend (Turbo, Stimulus, Lexxy, Action Cable client) ships unchanged. A Datastar frontend is a possible Phase 8, judged on pixels and behavior, after full parity and only with sign-off |
| D4 | Docker via colima. Parity and bench scripts run inside the colima VM (`colima ssh`), which gives them GNU userland and `--network host` |
| D5 | Numbers from `bench/run` on one machine with Rails, Rust and F#; the PR quotes ratios to Rust and asks maintainers to re-measure |
| D6 | Built by phased multi-agent workflows, each phase gated before the next starts |

Rust's deliberate differences from Rails (its README, "Known differences") are ours too, so the two
ports do the same work in the benchmark.

## Translation approach

Translate `rust/` module by module, tests included, citing the source file. Rust already proved
each contract against Rails with golden vectors, the parity harness and ported Rails tests. Read
`reference/` when Rust is unclear or looks wrong. `plans/rust-conversion.md` is the contract
catalogue: cookies, CSRF, persisted data, Active Storage, real time, HTTP clients, request
contracts and packaging.

Library mapping:

| Rust | F# / .NET |
|---|---|
| axum + hyper + tokio | ASP.NET Core Kestrel + Falco routing; controllers take our `Ctx`, never `HttpContext` |
| rusqlite (writer task + reader pool) | Microsoft.Data.Sqlite, same topology |
| askama templates | one F# render function per ERB file writing UTF-8 into pooled buffers |
| html5ever (Gumbo limits) | a port of the vendored html5ever's tokenizer and tree builder (`Campfire.RichText/Html5ever`): AngleSharp can't stop at Gumbo's tree depth limit while it builds the tree (its tree builder is internal) or count a tag's attributes as the tokenizer reads them. Rust's corpus and hardening tests decide parse equivalence |
| hmac/sha/pbkdf2/aes-gcm | System.Security.Cryptography |
| bcrypt | BCrypt.Net-Next |
| libvips FFI | NetVips over the same libvips 8.16.1 build |
| ffmpeg subprocess | same argv, `System.Diagnostics.Process` |
| multer + mime + httparse (multipart) | `Campfire.Kit/Multipart.fs`, a port of multer 3.1.0 with the `mime` and `httparse` code it leans on: ASP.NET's `MultipartReader` accepted bodies multer refuses (no closing boundary, bodies cut short) and read part headers its own way, and the Rust port is what Rails parity was proved against. `bin/kit-differential` shows no difference on 9 kinds of input, multipart among them |
| flate2 / crc32fast (gzip) | `System.IO.Compression.DeflateStream` for the deflate, with the gzip header and trailer written by hand (the modification time and the Unix OS code Rack writes) and a CRC-32 on the ARM instructions or slicing-by-8 tables. Compressed bytes differ from flate2's; what they decode to doesn't |
| the cable's own socket (RFC 6455 server framing, `permessage-deflate` without context takeover) | `Campfire.Cable/Socket.fs`, the same framing over the stream `IHttpUpgradeFeature.UpgradeAsync` returns: ASP.NET Core's `WebSocket` deflates per socket, and a broadcast has to be deflated once for all its subscribers. `DeflateStream` does the deflate (sync-flushed, the 4-byte tail stripped); no package |
| `tokio::sync::broadcast` per broadcasting | a ring per group of subscribers (`Campfire.Cable/Pubsub.fs`) read by index with a seqlock check, and one `Wake` (an allocation-free auto-reset event over `IValueTaskSource`) per connection instead of a task per subscription |
| web-push / p256 / hkdf | System.Security.Cryptography ECDH + HKDF + AES-GCM |
| rustls + instant-acme | Kestrel TLS (a handshake callback picks the certificate, after reading the ClientHello's ALPN list off the connection for TLS-ALPN-01) + `Front/Acme.fs`, a small RFC 8555 client on `HttpClient` and `System.Security.Cryptography`: Certes and its forks are unmaintained and LettuceEncrypt keeps PFX files behind its own hosting integration, while the part of ACME this needs is a few hundred lines. Exercised against Pebble and a stub CA |
| zstd (zstd crate) | ZstdSharp.Port, a managed port (.NET 10 has no zstd; the front server compresses with it for clients that accept zstd but not gzip) |
| hyper's server, hyper-util `auto` (h1, h2, h2c) | Kestrel, one host for every listener; cleartext HTTP/2 beside HTTP/1.1 by reading a connection's first bytes (`FrontH2c`) |
| qrcode (pinned to RQRCode) | port Rust's `rqrcode.rs` directly |

## Phases and gates

Each phase is one workflow of sequential build units and an independent verifier. A phase is done
when `bin/verify` is green, every Rust test in scope has an F# counterpart, and the verifier's
findings are fixed.

| Phase | Units (Rust sources) | Gate |
|---|---|---|
| 1 Foundations | Ruby, RailsCompat, Routes; Assets | `ruby_core`, `rails_compat`, `campfire_routes` vectors pass; Rails verifies cookies F# issues; assets match the reference's digests |
| 2 Data and content | Db; RichText; Storage | ported db, richtext (corpus + hardening) and storage vector tests pass |
| 3 HTTP and real time | Kit core (ctx, params, cookies, session, CSRF, formats, body, adapter); Kit front (TLS, ACME, HTTP/2, compression, response cache); Cable | ported kit and cable tests, including golden Cable frames |
| 4 Views | helpers, fragment cache, layouts, rooms, messages; the remaining templates | ported view tests; rendered HTML for the seed matches Rust's byte for byte |
| 5 App | boot, config, concerns, CLI, Dockerfile, parity candidate wiring; rooms, messages, searches, bots, presenters; channels, broadcasts, jobs; integrations (Web Push, webhooks, opengraph, network guard, QR, user agent) | ported app tests with the seed; the image boots under `parity/bin/candidate` |
| 5b Baseline | `bench/run --apps reference,rust,fsharp` as soon as the app boots, before the parity loop, with a per-layer cost breakdown (Kestrel, Falco, SQLite, templates, rich text) | numbers recorded under `bench/results/`; no gate, it sets Phase 7's targets |
| 6 Parity | loop: `parity/bin/compare` (lean, then full matrix) → fix → repeat until no new failures | allowlist no larger than Rust's |
| 7 Performance | `bench/run --apps reference,rust,fsharp`; profile; optimize | aim: faster than Rust on all five workloads (rows verified for posts); close to Rust counts as a win, reported as close. Known gaps so far: signed-cookie requests, identity bodies through Kestrel, Db row reads, rich text, plain-frame Cable fan-out |
| 8 Datastar (optional) | a second frontend | screenshots identical; behavior screens pass. Needs sign-off first |
| 9 Publish | GitHub repo, README with results, upstream README PR | maintainers' review |

### Requirements carried forward

Findings of a phase's verifier that a later phase has to meet. Each is a gate of that phase.

- **Phase 4: the fragment cache is byte-bounded, pieces and SHA included.** Rust keeps a fragment's SHA-256 and
  its compressed pieces in a generation-bounded map (`rust/crates/kit/src/deflater/splice.rs`, `Generations`),
  and its test `fragments_a_page_keeps_showing_stay_while_the_rest_age_out` holds the total at or under
  `budget + 2 * 2048` while 300 other fragments churn. `Campfire.Kit`'s `Splice.Fragment` carries both itself
  (at most `PiecesPerFragment` pieces), so nothing in the kit bounds them: the view fragment cache is what does,
  by dropping `Fragment` objects. Its budget must count the bytes of the pieces and the SHA each `Fragment`
  holds, not only the fragment's own bytes, and the phase adds the test that reproduces Rust's: a page's
  fragments that keep being shown stay while others age out, and the total held stays within the budget.
  *Unit 4.1:* `Campfire.Views` cannot reference the kit, so its own `Fragment` carries an `IFragmentAttachment`
  the kit's page parts hang their SHA and pieces on (`Campfire.App` links the two when it first splices a view
  fragment, Phase 5), and the cache counts `Fragment.HeldBytes` each time an entry is used; the test is
  `FragmentCacheTests` ("fragments a page keeps showing stay while the rest age out, pieces and SHA included").
- **Phase 7: the gaps `bench/results/cable-fanout.md` and `bench/results/front.md` record are open.** F# is slower
  than Rust on plain-frame Cable bursts, lone broadcasts, idle CPU and memory per client, and on the front's
  signed-cookie page, 100 KB identity page and 20 KB asset cache hit. They are gates, not accepted costs.

