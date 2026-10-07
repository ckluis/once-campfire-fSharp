export const meta = {
  name: 'campfire-fs-phase6',
  description: 'Phase 6 parity loop: triage F#-vs-Rails differences against Rust-vs-Rails, fix by cause, re-compare, verify no perf regression',
  phases: [
    { title: 'Triage', detail: 'lean compare F# and Rust against Rails, bucket F#-only failures by cause', model: 'opus' },
    { title: 'Fix', detail: 'one bucket at a time, re-compare its states' },
    { title: 'Verify', detail: 'full lean compare, tests, differentials, perf sanity', model: 'opus' },
  ],
}

const REPO = '/Users/clank/Desktop/projects/once-campfire-fsharp'

const TRIAGE = {
  type: 'object',
  properties: {
    summary: { type: 'string' },
    fsharp_counts: { type: 'string', description: 'F# vs Rails: cells pass/fail/allowed/error per seed' },
    rust_counts: { type: 'string', description: 'Rust vs Rails on the same states: pass/fail/allowed/error per seed' },
    buckets: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          key: { type: 'string' },
          cause: { type: 'string', description: 'root cause, with the Rust/Rails source that shows the expected behavior' },
          layer: { type: 'string' },
          states: { type: 'array', items: { type: 'string' } },
          cells: { type: 'integer' },
          difficulty: { type: 'string', enum: ['easy', 'hard'] },
          fix_plan: { type: 'string' },
        },
        required: ['key', 'cause', 'layer', 'states', 'cells', 'difficulty', 'fix_plan'],
      },
    },
  },
  required: ['summary', 'fsharp_counts', 'rust_counts', 'buckets'],
}

const FIX = {
  type: 'object',
  properties: {
    summary: { type: 'string' },
    commits: { type: 'array', items: { type: 'string' } },
    states_now: { type: 'string', description: "the bucket's states re-compared: pass/fail before and after" },
    tests: { type: 'string' },
    deferred: { type: 'array', items: { type: 'string' } },
  },
  required: ['summary', 'commits', 'states_now', 'tests', 'deferred'],
}

const VERIFY = {
  type: 'object',
  properties: {
    fsharp_counts: { type: 'string' },
    rust_counts: { type: 'string' },
    fsharp_only_failures: { type: 'array', items: { type: 'string' }, description: 'every cell F# fails that Rust passes, with its cause' },
    regressions: { type: 'array', items: { type: 'string' } },
    perf_check: { type: 'string', description: 'tier 2 room page and post at c=16 vs the phase7-final F# medians' },
    verify_passed: { type: 'boolean' },
  },
  required: ['fsharp_counts', 'rust_counts', 'fsharp_only_failures', 'regressions', 'perf_check', 'verify_passed'],
}

const COMMON = `You are working in ${REPO}, an F# port of Basecamp's ONCE Campfire. Read AGENTS.md and plans/fsharp-port.md first. Phase 7 just finished (bench/results/phase7-final.md: F# beats Rust 1.22-1.33x under load). This is Phase 6, the parity loop: F# must be indistinguishable from Rails wherever the Rust port is (rust/ adopted Known differences are ours too; parity/allowlist.yml lists the accepted exceptions). The goal: no cell that F# fails and Rust passes. Run parity inside colima: colima ssh -- bash -c 'mkdir -p /tmp/parity-net && cd ${REPO} && PARITY_NET_DIR=/tmp/parity-net parity/bin/candidate ...' (PARITY_CANDIDATE_APP=rust selects the Rust image for comparison runs; the default is campfire-fsharp:app; rebuild the F# image from HEAD after code changes with parity/bin/candidate build). Use --matrix lean --engines chromium --viewports desktop --schemes light --breakpoints exclude unless told otherwise; --only for subsets. Never edit reference/, rust/ or vectors/; don't weaken parity/allowlist.yml or masks to make F# pass unless Rust's own allowlist already covers the same difference. Behaviour and performance must hold: bin/verify (CAMPFIRE_REQUIRE_SEED=1) and the three differentials pass; performance is not to regress. Commit on branch port, messages ending in a blank line and "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"; never push. Check df -h /Users/clank before heavy runs (stop under 15 GB) and run bin/clean-docker after image rebuilds. Run commands synchronously in the foreground. Ask nothing. Time-box: this whole phase has about 2.5 hours; keep moving.`

phase('Triage')
const t = await agent(`${COMMON}

TRIAGE (about 40 minutes). Run the lean compare for F# and for Rust against Rails on every state of every seed (default, crowd, first_run, custom_styles, restricted; per seed with --seed NAME), Chromium desktop light. Then bucket every cell F# fails that Rust passes by root cause (read the network/server/DOM diffs in parity/out/*/report.json). Phase 5's smoke found the F#-only differences almost entirely in the network layer: the _campfire_session Set-Cookie, the digests of the three overridden JS files, last_room, and the PWA manifest escaping; confirm or correct that. For each bucket give the cause with the Rust or Rails source that shows the expected behavior, the states, the cell count, difficulty (easy/hard) and a fix plan. Order buckets by cells affected.`, { label: 'triage', phase: 'Triage', schema: TRIAGE, model: 'opus', effort: 'high' })
log(`Triage: F# ${t && t.fsharp_counts} | Rust ${t && t.rust_counts} | ${t && t.buckets ? t.buckets.length : 0} buckets`)

phase('Fix')
const fixes = []
for (const b of ((t && t.buckets) || []).slice(0, 5)) {
  const r = await agent(`${COMMON}

FIX one parity bucket (about 25 minutes for an easy one, 45 for a hard one). Fix the cause in F# to match what Rust does (read rust/ and reference/), add a test that would have caught it, rebuild the image, and re-compare the bucket's states for F# (before/after counts). Don't touch what other buckets own.
BUCKET: ${JSON.stringify(b)}
EARLIER FIXES THIS PHASE: ${JSON.stringify(fixes.map(f => ({ key: f.key, summary: f.r && f.r.summary })))}`, { label: `fix:${b.key}`, phase: 'Fix', schema: FIX, model: b.difficulty === 'hard' ? 'opus' : 'sonnet', effort: 'high' })
  fixes.push({ key: b.key, r })
  log(`fixed ${b.key}: ${r && r.states_now}`)
}
if (t && t.buckets && t.buckets.length > 5) log(`Buckets beyond the first five were not attempted this phase: ${t.buckets.slice(5).map(b => b.key).join(', ')}`)

phase('Verify')
const v = await agent(`${COMMON}

VERIFY Phase 6 (do not modify source files). Rebuild the F# image from HEAD. Re-run the lean compare for F# against Rails on every state of every seed and compare with the triage's Rust counts; list every cell F# still fails that Rust passes, with its cause, and any cell that passed before and fails now (regressions). Run bin/verify with CAMPFIRE_REQUIRE_SEED=1 and the three differentials. Then a performance sanity check: a tier 2 F#-only bench/quick on the room page and post a message at c=16 (one rep, the long warm-up) against the phase7-final F# medians (room 40,422 / post 8,905 req/s; within noise is fine, a drop beyond 5% is a regression).
TRIAGE: ${JSON.stringify(t)}
FIXES: ${JSON.stringify(fixes.map(f => ({ key: f.key, r: f.r })))}`, { label: 'verify:phase6', phase: 'Verify', schema: VERIFY, model: 'opus', effort: 'high' })

return { t, fixes, v }
