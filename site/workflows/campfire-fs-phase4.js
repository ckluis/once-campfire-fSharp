export const meta = {
  name: 'campfire-fs-phase4',
  description: 'Phase 4 of the F# Campfire port: views (helpers, fragment cache, all templates) with a byte-for-byte differential against Rust',
  phases: [
    { title: 'Build', detail: 'port Rust crate views to F#: foundations + differential, hot-path templates, remaining templates' },
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
    templates_ported: { type: 'string', description: 'templates in scope vs ported, and any not ported by name' },
    differential: { type: 'string', description: 'byte-for-byte comparison of rendered output against the Rust views crate: how many renders, how many identical' },
    perf_notes: { type: 'string', description: 'recorded numbers only; no long tuning in this phase' },
    verify_passed: { type: 'boolean' },
    verify_output_tail: { type: 'string' },
    deferred: { type: 'array', items: { type: 'string' } },
    divergences: { type: 'array', items: { type: 'string' } },
  },
  required: ['summary', 'commits', 'rust_tests_in_scope', 'fsharp_tests_ported', 'templates_ported', 'differential', 'perf_notes', 'verify_passed', 'verify_output_tail', 'deferred', 'divergences'],
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
          evidence: { type: 'string' },
          fix: { type: 'string' },
        },
        required: ['severity', 'file', 'issue', 'evidence', 'fix'],
      },
    },
  },
  required: ['verify_passed', 'findings'],
}

const COMMON = `You are working in ${REPO}, an F# port of Basecamp's ONCE Campfire. Read AGENTS.md and plans/fsharp-port.md first and follow them exactly: translate from rust/ (the Rust port, pinned), use reference/ (Rails) as the oracle, never edit reference/, rust/ or vectors/ (and never write files into them, including from tests), keep the project dependency graph (Campfire.Views references only Routes, RailsCompat and Ruby, as rust/crates/views does), commit each logical unit on the current branch (port) with a clear message ending in a blank line and "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>", never push. Phases 1-3 are done (Ruby, RailsCompat, Routes, Assets, Db, RichText, Storage, Kit, Cable); reuse their types. Any Rust helper you build uses the shared CARGO_TARGET_DIR=target/rust (never a separate target dir), and you run bin/clean-rust-builds when done. Rendering must follow AGENTS.md's performance rules from the start (templates write UTF-8 straight into pooled buffers; static template text is precomputed UTF-8 bytes, not re-encoded per request; no intermediate strings, DOM or markup library). This phase records performance numbers but does not spend long tuning them: the whole-app baseline after Phase 5 decides what to optimize. bin/verify must pass before you finish; run it with CAMPFIRE_REQUIRE_SEED=1 at least once. Run Docker-based scripts inside colima: colima ssh -- bash -c 'cd ${REPO} && ...'. Run commands synchronously in the foreground; never background a long command and wait on it. Ask nothing; decide, and record decisions under divergences. Report honestly: anything not done goes under deferred with the reason.`

phase('Build')
const u1 = await agent(`${COMMON}

UNIT 4.1: Views foundations and the differential. Port to src/Campfire.Views (tests -> tests/Campfire.Views.Tests):
- The rendering core: design the F# template writer (pooled UTF-8 buffer, precomputed static byte segments, ERB/HTML escaping from Campfire.Ruby writing straight into the buffer, SafeString semantics so already-safe HTML isn't escaped twice). Document the pattern at the top of the module so units 4.2 and 4.3 follow it, one F# module per template at the same relative path as rust/crates/views/templates/ (which mirrors the ERB files).
- rust/crates/views/src/helpers/ (tag, forms, application, users, and the rest), fragment_cache.rs (keep its caching exactly: it is how Rust makes the benchmarked pages cheap, and Kit's page parts / IPageParts splice consume it), recorded.rs, and the view-model modules (messages.rs, messages/presentation.rs, rooms.rs, users.rs, accounts.rs and the rest of src/).
- The layouts and shared partials under templates/layouts and any partial used across areas.
- A byte-for-byte differential: a small Rust program (reference-tools/views/differential, built into target/rust) that uses rust/crates/views to render templates for given inputs, and an F# counterpart that renders the same inputs with Campfire.Views; bin/views-differential runs both over inputs built from the parity seeds (parity/.seed/*) and generated variations, and diffs the bytes. Wire in every template ported so far, and make it easy for the next units to add theirs.
- Port the Rust tests for everything above (#[cfg(test)] modules; the tests/ files that cover these modules).
Return the structured result.`, { label: 'build:views-foundations', phase: 'Build', schema: BUILD_RESULT, model: 'sonnet', effort: 'high' })
log(`Unit 4.1: verify=${u1 && u1.verify_passed}, tests ${u1 && u1.fsharp_tests_ported}/${u1 && u1.rust_tests_in_scope}; ${u1 && u1.differential}`)

const u2 = await agent(`${COMMON}

UNIT 4.2: the hot-path templates. Unit 4.1 built the template writer, helpers, fragment cache, layouts and bin/views-differential; follow its pattern exactly. Port every template under rust/crates/views/templates/ for rooms/, messages/, searches/, autocompletable/, and whatever renders the sidebar (find it: the benchmarked workloads are the room page, the messages page, the sidebar and search), plus any partials they use that 4.1 did not port. Add each to bin/views-differential and get it byte-identical against Rust on seed-derived and generated inputs (busy rooms, empty rooms, direct rooms, every message presentation: plain, mention, embed, attachment image/video/file, bot, edited, boosted). Port the Rust tests that cover them: tests/parity_a.rs, tests/messages_support, tests/rooms_views.rs and any #[cfg(test)] modules. Record (don't tune) the render time of a busy room page and a messages page against Rust's, using the differential programs.
Return the structured result.`, { label: 'build:views-hot', phase: 'Build', schema: BUILD_RESULT, model: 'sonnet', effort: 'high' })
log(`Unit 4.2: verify=${u2 && u2.verify_passed}, tests ${u2 && u2.fsharp_tests_ported}/${u2 && u2.rust_tests_in_scope}; ${u2 && u2.differential}`)

const u3 = await agent(`${COMMON}

UNIT 4.3: every remaining template. Units 4.1 and 4.2 built the writer, helpers, fragment cache, layouts, bin/views-differential and the rooms/messages/searches/sidebar templates; follow the same pattern. Port every template under rust/crates/views/templates/ not yet ported (accounts/, users/, sessions/, first_runs/, pwa/, welcome/ and anything else left; list the directory and check off each file), plus any JSON or SVG renderers rust/crates/views provides. Add each to bin/views-differential and get it byte-identical against Rust on seed-derived and generated inputs (admin vs member, custom styles, bots, push subscriptions, the PWA manifest and service worker). Port the remaining Rust tests in rust/crates/views. Finally confirm by listing: every file in rust/crates/views/templates has an F# module, and every Rust #[test] in the crate has an F# counterpart.
Return the structured result.`, { label: 'build:views-rest', phase: 'Build', schema: BUILD_RESULT, model: 'sonnet', effort: 'high' })
log(`Unit 4.3: verify=${u3 && u3.verify_passed}, tests ${u3 && u3.fsharp_tests_ported}/${u3 && u3.rust_tests_in_scope}; ${u3 && u3.differential}`)

phase('Verify')
const v1 = await agent(`${COMMON}

You are the adversarial verifier for Phase 4. Do not modify source files. Find every way src/Campfire.Views (and its tests and bin/views-differential) differs from rust/crates/views or from Rails (reference/app/views, helpers).
Builders reported:
FOUNDATIONS: ${JSON.stringify(u1)}
HOT PATH: ${JSON.stringify(u2)}
REST: ${JSON.stringify(u3)}
Check, at minimum:
- Coverage: every template file and every Rust #[test] in rust/crates/views has an F# counterpart; list any missing by name.
- The differential is real: run bin/views-differential yourself; confirm it compares raw bytes (not normalized), covers every template, and that its inputs reach the interesting branches (mentions, embeds, every attachment kind, bots, edited, boosts, admin vs member, custom styles, empty states). A differential that renders only trivial inputs is a finding.
- Escaping and XSS: every interpolation escapes as ERB does; SafeString/raw output only where Rust/Rails mark it safe; attribute vs text contexts; URLs in href/src.
- Fragment cache: keys, invalidation, and that cached fragments can't leak one user's view (admin controls, own-message actions, unread state) to another.
- Performance rules: no per-request re-encoding of static text, no intermediate strings or StringBuilder on render paths, pooled buffers returned.
- Run bin/verify with CAMPFIRE_REQUIRE_SEED=1 and report.
Report only real, evidenced findings. Severity: critical = wrong output users or security would see; major = missing template/test or differential gap; minor = style or citation gaps.`, { label: 'verify:phase4', phase: 'Verify', schema: FINDINGS, model: 'opus', effort: 'high' })
const findings = (v1 && v1.findings) || []
log(`Verifier: ${findings.length} findings (${findings.filter(f => f.severity === 'critical').length} critical, ${findings.filter(f => f.severity === 'major').length} major)`)

phase('Fix')
let fix = null, v2 = null
if (findings.length) {
  fix = await agent(`${COMMON}

Fix every finding below from the Phase 4 verifier, in severity order. For each, either fix it (with a test or differential input that would have caught it) or, if you can show it is not a real difference, say why with evidence. Commit the fixes.
FINDINGS: ${JSON.stringify(findings)}
Return the structured result; list any finding you did not fix under deferred with the reason.`, { label: 'fix:phase4', phase: 'Fix', schema: BUILD_RESULT, model: 'sonnet', effort: 'high' })
  v2 = await agent(`${COMMON}

Re-verify Phase 4 after fixes. Do not modify source files. For each original finding, confirm it is fixed or still open. Run bin/verify with CAMPFIRE_REQUIRE_SEED=1 and bin/views-differential. Report any NEW issue introduced by the fixes.
ORIGINAL FINDINGS: ${JSON.stringify(findings)}
FIXER REPORT: ${JSON.stringify(fix)}`, { label: 'reverify:phase4', phase: 'Fix', schema: FINDINGS, model: 'opus', effort: 'high' })
}

return { u1, u2, u3, v1, fix, v2 }
