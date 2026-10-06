export const meta = {
  name: 'campfire-fs-phase5',
  description: 'Phase 5 of the F# Campfire port: the app (boot, controllers, channels, integrations, Docker image) through to a first parity smoke',
  phases: [
    { title: 'Build', detail: 'boot+wiring+image; small controllers; rooms/messages/search; channels+integrations' },
    { title: 'Verify', detail: 'adversarial review + first parity smoke against Rails', model: 'opus' },
    { title: 'Fix', detail: 'fix confirmed findings, re-verify' },
  ],
}

const REPO = '/Users/clank/Desktop/projects/once-campfire-fsharp'

const BUILD_RESULT = {
  type: 'object',
  properties: {
    summary: { type: 'string' },
    commits: { type: 'array', items: { type: 'string' } },
    rust_tests_in_scope: { type: 'integer' },
    fsharp_tests_ported: { type: 'integer' },
    rust_files_in_scope: { type: 'string', description: 'every Rust source file in scope, checked off, and any not ported by name' },
    seed_tests: { type: 'string' },
    perf_notes: { type: 'string', description: 'recorded numbers only; no long tuning in this phase' },
    verify_passed: { type: 'boolean' },
    verify_output_tail: { type: 'string' },
    deferred: { type: 'array', items: { type: 'string' } },
    divergences: { type: 'array', items: { type: 'string' } },
  },
  required: ['summary', 'commits', 'rust_tests_in_scope', 'fsharp_tests_ported', 'rust_files_in_scope', 'seed_tests', 'perf_notes', 'verify_passed', 'verify_output_tail', 'deferred', 'divergences'],
}

const FINDINGS = {
  type: 'object',
  properties: {
    verify_passed: { type: 'boolean' },
    parity_smoke: { type: 'string', description: 'which states were compared against the reference, pass/fail/error counts, and what failed' },
    findings: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          severity: { type: 'string', enum: ['critical', 'major', 'minor'] },
          file: { type: 'string' },
          issue: { type: 'string' },
          evidence: { type: 'string' },
          fix: { type: 'string' },
        },
        required: ['severity', 'file', 'issue', 'evidence', 'fix'],
      },
    },
  },
  required: ['verify_passed', 'parity_smoke', 'findings'],
}

const COMMON = `You are working in ${REPO}, an F# port of Basecamp's ONCE Campfire. Read AGENTS.md and plans/fsharp-port.md first and follow them exactly: translate from rust/ (the Rust port, pinned; this phase ports rust/crates/campfire into src/Campfire.App, tests into tests/Campfire.App.Tests), use reference/ (Rails) as the oracle, never edit or write into reference/, rust/ or vectors/, commit each logical unit on the current branch (port) with a clear message ending in a blank line and "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>", never push. Phases 1-4 are done (every library project: Ruby, RailsCompat, Routes, Assets, Db, RichText, Storage, Kit with its front server and page parts, Cable, Views with its fragment cache); reuse them and don't duplicate their logic in the app. Controllers take Kit's Ctx, never HttpContext; Falco does routing only. Any Rust helper you build uses CARGO_TARGET_DIR=target/rust; differential scripts delete their outputs on success; run bin/clean-rust-builds when done. This phase records performance numbers but does not spend long tuning them: a whole-app baseline right after this phase decides what to optimize. bin/verify must pass before you finish; run it with CAMPFIRE_REQUIRE_SEED=1 at least once (the app's integration tests need the parity seeds in parity/.seed). Run Docker-based scripts inside colima: colima ssh -- bash -c 'cd ${REPO} && ...', with PARITY_NET_DIR=/tmp/parity-net (mkdir -p it) for page captures. Run commands synchronously in the foreground; never background a long command and wait on it. Ask nothing; decide, and record decisions under divergences. Report honestly: anything not done goes under deferred with the reason.`

phase('Build')
const u1 = await agent(`${COMMON}

UNIT 5.1: boot, wiring and the image. Port from rust/crates/campfire/src: main.rs (the CLI: server, backup and any other subcommands), app.rs and app/ (router wiring; recognize every path in vectors/campfire_routes.json's 111 "recognitions", deferred from Phase 1, with a test asserting the count), config.rs (every env var), concerns.rs and concerns/ (authentication, authorization, platform, user_agent, and the rest), controllers.rs (shared controller code), jobs.rs (in-process bounded queues with JOB_CONCURRENCY per kind, per rust/README.md), rich_text.rs, active_storage.rs (the /rails/active_storage endpoints), test_support.rs, and the glue that attaches Kit's page-part Fragment to the Views Fragment (Phase 4 left the contract in KitIntegrationTests). Then packaging: a production Dockerfile that is a drop-in for the reference image exactly as rust/Dockerfile is (same user, workdir, storage layout, env vars, ports, ONCE hooks; libvips and ffmpeg from the same Debian source stages as Dockerfile.toolchain; the .NET app published self-contained or framework-dependent, your choice with reasons), built in colima as campfire-fsharp:app; and parity/bin/candidate pointed at it (PARITY_CANDIDATE_APP_IMAGE=campfire-fsharp:app by default in this repo, keeping the Rust image selectable). Prove it boots: parity/bin/candidate up, GET /up returns 200, and a sign-in with a seed user (see parity/seeds) sets session_token. Port the tests for everything above (app/tests.rs and any #[cfg(test)] modules).
Return the structured result.`, { label: 'build:boot+image', phase: 'Build', schema: BUILD_RESULT, model: 'sonnet', effort: 'high' })
log(`Unit 5.1: verify=${u1 && u1.verify_passed}, tests ${u1 && u1.fsharp_tests_ported}/${u1 && u1.rust_tests_in_scope}`)

const u2 = await agent(`${COMMON}

UNIT 5.2: the account-side controllers. Unit 5.1 ported boot, the router, concerns, jobs, active storage and the Docker image; build on it. Port from rust/crates/campfire/src/controllers: welcome.rs, sessions.rs and sessions/, first_runs.rs, pwa.rs, accounts.rs and accounts/ (bots, custom styles, users, join codes), users.rs and users/ (profiles, avatars, push_subscriptions, sidebars), autocompletable.rs, qr_code.rs and qr_code/ (rqrcode.rs: the QR SVG must match RQRCode's output exactly), unfurl_links.rs, and the presenters they use (presenters/accounts.rs, view_context.rs, test_support.rs, and any other presenter file not about rooms, messages or search). Port their tests (controllers' tests/ and presenters/accounts/tests.rs). Rebuild campfire-fsharp:app at the end and confirm it still boots and signs in.
Return the structured result.`, { label: 'build:account-controllers', phase: 'Build', schema: BUILD_RESULT, model: 'sonnet', effort: 'high' })
log(`Unit 5.2: verify=${u2 && u2.verify_passed}, tests ${u2 && u2.fsharp_tests_ported}/${u2 && u2.rust_tests_in_scope}`)

const u3 = await agent(`${COMMON}

UNIT 5.3: the hot path. Units 5.1-5.2 are done; build on them. Port from rust/crates/campfire/src/controllers: rooms.rs and rooms/ (opens, closeds, directs, involvements, refreshes, and the rest), messages.rs and messages/ (by_bots with its raw-body and multipart API, boosts), searches.rs, and the remaining presenters (presenters.rs, page.rs, pagination.rs: timestamp-based, 40 per page, page_around up to 81; attachments.rs; anything left). These serve four of the five benchmarked workloads (room page, messages page, sidebar, search) and the fifth (post a message), so wire them to the fragment cache and page parts exactly as Rust does (the room and messages pages must be assembled from cached parts and gzip-spliced, not re-rendered). Port their tests (rooms/tests.rs, messages/tests.rs and the rest). Rebuild campfire-fsharp:app; with it running under parity/bin/candidate up, signed in as a seed user, GET the room page, messages page (?before=<a message id in that room>), sidebar and search, and POST a message, and check each is 200 (204 is wrong for a valid messages page) and that the post wrote a row. Record per-request latency for those five with a quick wrk run against the container (no tuning).
Return the structured result.`, { label: 'build:hot-controllers', phase: 'Build', schema: BUILD_RESULT, model: 'sonnet', effort: 'high' })
log(`Unit 5.3: verify=${u3 && u3.verify_passed}, tests ${u3 && u3.fsharp_tests_ported}/${u3 && u3.rust_tests_in_scope}`)

const u4 = await agent(`${COMMON}

UNIT 5.4: channels and integrations. Units 5.1-5.3 are done; build on them. Port rust/crates/campfire/src/channels.rs and channels/ (the seven app channels plus Turbo::StreamsChannel wiring, presence and heartbeat, typing, read/unread, room messages, broadcasts.rs) and their tests (channels/tests: golden frames, channels_test, broadcasts_test, revocation_test, support), mounting Campfire.Cable at /cable. Port rust/crates/campfire/src/integrations.rs and integrations/: net (http.rs and guard.rs: three HTTP client policies, never one shared client: opengraph with the private-network guard, pinned IPs, re-checked redirects, 5 MB and 10-redirect caps; Web Push with endpoint restrictions and IP pinning; bot webhooks intentionally unrestricted with a 7 s timeout), web_push (VAPID, encryption, pool), webhook.rs, opengraph/ (fetch, document, html, entities, metadata, location), search/ (word_ranges and query preprocessing), jobs.rs, test_support.rs, and their tests. Rebuild campfire-fsharp:app and confirm a Cable connection to /cable gets the welcome frame and can subscribe to a room the user belongs to.
Return the structured result.`, { label: 'build:channels+integrations', phase: 'Build', schema: BUILD_RESULT, model: 'sonnet', effort: 'high' })
log(`Unit 5.4: verify=${u4 && u4.verify_passed}, tests ${u4 && u4.fsharp_tests_ported}/${u4 && u4.rust_tests_in_scope}`)

phase('Verify')
const v1 = await agent(`${COMMON}

You are the adversarial verifier for Phase 5. Do not modify source files (you may build images and run servers). Find every way src/Campfire.App and its image differ from rust/crates/campfire and rust/Dockerfile, or from Rails.
Builders reported:
5.1: ${JSON.stringify(u1)}
5.2: ${JSON.stringify(u2)}
5.3: ${JSON.stringify(u3)}
5.4: ${JSON.stringify(u4)}
Do, at minimum:
1. Coverage: every Rust source file and #[test] in rust/crates/campfire has an F# counterpart; list missing ones by name.
2. A first parity smoke against Rails with the F# image: inside colima, with PARITY_NET_DIR=/tmp/parity-net, run parity/bin/candidate compare --matrix lean --engines chromium --viewports desktop --schemes light --breakpoints exclude --workers 4 on a broad sample of states (at least: rooms/show/*, searches/*, users/*, accounts/*, sessions/*, and a few interactions), and report pass/fail/error counts and the main failure causes in parity_smoke. Don't try to make it pass; Phase 6 does that. If many states fail for one cause (boot, sign-in, assets), say so.
3. Security: authentication and authorization on every route (non-member access to rooms, admin-only actions, bot keys only on bot routes), CSRF policy, the three HTTP client policies and the private-network guard (SSRF), webhook isolation, Cable channel authorization and revocation, file serving behind signed URLs, the backup command.
4. Drop-in compatibility: the image's user, paths, env vars, ports and hooks match rust/Dockerfile; Rails can boot on a database the F# app wrote.
5. Run bin/verify with CAMPFIRE_REQUIRE_SEED=1 and report.
Report only real, evidenced findings. Severity: critical = wrong behavior users or security would see; major = missing port/test or broken workload; minor = style or citation gaps.`, { label: 'verify:phase5', phase: 'Verify', schema: FINDINGS, model: 'opus', effort: 'high' })
const findings = (v1 && v1.findings) || []
log(`Verifier: ${findings.length} findings (${findings.filter(f => f.severity === 'critical').length} critical, ${findings.filter(f => f.severity === 'major').length} major); parity smoke: ${v1 && v1.parity_smoke}`)

phase('Fix')
let fix = null, v2 = null
if (findings.length) {
  fix = await agent(`${COMMON}

Fix every finding below from the Phase 5 verifier, in severity order. For each, either fix it (with a test that would have caught it) or, if you can show it is not a real difference, say why with evidence. Parity-smoke failures that aren't listed as findings are Phase 6's; don't chase them here. Commit the fixes and rebuild campfire-fsharp:app.
FINDINGS: ${JSON.stringify(findings)}
Return the structured result; list any finding you did not fix under deferred with the reason.`, { label: 'fix:phase5', phase: 'Fix', schema: BUILD_RESULT, model: 'sonnet', effort: 'high' })
  v2 = await agent(`${COMMON}

Re-verify Phase 5 after fixes. Do not modify source files. For each original finding, confirm it is fixed or still open. Run bin/verify with CAMPFIRE_REQUIRE_SEED=1, and re-run the same parity smoke the first verifier ran (see its report) and report the new counts. Report any NEW issue introduced by the fixes.
ORIGINAL FINDINGS: ${JSON.stringify(findings)}
FIRST PARITY SMOKE: ${v1 && v1.parity_smoke}
FIXER REPORT: ${JSON.stringify(fix)}`, { label: 'reverify:phase5', phase: 'Fix', schema: FINDINGS, model: 'opus', effort: 'high' })
}

return { u1, u2, u3, u4, v1, fix, v2 }
