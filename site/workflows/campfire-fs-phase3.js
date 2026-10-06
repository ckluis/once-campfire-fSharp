export const meta = {
  name: 'campfire-fs-phase3',
  description: 'Phase 3 of the F# Campfire port: Kit core, Kit front server, Cable; adversarial verify; fix',
  phases: [
    { title: 'Build', detail: 'port Rust crates kit (core + front) and cable to F# with tests' },
    { title: 'Verify', detail: 'adversarial review against rust/ and reference/', model: 'opus' },
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
    vector_cases_checked: { type: 'string', description: 'per vector file or golden set: cases in file vs cases exercised' },
    perf_notes: { type: 'string', description: 'hot-path design choices and any measurement vs Rust' },
    verify_passed: { type: 'boolean' },
    verify_output_tail: { type: 'string' },
    deferred: { type: 'array', items: { type: 'string' }, description: 'anything in scope not done, with reason' },
    divergences: { type: 'array', items: { type: 'string' }, description: 'any deliberate difference from rust/ or reference/' },
  },
  required: ['summary', 'commits', 'rust_tests_in_scope', 'fsharp_tests_ported', 'perf_notes', 'verify_passed', 'verify_output_tail', 'deferred', 'divergences'],
}

const FINDINGS = {
  type: 'object',
  properties: {
    verify_passed: { type: 'boolean' },
    findings: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          severity: { type: 'string', enum: ['critical', 'major', 'minor'] },
          file: { type: 'string' },
          issue: { type: 'string' },
          evidence: { type: 'string', description: 'the Rust/Ruby source or test that shows the expected behavior, and how F# differs' },
          fix: { type: 'string' },
        },
        required: ['severity', 'file', 'issue', 'evidence', 'fix'],
      },
    },
  },
  required: ['verify_passed', 'findings'],
}

const COMMON = `You are working in ${REPO}, an F# port of Basecamp's ONCE Campfire. Read AGENTS.md and plans/fsharp-port.md first and follow them exactly: translate from rust/ (the Rust port, pinned), use reference/ (Rails) as the oracle, never edit reference/, rust/ or vectors/, keep the project dependency graph (a project references only what its Rust crate depends on), commit each logical unit on the current branch (port) with a clear message ending in a blank line and "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>", never push. Phases 1-2 are done and committed (Campfire.Ruby, RailsCompat, Routes, Assets, Db, RichText, Storage); reuse their types rather than duplicating them. These projects sit on the hot path of the five benchmarked workloads (room page, messages page, sidebar, search, post a message) and of Cable fan-out, and the port must end up faster than Rust there: follow AGENTS.md's performance rules (no needless allocation, pooled buffers, no reflection serializers, no sync-over-async), and keep Rust's caching and precomputation. bin/verify (Release build, warnings as errors, all tests) must pass before you finish; run it with CAMPFIRE_REQUIRE_SEED=1 at least once. Run Docker-based scripts inside the colima VM: colima ssh -- bash -c 'cd ${REPO} && ...' (set PARITY_NET_DIR=/tmp/parity-net for page captures). Run commands synchronously in the foreground; never background a long command and wait on it. Ask nothing; decide, and record decisions under divergences. Adding a NuGet package needs a reason in divergences. Report honestly: anything not done goes under deferred with the reason.`

phase('Build')
const u1 = await agent(`${COMMON}

UNIT 3.1: Kit core. Port the request/response layer of rust/crates/kit to src/Campfire.Kit (tests -> tests/Campfire.Kit.Tests): ctx.rs (Ctx: request, params, cookies, session, current user hook, flash, format, clock), params.rs (Rails nested params across urlencoded, multipart and JSON, the _method override), cookies.rs, session and forgery protection (Rust's Sec-Fetch-Site CSRF policy from rust/README.md "Known differences", plus the Origin check), flash, format.rs (html, turbo_stream, json, svg by extension and Accept, Turbo Frame requests), body.rs (limits per README: 16 MiB non-upload bodies, 413), request.rs (proxy-derived host and remote IP, X-Forwarded-*), adapter.rs (the one adapter every route goes through, which builds Ctx and calls a plain action; controllers never touch HttpContext), responses, conditional GETs (ETag/304), HEAD, X-Version/X-Rev, gzip helpers, and anything else in kit/src outside front/ and deflater/. Hosting is ASP.NET Core Kestrel with Falco for routing (plans/fsharp-port.md D3); the adapter is where Falco/HttpContext stops. Port every Rust test outside front/deflater: kit/tests/http.rs, kit/tests/rails_vectors.rs (the vectors/rails_compat.json "csrf" section, deferred from Phase 1; assert case counts) and #[cfg(test)] modules.
Return the structured result.`, { label: 'build:kit-core', phase: 'Build', schema: BUILD_RESULT, model: 'sonnet', effort: 'high' })
log(`Unit 3.1 kit core: verify=${u1 && u1.verify_passed}, tests ${u1 && u1.fsharp_tests_ported}/${u1 && u1.rust_tests_in_scope}`)

const u2 = await agent(`${COMMON}

UNIT 3.2: Kit front server. Port rust/crates/kit/src/front/ and rust/crates/kit/src/deflater/ into src/Campfire.Kit (tests -> tests/Campfire.Kit.Tests). Unit 3.1 (Kit core) is done; build on it. The front server replaces Thruster in one process: HTTP on HTTP_PORT (80), and with TLS_DOMAIN, HTTPS on HTTPS_PORT (443) with Let's Encrypt certificates cached in /rails/storage/thruster in the format Thruster uses (read it so an existing install's certificates are reused), the app also on TARGET_PORT (3000, loopback unless TARGET_BIND), HTTP/2, request timeouts and body limits (HTTP_*_TIMEOUT, MAX_REQUEST_BODY and their THRUSTER_ forms), compression.rs, the response cache (cache.rs, CACHE_SIZE), handler.rs, acme.rs, and deflater/ including splice.rs (it splices gzip members so cached page parts are compressed once; this matters for the benchmarked pages, keep it). Use Kestrel for HTTP/1.1, HTTP/2 and TLS. For ACME choose a maintained .NET ACME client (or port Rust's acme.rs if no package fits) and justify it; the ACME directory must be configurable (ACME_DIRECTORY) so tests can point it at a local Pebble or a stub. Port every Rust test for these modules: kit/tests/front.rs and #[cfg(test)] modules under front/ and deflater/.
Return the structured result.`, { label: 'build:kit-front', phase: 'Build', schema: BUILD_RESULT, model: 'sonnet', effort: 'high' })
log(`Unit 3.2 kit front: verify=${u2 && u2.verify_passed}, tests ${u2 && u2.fsharp_tests_ported}/${u2 && u2.rust_tests_in_scope}`)

const u3 = await agent(`${COMMON}

UNIT 3.3: Cable. Port rust/crates/cable to src/Campfire.Cable (tests -> tests/Campfire.Cable.Tests): the Action Cable protocol (welcome, ping, confirm/reject, identifiers, perform, disconnect with a reconnect flag), connection.rs, server.rs, channel.rs, pubsub.rs (in-process pub/sub, one channel per stream; a lagging receiver is disconnected with reconnect: true, never silently skipped), turbo.rs (Turbo::StreamsChannel with signed stream names and Campfire's RoomStreamsAreAuthorized rejection), naming.rs, protocol.rs, and socket.rs (WebSocket framing with permessage-deflate without context takeover, compressing each broadcast once for all subscribers, per rust/README.md "Known differences"). Use ASP.NET Core's WebSocket upgrade; if its framing can't compress once per broadcast, take the raw upgraded stream and frame it yourself as Rust does. Respect the cable limits in the README (64 subscriptions per connection, 4 KiB identifiers, 1 MiB messages). Port every test: cable/tests/golden.rs (golden frame sequences; assert counts), cable/tests/protocol.rs, cable/tests/support, and #[cfg(test)] modules. Fan-out to many subscribers is a benchmarked suite (bench/run cable): measure fan-out to 1,000 local subscribers and report it under perf_notes.
Return the structured result.`, { label: 'build:cable', phase: 'Build', schema: BUILD_RESULT, model: 'sonnet', effort: 'high' })
log(`Unit 3.3 cable: verify=${u3 && u3.verify_passed}, tests ${u3 && u3.fsharp_tests_ported}/${u3 && u3.rust_tests_in_scope}`)

phase('Verify')
const v1 = await agent(`${COMMON}

You are the adversarial verifier for Phase 3. Do not modify source files. Find every way the F# code in src/Campfire.Kit and src/Campfire.Cable (and their tests) differs in behavior from rust/crates/{kit,cable} or from Rails (reference/).
Builders reported:
KIT CORE: ${JSON.stringify(u1)}
KIT FRONT: ${JSON.stringify(u2)}
CABLE: ${JSON.stringify(u3)}
Check, at minimum:
- Every Rust module and #[test] has an F# counterpart; list any missing by name.
- Security: CSRF (Sec-Fetch-Site + Origin rules, plain HTTP vs HTTPS behavior, the bot-key exemption hook), cookie flags (HttpOnly, SameSite=Lax, Secure over HTTPS, permanent expiry), session encryption, body limits and timeouts (slowloris-style slow clients), header injection, X-Forwarded-* trust only on TARGET_PORT as rust/README.md says, TLS/ACME certificate storage paths and permissions, Turbo signed-stream authorization and RoomStreamsAreAuthorized, Cable message/identifier limits.
- Params: nested-params edge cases (arrays of hashes, conflicting keys, malformed brackets, _method override only on POST) against Rust's tests and Rails' behavior.
- Front server: response cache keys and invalidation, gzip splice output decompresses to the same bytes, Vary/ETag handling, HTTP/2.
- Cable: golden frame sequences byte-for-byte, lag disconnect with reconnect: true, compression once per broadcast, revocation behavior.
- Hot-path performance smells: sync-over-async, per-request allocations Rust avoids, locks on the broadcast path.
- Tests that pass vacuously.
- Run bin/verify yourself with CAMPFIRE_REQUIRE_SEED=1 and report the result.
Report only real, evidenced findings. Severity: critical = wrong behavior users or security would see; major = missing port/test, or a golden case not exercised; minor = style or citation gaps.`, { label: 'verify:phase3', phase: 'Verify', schema: FINDINGS, model: 'opus', effort: 'high' })
const findings = (v1 && v1.findings) || []
log(`Verifier: ${findings.length} findings (${findings.filter(f => f.severity === 'critical').length} critical, ${findings.filter(f => f.severity === 'major').length} major)`)

phase('Fix')
let fix = null, v2 = null
if (findings.length) {
  fix = await agent(`${COMMON}

Fix every finding below from the Phase 3 verifier, in severity order. For each, either fix it (with a test that would have caught it) or, if you can show it is not a real difference, say why with evidence. Commit the fixes.
FINDINGS: ${JSON.stringify(findings)}
Return the structured result; list any finding you did not fix under deferred with the reason.`, { label: 'fix:phase3', phase: 'Fix', schema: BUILD_RESULT, model: 'sonnet', effort: 'high' })
  v2 = await agent(`${COMMON}

Re-verify Phase 3 after fixes. Do not modify source files. For each original finding, confirm it is fixed (read the code and the new test) or still open. Then run bin/verify with CAMPFIRE_REQUIRE_SEED=1. Also report any NEW issue introduced by the fixes.
ORIGINAL FINDINGS: ${JSON.stringify(findings)}
FIXER REPORT: ${JSON.stringify(fix)}`, { label: 'reverify:phase3', phase: 'Fix', schema: FINDINGS, model: 'opus', effort: 'high' })
}

return { u1, u2, u3, v1, fix, v2 }
