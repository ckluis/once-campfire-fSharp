export const meta = {
  name: 'campfire-fs-phase7',
  description: 'Phase 7 of the F# Campfire port: tune F# against Rust (harness fixes, database path, allocation/GC, crypto and logging, post path), then re-baseline with a skeptical review',
  phases: [
    { title: 'Harness', detail: 'trustworthy before/after measurement' },
    { title: 'Tune', detail: 'one target at a time, measured before and after, parity kept' },
    { title: 'Check', detail: 'correctness verifier, full re-baseline, skeptical review', model: 'opus' },
  ],
}

const REPO = '/Users/clank/Desktop/projects/once-campfire-fsharp'

const TUNE = {
  type: 'object',
  properties: {
    summary: { type: 'string' },
    commits: { type: 'array', items: { type: 'string' } },
    before_after: { type: 'string', description: 'markdown table: workload x concurrency, Rust req/s, F# before, F# after, ratio before/after, CPU us/req before/after; reps and spread' },
    results_file: { type: 'string' },
    parity_kept: { type: 'string', description: 'bin/verify result, views/kit/richtext differentials, and anything else run to prove behavior did not change' },
    deferred: { type: 'array', items: { type: 'string' } },
    next_best_target: { type: 'string', description: 'what the measurements now say is the largest remaining gap' },
  },
  required: ['summary', 'commits', 'before_after', 'results_file', 'parity_kept', 'deferred', 'next_best_target'],
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

const REVIEW = {
  type: 'object',
  properties: {
    numbers_trustworthy: { type: 'boolean' },
    reproduced: { type: 'string' },
    problems: { type: 'array', items: { type: 'string' } },
    final_table: { type: 'string', description: 'the HTTP table as you would publish it: Rails, Rust, F#, F#/Rust, for the five workloads at c=1/16/64, with CPU per request' },
    verdict: { type: 'string', description: 'plainly: where F# beats Rust, where it is close (within 10%), where it is behind, and what the remaining gaps are' },
  },
  required: ['numbers_trustworthy', 'reproduced', 'problems', 'final_table', 'verdict'],
}

const COMMON = `You are working in ${REPO}, an F# port of Basecamp's ONCE Campfire. Read AGENTS.md, plans/fsharp-port.md and bench/results/baseline-20261006.md (with its reviewed corrections) first. Phase 7 makes F# as fast as possible against the Rust port; beating Rust everywhere is the aim and getting close counts, reported honestly as close. Behavior must not change: bin/verify (with CAMPFIRE_REQUIRE_SEED=1), bin/views-differential, bin/kit-differential and bin/richtext-differential must still pass after every change, and decoded response bytes must stay identical to Rust's. Methodology is non-negotiable: same seed and pinned CPUs (SERVER_CPUS=0-3, LOADGEN_CPUS=4-7 in the colima VM), alternate app order, warm up long enough for the JIT, check status classes, rows written and response bytes, never manufacture a win; if a number looks too good, find out why before reporting it. Before every benchmark check free disk on the Mac (df -h /Users/clank) and stop if it is under 15 GB; run bin/clean-docker after image rebuilds and bin/clean-rust-builds after Rust builds. Rebuild campfire-fsharp:app from the current HEAD before measuring F# (record the image digest and the commit it was built from). Commit on branch port with messages ending in a blank line and "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"; never push; never edit reference/, rust/ or vectors/. Each optimization is committed with its before/after numbers in bench/results/phase7-<topic>.md. Docker and bench scripts run inside colima: colima ssh -- bash -c 'cd ${REPO} && ...'. Run commands synchronously in the foreground; never background a long command and wait on it. Ask nothing; decide and record why.`

phase('Harness')
const h = await agent(`${COMMON}

UNIT 7.0: make before/after measurements trustworthy, as the baseline review asked, before anyone optimizes.
1. Cap container logs in bench/run, bench/breakdown and parity/bin/candidate (docker --log-opt max-size and max-file, or the local driver with rotation) for every app: one Rust run wrote 9 GB of json logs and filled the Mac's disk. Logging stays on (it is part of the work both apps do); only its retention is capped.
2. Warm-up: each route at the measured concurrency for long enough that F#'s JIT has tiered up (the review found the first window 13-20% slow); make it a knob with a default that removes the artifact, applied identically to every app.
3. bench/quick: a fast before/after runner for rust and fsharp only (the five workloads at c=1 and c=16, 3 reps, alternating order, same validity checks and report format as bench/run), so each tuning unit can measure in minutes. Also a quick CPU-per-request mode (cgroup cpu.stat, unprofiled).
4. bench/breakdown: scale perf layer shares to unprofiled CPU per request, and attribute allocation the same way for both apps (Rust's allocator bucket vs F#'s CLR allocation helpers).
5. Rebuild campfire-fsharp:app from HEAD and record a fresh F# and Rust bench/quick as the Phase 7 starting point in bench/results/phase7-start.md.
Return the structured result (before_after = the starting point).`, { label: 'harness', phase: 'Harness', schema: TUNE, model: 'sonnet', effort: 'high' })
log(`7.0 start: ${h && h.before_after}`)

phase('Tune')
const units = [
  { key: 'database', prompt: `UNIT 7.1: the database path (review targets 1 and 2, the largest gap). F# spends ~26-36 us per page request in database access around SQLite's engine against Rust's ~6-7, and SQLite's engine itself does 1.5-2x Rust's work on sidebar and search. Go below Microsoft.Data.Sqlite on the hot paths: use SQLitePCLRaw's raw API (already a dependency) the way rusqlite drives SQLite: open connections with SQLITE_OPEN_NOMUTEX as rusqlite does, prepared statements cached per connection, bind by index, read columns by ordinal (text straight into UTF-8/pooled buffers where the views consume UTF-8), fewer API calls per row, and no thread hand-off for reads where Rust does none (check ReadQueue.TakeConnection contention). Compare the SQL each workload runs against Rust's (count statements and steps per request on sidebar and search) and remove any extra queries. Keep the writer topology and every transaction and callback order. Measure with bench/quick before and after.` },
  { key: 'runtime', prompt: `UNIT 7.2: allocation, GC and the thread pool (review targets 3, 6 and 9). Take an allocation-by-type trace of each workload first (dotnet-trace or EventPipe in the container) so the 100-161 KB per page request is attributed to code; then cut the largest sources on the hot paths (task/Bind state machines, lists, per-request strings and Guid.NewGuid's getrandom for request ids, Ctx/kit objects). Then tune the runtime: DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=0 (cut c=1 CPU 40-50% in one rep), gen0 size, server vs workstation GC and concurrency with 4 CPUs, TieredPGO/ReadyToRun choices; each knob measured on its own with bench/quick, kept only if it helps without hurting another workload, and set in the Dockerfile so it ships.` },
  { key: 'crypto-logging', prompt: `UNIT 7.3: signed cookies and the request log (review targets 4 and 7). HMAC and key derivation cost 15 vs 6 us on the room page and 30 vs 18 on the sidebar: cache derived keys per (secret, salt) and reuse HMAC state (no per-call EVP setup), and look at why the sidebar signs one avatar token per user (cache what Rust caches). The request log line costs F# 8-10 us more than Rust on sidebar and search: write a pre-formatted UTF-8 line in batches without the monitor-guarded queue contention in FrontResponse.Finish, keeping the line's content identical. Measure with bench/quick before and after.` },
  { key: 'post', prompt: `UNIT 7.4: posting a message and the remaining paths (review targets 5 and 8). F# uses 546 us of CPU per post against Rust's 381 (database 76 vs 18, CLR runtime 72 vs 13, rich text 23 vs 11), and loses at c=1 (0.67x) and c=64 (0.88x). Confirm and remove the default host's file-watching configuration (a File Watch thread at 3.8% plus lstat 2%), cut allocation and database work on the write path (the previous units' improvements may already apply; measure first), and look at rich text's per-post cost. Then identity bodies: F# 263 vs 171 us on the room page with Accept-Encoding: identity because Kestrel copies 4 KB blocks; find a way to write large bodies with fewer copies (e.g. response body writer with larger segments or SendFile-style paths) if it doesn't cost the gzip path. Measure with bench/quick (and its identity mode) before and after.` },
]
// Chris, 2026-10-06: Rust is a fixed target during tuning. Incremental measurements run F# only against
// the stored Rust numbers; Rust is re-measured only at the area-end re-baseline (the final step).
const RUST_RULE = `MEASUREMENT RULE (from Chris): do not re-run Rust while tuning. Compare F# against the stored Rust numbers in bench/results/phase7-start.md (taken with the same warm-up and harness). If bench/quick has no F#-only mode that reads those stored numbers, add one first (default to F#-only; an explicit flag re-measures Rust) and commit it. Label every intermediate ratio "vs stored Rust baseline". Rust is re-measured only in the final re-baseline at the end of this phase.

ITERATION TIERS (from Chris): iterate fast, then test fully once you are happy with the area.
- Tier 1 (seconds): in-process A/B micro-benchmarks of the function you are changing (BenchmarkDotNet or a Stopwatch harness, Release build) and the affected tests only. SageFs 0.6.904 is installed (~/.dotnet/tools/sagefs; needs export DOTNET_ROOT=$HOME/.dotnet; an F# live REPL/daemon with hot reload, live tests and an MCP server via `sagefs mcp`; start it with --owner-pid set to your shell's pid so it exits with you, and run `sagefs stop` before any tier 2 or tier 3 measurement so it adds no CPU noise) as an optional aid for this tier: try it, and report under deferred whether it saved time or got in the way. Its timings are for comparing variants only, never for reporting.
- Tier 2 (minutes): no image rebuild. dotnet publish the app (Release, the same publish settings as the Dockerfile) into a directory under target/, and run it in a container from the existing campfire-fsharp:app or toolchain image with that directory mounted over the app, same env, same seed copy, same pinned CPUs and warm-up as bench/quick; measure F# only against the stored Rust numbers. Add this as a bench/quick mode (e.g. --mounted DIR) if it doesn't exist, and check once that mounted and image-built runs give the same numbers.
- Progress log (Chris watches it live): after EVERY tier 2 or tier 3 run, append one JSON object per line to bench/results/phase7-log.jsonl and commit it with the change: {"time": ISO-8601 UTC, "unit": "database|runtime|crypto-logging|post", "tier": 2 or 3, "change": one short sentence, "commit": short sha, "build": "mounted" or the image digest, "kept": true/false, "workloads": {"room_show": {"c16": {"rps": n, "cpu_us": n}, "c1": {...}}, "messages_page": ..., "sidebar": ..., "search": ..., "post_message": ...}}. Ratios against Rust are computed from bench/results/phase7-start.md, so record F#'s raw numbers only. Log reverted experiments too, with kept=false. Then run python3 bench/progress/build.py, which rebuilds Chris's progress dashboard (target/phase7-dashboard/index.html) from the log; the dashboard is only ever rebuilt this way, once per logged run.
- Tier 3 (once per unit, when you are happy): rebuild campfire-fsharp:app from HEAD, bench/quick F#-only from the image, bin/verify with CAMPFIRE_REQUIRE_SEED=1 and all three differentials. That is the unit's reported before/after.`
const tuned = []
for (const u of units) {
  const prior = tuned.map(t => `${t.key}: ${t.r && t.r.summary}`).join('\n')
  const r = await agent(`${COMMON}

${u.prompt}

${RUST_RULE}
Earlier units this phase (build on them, don't redo them):
HARNESS: ${h && h.summary}
${prior}
Return the structured result; next_best_target says what is now the largest remaining gap.`, { label: `tune:${u.key}`, phase: 'Tune', schema: TUNE, model: 'sonnet', effort: 'high' })
  tuned.push({ key: u.key, r })
  log(`7 ${u.key}: ${r && r.before_after}`)
}

phase('Check')
const v = await agent(`${COMMON}

You are the correctness verifier for Phase 7. Do not modify source files. Optimizations are where subtle behavior changes hide. Review every commit made in this phase (git log since the Phase 7 start recorded in bench/results/phase7-start.md) for: changed SQL results or ordering, transaction and callback order on writes, connection thread-safety after SQLITE_OPEN_NOMUTEX (one connection never used by two threads at once), pooled buffers returned twice or used after return, cached keys or HMAC state shared unsafely across threads or across secrets, the log line's content, runtime knobs that change behavior (GC, thread pool) beyond speed, and anything a cache could leak between users. Run bin/verify with CAMPFIRE_REQUIRE_SEED=1 and all three differentials, and a parity smoke with the rebuilt image (parity/bin/candidate compare --matrix lean --engines chromium --viewports desktop --schemes light --breakpoints exclude on rooms/**, messages/**, search/**, users/**, realtime/**) compared with Phase 5's result (24 pass; failures only in the network layer) — any new layer failing is a finding.
UNITS: ${JSON.stringify(tuned.map(t => ({ key: t.key, summary: t.r && t.r.summary, commits: t.r && t.r.commits })))}`, { label: 'verify:phase7', phase: 'Check', schema: FINDINGS, model: 'opus', effort: 'high' })

let fix = null
if (v && v.findings && v.findings.length) {
  fix = await agent(`${COMMON}

Fix every finding from the Phase 7 correctness verifier, in severity order, each with a test that would have caught it, and re-measure with bench/quick any path a fix touches so the report's numbers stay true. Commit the fixes.
FINDINGS: ${JSON.stringify(v.findings)}`, { label: 'fix:phase7', phase: 'Check', schema: TUNE, model: 'sonnet', effort: 'high' })
}

const fin = await agent(`${COMMON}

The full Phase 7 re-baseline and skeptical review. Rebuild campfire-fsharp:app from HEAD. Run bench/run --apps reference,rust,fsharp --reps 3 with the capped logs and the long warm-up (all suites), and record bench/results/phase7-final.md next to bench/results/baseline-20261006.md with before/after columns. Then review it as the baseline was reviewed: assume it is wrong until shown otherwise; re-run at least room page and post a message for rust and fsharp yourself; check statuses, rows written, identical decoded bytes, CPU per request, load average, image provenance. Give the table as you would publish it and a plain verdict: where F# beats Rust, where it is within 10%, where it is behind, and why.
TUNING UNITS: ${JSON.stringify(tuned.map(t => ({ key: t.key, before_after: t.r && t.r.before_after })))}
VERIFIER: ${JSON.stringify(v)}
FIXER: ${JSON.stringify(fix && fix.summary)}`, { label: 'rebaseline+review', phase: 'Check', schema: REVIEW, model: 'opus', effort: 'high' })

return { h, tuned, v, fix, fin }
