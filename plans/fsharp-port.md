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
| html5ever (Gumbo limits) | AngleSharp, with Rust's corpus and hardening tests deciding parse equivalence |
| hmac/sha/pbkdf2/aes-gcm | System.Security.Cryptography |
| bcrypt | BCrypt.Net-Next |
| libvips FFI | NetVips over the same libvips 8.16.1 build |
| ffmpeg subprocess | same argv, `System.Diagnostics.Process` |
| tungstenite-style socket | ASP.NET Core WebSockets (permessage-deflate as Rust does it) |
| web-push / p256 / hkdf | System.Security.Cryptography ECDH + HKDF + AES-GCM |
| rustls + instant-acme | Kestrel TLS + an ACME client (choose in Phase 3) |
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
| 6 Parity | loop: `parity/bin/compare` (lean, then full matrix) → fix → repeat until no new failures | allowlist no larger than Rust's |
| 7 Performance | `bench/run --apps reference,rust,fsharp`; profile; optimize | F# faster than Rust on all five workloads, with rows verified for posts |
| 8 Datastar (optional) | a second frontend | screenshots identical; behavior screens pass. Needs sign-off first |
| 9 Publish | GitHub repo, README with results, upstream README PR | maintainers' review |
