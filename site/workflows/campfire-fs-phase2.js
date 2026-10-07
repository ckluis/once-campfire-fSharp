export const meta = {
  name: 'campfire-fs-phase2',
  description: 'Phase 2 of the F# Campfire port: Db, RichText, Storage; adversarial verify; fix',
  phases: [
    { title: 'Build', detail: 'port Rust crates db, richtext, storage to F# with tests' },
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
    vector_cases_checked: { type: 'string', description: 'per vector file or corpus: cases in file vs cases exercised' },
    seed_tests: { type: 'string', description: 'how many tests need the parity seed, and whether they ran with CAMPFIRE_REQUIRE_SEED=1' },
    verify_passed: { type: 'boolean' },
    verify_output_tail: { type: 'string' },
    deferred: { type: 'array', items: { type: 'string' }, description: 'anything in scope not done, with reason' },
    divergences: { type: 'array', items: { type: 'string' }, description: 'any deliberate difference from rust/ or reference/' },
  },
  required: ['summary', 'commits', 'rust_tests_in_scope', 'fsharp_tests_ported', 'seed_tests', 'verify_passed', 'verify_output_tail', 'deferred', 'divergences'],
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

const COMMON = `You are working in ${REPO}, an F# port of Basecamp's ONCE Campfire. Read AGENTS.md and plans/fsharp-port.md first and follow them exactly: translate from rust/ (the Rust port, pinned), use reference/ (Rails) as the oracle, never edit reference/, rust/ or vectors/, keep the project dependency graph (a project references only what its Rust crate depends on), commit each logical unit on the current branch (port) with a clear message ending in a blank line and "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>", never push. Phase 1 (Campfire.Ruby, Campfire.RailsCompat, Campfire.Routes, Campfire.Assets) is done and committed; read its code to reuse its types (Json.Value, Clock, Timestamp, MessageVerifier, SGIDs, Erb escaping) instead of duplicating them. The parity seeds are built in parity/.seed (default, crowd, custom_styles, first_run, restricted); tests that need a seed use Repo.seed from tests/Shared/Repo.fs, and you must run them with CAMPFIRE_REQUIRE_SEED=1 at least once and report it. bin/verify (Release build, warnings as errors, all tests) must pass before you finish. Run Docker-based scripts inside the colima VM: colima ssh -- bash -c 'cd ${REPO} && ...'. Run commands synchronously in the foreground; never background a long command and wait on it. Ask nothing; decide, and record decisions under divergences. Report honestly: anything not done goes under deferred with the reason.`

phase('Build')
const u1 = await agent(`${COMMON}

UNIT 2.1: port rust/crates/db to src/Campfire.Db (tests -> tests/Campfire.Db.Tests).
- database.rs: one writer that owns the write connection and takes a bounded queue of work, plus a reader pool, with the exact SQLite settings (immediate transactions, 5s busy timeout, foreign keys, WAL, memory mapping off, the index Rust adds at boot) per rust/README.md "Known differences" and reference/config/database.yml. Use Microsoft.Data.Sqlite with prepared statements reused per connection. The writer must not block the thread pool: use a dedicated thread or an async queue.
- schema.rs, fixtures.rs, every model under models/ (user, message, room, membership, push_subscription, account, and the rest), with Active Record callback side effects in the same order and transactions as Rust (see plans/rust-conversion.md "Persisted data").
- Timestamp format and precision exactly as Rails writes them to SQLite; JSON columns via RailsCompat.Json.
- Port every test: src/tests/*.rs (columns, user, message, callbacks, push, ...) and any #[cfg(test)] modules. Seed-dependent tests must actually run against parity/.seed/default (copy the seed DB to a temp dir per test; never write to parity/.seed).
- Also fix one leftover from Phase 1: the doc comment on Json.shortestDigits (src/Campfire.RailsCompat/Json.fs) and the test comment in tests/Campfire.RailsCompat.Tests/UnitTests.fs (around line 121) say the json gem takes the upper digit on exact ties. That is only verified for Rust's {:e}; reword them to say what was verified.
Return the structured result.`, { label: 'build:db', phase: 'Build', schema: BUILD_RESULT, model: 'sonnet', effort: 'high' })
log(`Unit 2.1 db: verify=${u1 && u1.verify_passed}, tests ${u1 && u1.fsharp_tests_ported}/${u1 && u1.rust_tests_in_scope}`)

const u2 = await agent(`${COMMON}

UNIT 2.2: port rust/crates/richtext to src/Campfire.RichText (tests -> tests/Campfire.RichText.Tests). This is the highest-risk area for security (plans/rust-conversion.md, "Rich text").
- Rust uses a vendored html5ever with Gumbo's parse limits, because Rails parses with Nokogiri's HTML5 (Gumbo). Use AngleSharp (add it to Directory.Packages.props) unless you find it cannot match; parse equivalence is decided by Rust's tests, not by assumption. Where AngleSharp's tree differs from html5ever/Gumbo on a case Rust tests, adapt (pre/post-processing, or a targeted fix) until the F# output matches; record each such adaptation under divergences.
- Port dom.rs, sanitizer.rs (all three layers: RemoveSoloUnfurledLinkText, SanitizeTags removing disallowed elements with contents, SanitizeAttributes, then Action Text's sanitizer with lib/rails_ext/action_text_allowed_tags.rb, then auto_link's re-sanitization), attachables.rs (SGIDs, the deliberate User-only invalid-signature fallback incl. Marshal-era payloads, and the rejection of tampered SGIDs for any other model), uri.rs, autolink.rs, content.rs, filters.rs, plain_text.rs, ruby.rs.
- Port every test: tests/corpus.rs (all corpus cases), tests/reference_tests.rs, tests/hardening.rs (independent security assertions: no script elements, no on* attributes, no javascript: URLs, no style outside the allowlist), plus #[cfg(test)] modules. Also exercise vectors/rails_compat.json "unverified_sgids" (23 cases), which Phase 1 deferred to this crate. Assert case counts.
Return the structured result.`, { label: 'build:richtext', phase: 'Build', schema: BUILD_RESULT, model: 'sonnet', effort: 'high' })
log(`Unit 2.2 richtext: verify=${u2 && u2.verify_passed}, tests ${u2 && u2.fsharp_tests_ported}/${u2 && u2.rust_tests_in_scope}`)

const u3 = await agent(`${COMMON}

UNIT 2.3: port rust/crates/storage to src/Campfire.Storage (tests -> tests/Campfire.Storage.Tests).
- Active Storage-compatible blobs, the disk service (storage/files/ab/cd/<key>), filename handling, marcel content-type detection, analysis (dimensions, duration, analyzed/identified metadata), variants via libvips, video posters via an ffmpeg subprocess with the same argv as Rails' VideoPreviewer, the Marshal subset behind variation digests, json.rs, file_server.rs, process.rs, and tables.rs (generated: reproduce it the way Phase 1 reproduced approximations.rs, with a generator mode in reference-tools and a checked-in F# file; don't hand-copy 3,000 lines).
- libvips: use NetVips (add it to Directory.Packages.props) over the system libvips, with the same operations, options and defaults Rust passes over FFI (vips.rs).
- Media vectors (vectors/storage.json, vectors/storage/): thumbnails, avatars and posters must be byte-identical to the reference's, which requires libvips 8.16.1 and ffmpeg 7.1.5 built as in rust/Dockerfile. Rust's tests skip byte comparisons on a version mismatch unless CAMPFIRE_REQUIRE_MEDIA_VECTORS=1. Do the same, and then make the comparisons actually run: add a Dockerfile.toolchain (or a stage in a new Dockerfile) that reuses rust/Dockerfile's vips and ffmpeg build stages (copy the stage definitions, don't reference rust/ at image build time) and adds the .NET 10 SDK, build it inside colima, and run the Storage tests in it with CAMPFIRE_REQUIRE_MEDIA_VECTORS=1. Report how many media byte comparisons ran and passed there.
- Port every test: tests/vectors.rs and #[cfg(test)] modules. Assert vector case counts.
Return the structured result.`, { label: 'build:storage', phase: 'Build', schema: BUILD_RESULT, model: 'sonnet', effort: 'high' })
log(`Unit 2.3 storage: verify=${u3 && u3.verify_passed}, tests ${u3 && u3.fsharp_tests_ported}/${u3 && u3.rust_tests_in_scope}`)

phase('Verify')
const v1 = await agent(`${COMMON}

You are the adversarial verifier for Phase 2. Do not modify source files. Find every way the F# code in src/Campfire.Db, src/Campfire.RichText and src/Campfire.Storage (and their tests) differs in behavior from rust/crates/{db,richtext,storage} or from Rails (reference/).
Builders reported:
DB: ${JSON.stringify(u1)}
RICHTEXT: ${JSON.stringify(u2)}
STORAGE: ${JSON.stringify(u3)}
Check, at minimum:
- Every Rust module and #[test] has an F# counterpart; list any missing by name. Seed tests really ran (run them yourself with CAMPFIRE_REQUIRE_SEED=1).
- Db: writer/reader topology and settings, transaction boundaries and callback order, timestamp precision/format written to SQLite, JSON columns, concurrency safety (no shared SqliteConnection across threads, no sync-over-async on the writer).
- RichText security: try to break the sanitizer with inputs Rust's hardening and corpus tests would reject (script, on* handlers, javascript:/data: URLs, style, nested/malformed markup, SVG/MathML namespace tricks, entity-encoded schemes), and any place AngleSharp's parse differs from html5ever/Gumbo that the builder adapted around. SGID fallback restricted to User only.
- Storage: variation digest (Marshal subset + SHA1) matches vectors for every variant; disk layout; media vectors really ran byte comparisons in the toolchain image (run them yourself in colima if the image exists).
- Tests that pass vacuously (empty loops, swallowed exceptions, skipped cases, seed absent).
- Run bin/verify yourself and report the result.
Report only real, evidenced findings. Severity: critical = wrong behavior users or security would see; major = missing port/test, or a vector/corpus case not exercised; minor = style or citation gaps.`, { label: 'verify:phase2', phase: 'Verify', schema: FINDINGS, model: 'opus', effort: 'high' })
const findings = (v1 && v1.findings) || []
log(`Verifier: ${findings.length} findings (${findings.filter(f => f.severity === 'critical').length} critical, ${findings.filter(f => f.severity === 'major').length} major)`)

phase('Fix')
let fix = null, v2 = null
if (findings.length) {
  fix = await agent(`${COMMON}

Fix every finding below from the Phase 2 verifier, in severity order. For each, either fix it (with a test that would have caught it) or, if you can show it is not a real difference, say why with evidence. Commit the fixes.
FINDINGS: ${JSON.stringify(findings)}
Return the structured result; list any finding you did not fix under deferred with the reason.`, { label: 'fix:phase2', phase: 'Fix', schema: BUILD_RESULT, model: 'sonnet', effort: 'high' })
  v2 = await agent(`${COMMON}

Re-verify Phase 2 after fixes. Do not modify source files. For each original finding, confirm it is fixed (read the code and the new test) or still open. Then run bin/verify, including seed tests with CAMPFIRE_REQUIRE_SEED=1. Also report any NEW issue introduced by the fixes.
ORIGINAL FINDINGS: ${JSON.stringify(findings)}
FIXER REPORT: ${JSON.stringify(fix)}`, { label: 'reverify:phase2', phase: 'Fix', schema: FINDINGS, model: 'opus', effort: 'high' })
}

return { u1, u2, u3, v1, fix, v2 }
