# Porting Campfire to F#

Status: in progress (Phase 0 done 2026-10-05; Phases 1-4 done; Phase 5 units 5.1, boot, wiring and the image, 5.2, the account-side controllers, 5.3, the hot path, and 5.4, channels and integrations, done 2026-10-06)

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

### Phase 5 progress

- *Unit 5.1 (boot, wiring and the image), 2026-10-06:* `Campfire.App` boots and serves. Ported from `rust/crates/campfire`: `main`
  (`server`, `backup`), `app` (state, boot, the pipeline: Falco's `/cable`, public files, the Rails route table as the fallback),
  `config`, `concerns` (the `ApplicationController` chain, `UserAgent`, `ApplicationPlatform`), `controllers.rs` (the 177-row route
  table, the 111 recognitions of `vectors/campfire_routes.json`, health, Turbo Native, Action Mailbox), `jobs`, `rich_text`,
  `active_storage` (all the engine's endpoints and `purge`), the fragment glue, and from `controllers/presenters` and
  `controllers/{sessions,welcome}` what the layout and sign-in need (`view_context`, `page`, `rich_text`, `accounts`' helpers,
  `attached_blob`); from `channels` the cable server, its connection, revocation, `Broadcasts` and the read/unread channels.
  The image (`Dockerfile`) is built in colima as `campfire-fsharp:app`; `parity/bin/candidate up` serves it and a seed user signs in.
  What is still to port, by the units that follow: every other controller (their rows answer 501), the remaining channels
  (presence, room, room messages, typing, and `Turbo::StreamsChannel` with its room guard), the presenters that map rows to
  view models, and the integrations (Web Push, webhooks, opengraph, network guard, QR, search word ranges). Numbers for this
  unit are in `bench/results/campfire-app-boot.md`.

- *Unit 5.2 (the account-side controllers), 2026-10-06:* every controller of the account, session and user screens is ported
  and routed (the rows that answered 501 are gone for them): first runs, session transfers, the PWA manifest and service
  worker, accounts (edit, update, bots and their keys, custom styles, join codes, the logo, the people list), users (join, show,
  avatar, ban, profile, sidebar, push subscriptions and test notifications), autocompletable users, the QR code (`Rqrcode`,
  rqrcode_core's encoder byte for byte against the gem's vectors) and unfurl links, with `presenters/accounts.rs`,
  `pagination.rs` and the writing half of `attachments.rs` (assign, destroy, analyze). Not account-side but needed by two of
  them, `integrations/net.rs`'s resolver and `net/guard.rs` (the private network guard) are in `Integrations/`: push endpoint
  validation and the unfurl URL check go through it. What is still the integrations unit's: fetching and parsing a page
  for an unfurl (`Opengraph.unfurl` answers 204 for a URL the guard refuses, as the reference does, and raises for one it would
  fetch) and delivering a Web Push (`WebPush.deliverTestNotification` raises; the layout still shows no VAPID key because the
  pool isn't built). `reference-tools/campfire/controllers_a/replay.py` runs the account controllers against the reference and
  the F# image (152 scenarios matched on the default seed, 15 on crowd, 11 on first_run; three differ by design, being token
  checks); it found the one bug the Rust tests don't reach (a `?1` SQL parameter in the attachment writes, a 500 on every
  upload). Numbers: `bench/results/campfire-app-account-controllers.md`.

- *Unit 5.3 (the hot path), 2026-10-06:* the room, message and search controllers are ported and routed, and no row of
  the route table answers 501 any more. `Presenters/Presenter.fs` is `presenters.rs` (the rows-to-view-models mapper, with
  the cached message fragment lookup, the Jbuilder caches keyed by base URL, `all_emoji` over regex-syntax's Unicode 16
  tables as Rust's regex has them), `Integrations/Search.fs` is `SearchesController#query` over Ruby's `[[:word:]]` table.
  Controllers: `MessagesController` (index with conditional gets, create with multipart upload and `process_attachment`,
  show, edit, update, destroy, the broadcasts and webhook delivery), `BoostsController`, `ByBots` and `BoostsByBots` (the bot
  API with raw bodies and multipart), `RoomsController` with `Opens`, `Closeds`, `Directs`, `Involvements` and
  `Refreshes`, and `SearchesController`. The room, messages, search and refresh pages are recorded pages: each message is a
  cached fragment noted at its offset, and the kit splices them as parts (gzipped from their stored pieces, never
  re-rendered or joined), as in Rust; `RoomsTests` ("pages of messages go out in parts") holds the plain, gzipped and HEAD
  answers to one page and one ETag. All of `rooms/tests.rs`, `messages/tests.rs`, `searches.rs`'s tests and
  `concurrent_message_posts_all_complete` are ported. Still to come: the channels (`RoomChannel`, `RoomMessagesChannel`,
  presence, typing and the guarded `Turbo::StreamsChannel`), which `a text message is answered and broadcast as it was
  stored` stands in for by reading the hub, and the integrations (Web Push delivery, webhooks, opengraph fetch). Checked
  against the Rust image on a frozen-clock seed: the room page, messages page, sidebar, search, refresh, bot JSON, room forms
  and a posted message are byte for byte the same. Numbers (no tuning): `bench/results/campfire-app-hot-path-controllers.md`.

- *Unit 5.4 (channels and integrations), 2026-10-06:* `Campfire.Cable` is mounted at `/cable` with every channel of the
  Rust crate: `PresenceChannel` (the membership connected while subscribed, the reads stream told on `present`),
  `RoomChannel`, `TypingNotificationsChannel`, `RoomMessagesChannel` and the stock `Turbo::StreamsChannel` with its
  `RoomStreamsAreAuthorized` guard, beside the read, unread and heartbeat channels of unit 5.1; no room stream is served to
  anyone but a current member. The three HTTP client policies are ported, never sharing a client: `Integrations/Http.fs` is
  `net/http.rs` (a `Net::HTTP` exchange over a pinned or resolved socket: the header order and spelling the tests compare, open
  and read timeouts, chunked and until-close bodies, a size-capped inflater), with the guard of unit 5.2, `Opengraph*.fs`
  (the guard and pinned addresses, every redirect re-checked, 5 MB and 10 responses, a 10 second deadline and 16 at once, the
  libxml2 `<meta>` scanner and its entity table), `WebPush*.fs` and `Vapid.fs` (endpoint restrictions and pinning, VAPID,
  RFC 8291 encryption, the pool of 50 with its invalidation worker) and `Webhook.fs` (unrestricted, 7 seconds, 100 MB,
  60 seconds in all). `IntegrationJobs.fs` registers `Room::PushMessageJob` and `Bot::WebhookJob` with the runner and builds
  the pool at boot (off without a valid VAPID pair, as in Rust). `AppState` holds the `WebPushPool` itself now, and the
  integrations' files that don't need the app come before it in the project. Tests: all of `channels/tests` (channels_test,
  broadcasts_test, revocation_test and the golden frames recorded from the reference, replayed over a `ClientWebSocket`) and
  every `#[test]` of `integrations/` (the opengraph and webhook cases recorded from the reference, the gem's ciphertext and
  headers, the gzip bombs, the pool's limits); Rust's `record_reference` (an `#[ignore]`d re-recorder that needs a running
  reference app) is the one test not ported. Checked in the image: a Cable connection to `/cable` gets the welcome frame and
  subscribes to a room its user belongs to, and the room page and sidebar are byte for byte Rust's with the VAPID tag in place.
  Numbers (no tuning): `bench/results/campfire-app-channels-integrations.md`.

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
  *Unit 4.2:* a room page (and the messages page, search and a refresh) gives each message to the recorded page as a
  fragment, `Fragment` and all, and `RoomsViewsTests` ("show recorded keeps every message as a fragment") holds a
  recorded page to a plain render, cold and warm, in the application layout and the Turbo-Frame one.
  *Unit 4.3:* the remaining pages (accounts, bots, sessions, users, profile, push subscriptions, first run) cache
  no fragments, so they only have to render the same bytes as Rust's in both layouts: `bin/views-differential` renders
  each cold and warm, plain and recorded, and in the Turbo-Frame layout when its case takes `frame`. Every Rust
  `#[test]` and template of the crate has an F# counterpart (`bin/views-test-parity`).
- **Phase 7: the gaps `bench/results/cable-fanout.md` and `bench/results/front.md` record are open.** F# is slower
  than Rust on plain-frame Cable bursts, lone broadcasts, idle CPU and memory per client, and on the front's
  signed-cookie page, 100 KB identity page and 20 KB asset cache hit. They are gates, not accepted costs.

