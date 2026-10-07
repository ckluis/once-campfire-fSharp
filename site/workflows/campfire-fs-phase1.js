export const meta = {
  name: 'campfire-fs-phase1',
  description: 'Phase 1 of the F# Campfire port: Ruby/RailsCompat/Routes and Assets, adversarial verify, fix',
  phases: [
    { title: 'Build', detail: 'port Rust crates ruby, rails_compat, routes, assets to F# with tests' },
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
    vector_cases_checked: { type: 'string', description: 'per vector file: cases in file vs cases exercised' },
    verify_passed: { type: 'boolean' },
    verify_output_tail: { type: 'string' },
    deferred: { type: 'array', items: { type: 'string' }, description: 'anything in scope not done, with reason' },
    divergences: { type: 'array', items: { type: 'string' }, description: 'any deliberate difference from rust/ or reference/' },
  },
  required: ['summary', 'commits', 'rust_tests_in_scope', 'fsharp_tests_ported', 'verify_passed', 'verify_output_tail', 'deferred', 'divergences'],
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

const COMMON = `You are working in ${REPO}, an F# port of Basecamp's ONCE Campfire. Read AGENTS.md and plans/fsharp-port.md first and follow them exactly: translate from rust/ (the Rust port, pinned), use reference/ (Rails) as the oracle, never edit reference/, rust/ or vectors/, keep the project dependency graph, commit each logical unit on the current branch (port) with a clear message ending in a blank line and "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>", never push. bin/verify (Release build, warnings as errors, all tests) must pass before you finish. Run Docker-based scripts inside the colima VM: colima ssh -- bash -c 'cd ${REPO} && ...'. Run commands synchronously in the foreground; do not background long commands and wait on them. Report honestly: if something is not done, list it under deferred with the reason.`

phase('Build')
const u1 = await agent(`${COMMON}

UNIT 1.1: port three Rust crates to F#, with every one of their tests.
- rust/crates/ruby -> src/Campfire.Ruby (tests -> tests/Campfire.Ruby.Tests)
- rust/crates/rails_compat -> src/Campfire.RailsCompat (tests -> tests/Campfire.RailsCompat.Tests)
- rust/crates/routes -> src/Campfire.Routes (tests -> tests/Campfire.Routes.Tests). Check whether Rust generates anything at build time and reproduce that faithfully (an MSBuild step or a checked-in generated file with the generator script).
Requirements:
1. Port every module, preserving function boundaries and names (F# casing). Cite the Rust source file at the top of each F# module. Replace the placeholder Library.fs files.
2. Port every Rust test: #[cfg(test)] modules inside src files and the tests/ directory. Count Rust #[test] functions in scope and F# tests you wrote.
3. Golden vectors: vectors/ruby_core.json, vectors/rails_compat.json and vectors/campfire_routes.json (and any other vector file these crates read). Every case in each file must be exercised by an F# test; assert the case count so a silently skipped case fails.
4. Rails cross-check: Rust's rails_compat tests write target/rails_compat_rust_output.json, which reference-tools/rails_compat_verify_rust.rb verifies inside the reference container (see vectors/README.md). Make an F# test emit the same file with the same shape (cookies/messages F# generated), then run reference-tools/run.sh reference-tools/rails_compat_verify_rust.rb inside colima and report whether Rails accepted everything. The image campfire-reference:app exists; if run.sh expects campfire-reference, check the tags with docker images inside colima.
5. Security-sensitive code (signature checks, AES-GCM, PBKDF2, bcrypt) uses System.Security.Cryptography and BCrypt.Net-Next, with constant-time comparisons wherever Rust uses them.
6. The Clock must be injectable as in Rust (frozen time for tests and parity).
Return the structured result.`, { label: 'build:ruby+rails_compat+routes', phase: 'Build', schema: BUILD_RESULT, model: 'sonnet', effort: 'high' })
log(`Unit 1.1: verify=${u1 && u1.verify_passed}, tests ${u1 && u1.fsharp_tests_ported}/${u1 && u1.rust_tests_in_scope}`)

const u2 = await agent(`${COMMON}

UNIT 1.2: port rust/crates/assets to src/Campfire.Assets (tests -> tests/Campfire.Assets.Tests). Unit 1.1 (Ruby, RailsCompat, Routes) is already ported and committed; build on it.
Rust's assets crate digests and embeds the reference's assets and public/ at build time (build.rs, build/propshaft.rs, build/importmap.rs), vendors JS/CSS (vendor/), and layers port-owned overrides (overrides/, OVERRIDES.md) that shadow reference assets by logical path. Reproduce this exactly:
1. Propshaft-compatible digests and the compilers that rewrite content (CSS url() references, JS source maps and anything else build/propshaft.rs handles), so every digested logical path matches what the Rails app serves. Choose where it runs (an MSBuild step that generates a manifest plus embedded resources, or a startup scan); justify the choice by startup cost and Docker image packaging, and keep it deterministic.
2. Importmap generation identical to the reference's (build/importmap.rs).
3. Copy rust/crates/assets/vendor and overrides into the F# project (MIT, keep OVERRIDES.md) rather than referencing rust/ at runtime, because the Docker image must not depend on rust/. The reference/ assets may be read at build time, as Rust does.
4. serve.rs, helpers.rs, tags.rs: port them (serving with cache headers, helpers for asset paths and tags).
5. Port every test, including tests/reference.rs, and report Rust vs F# test counts.
Return the structured result.`, { label: 'build:assets', phase: 'Build', schema: BUILD_RESULT, model: 'sonnet', effort: 'high' })
log(`Unit 1.2: verify=${u2 && u2.verify_passed}, tests ${u2 && u2.fsharp_tests_ported}/${u2 && u2.rust_tests_in_scope}`)

phase('Verify')
const v1 = await agent(`${COMMON}

You are the adversarial verifier for Phase 1. Do not modify source files. Your job is to find every way the F# code in src/Campfire.Ruby, src/Campfire.RailsCompat, src/Campfire.Routes and src/Campfire.Assets (and their tests) differs from rust/crates/{ruby,rails_compat,routes,assets} or from Rails (reference/) in behavior.
Builders reported:
UNIT 1.1: ${JSON.stringify(u1)}
UNIT 1.2: ${JSON.stringify(u2)}
Check, at minimum:
- Every Rust module and #[test] has an F# counterpart; list any missing by name.
- Every case in vectors/ruby_core.json, rails_compat.json, campfire_routes.json is exercised, and the assertion compares the full value (not a prefix or a looser predicate).
- Ruby semantics: Float#to_s formatting (exponents, -0.0, rounding), String#to_i/to_f edge cases, ERB/CGI escaping, byte vs char handling (UTF-8), invariant culture.
- Crypto: PBKDF2 parameters, signing digest separate from derivation digest, envelope metadata and expiry, AES-256-GCM IV/tag layout, constant-time comparison, rejection paths (tampered, expired, wrong purpose, wrong secret, rotated secret).
- Assets: digests and rewritten content match what Rails serves (compare a sample of logical paths against the reference container if needed: colima ssh, campfire-reference:app), importmap byte-identical, overrides shadow correctly.
- Tests that pass vacuously (empty loops over vectors, swallowed exceptions, skipped cases).
- Run bin/verify yourself and report the result.
Report only real, evidenced findings. Severity: critical = wrong behavior users or security would see; major = missing port/test or vector case not exercised; minor = style or citation gaps.`, { label: 'verify:phase1', phase: 'Verify', schema: FINDINGS, model: 'opus', effort: 'high' })
const findings = (v1 && v1.findings) || []
log(`Verifier: ${findings.length} findings (${findings.filter(f => f.severity === 'critical').length} critical, ${findings.filter(f => f.severity === 'major').length} major)`)

phase('Fix')
let fix = null, v2 = null
if (findings.length) {
  fix = await agent(`${COMMON}

Fix every finding below from the Phase 1 verifier, in severity order. For each, either fix it (with a test that would have caught it) or, if you can show it is not a real difference, say why with evidence. Commit the fixes.
FINDINGS: ${JSON.stringify(findings)}
Return the structured result; list any finding you did not fix under deferred with the reason.`, { label: 'fix:phase1', phase: 'Fix', schema: BUILD_RESULT, model: 'sonnet', effort: 'high' })
  v2 = await agent(`${COMMON}

Re-verify Phase 1 after fixes. Do not modify source files. For each original finding, confirm it is fixed (read the code and the new test) or still open. Then run bin/verify. Also report any NEW issue introduced by the fixes.
ORIGINAL FINDINGS: ${JSON.stringify(findings)}
FIXER REPORT: ${JSON.stringify(fix)}`, { label: 'reverify:phase1', phase: 'Fix', schema: FINDINGS, model: 'opus', effort: 'high' })
}

return { u1, u2, v1, fix, v2 }
