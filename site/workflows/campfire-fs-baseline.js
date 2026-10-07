export const meta = {
  name: 'campfire-fs-baseline',
  description: 'Phase 5b: whole-app baseline, Rails vs Rust vs F# on bench/run, with a per-layer cost breakdown and a skeptical review',
  phases: [
    { title: 'Bench', detail: 'wire F# into bench/run, run 3 reps, per-layer breakdown' },
    { title: 'Review', detail: 'skeptical validation of every number', model: 'opus' },
  ],
}

const REPO = '/Users/clank/Desktop/projects/once-campfire-fsharp'

const BENCH = {
  type: 'object',
  properties: {
    summary: { type: 'string' },
    commits: { type: 'array', items: { type: 'string' } },
    results_file: { type: 'string' },
    http_table: { type: 'string', description: 'markdown: the five workloads at c=16 (and c=1, c=64 if run), median req/s and spread for Rails, Rust, F#, and F#/Rust ratio' },
    other_suites: { type: 'string', description: 'cable fan-out, uploads, cold start, idle and peak memory, if run' },
    validity: { type: 'string', description: 'how each number was checked: status codes, rows written for posts, response bytes per app, load average before each run, CPU pinning' },
    breakdown: { type: 'string', description: 'where F# spends a request on each workload, by layer (Kestrel, Falco routing, Kit adapter, auth/cookies, SQLite, rich text, templates/fragment cache, gzip splice, GC), with the method used' },
    targets: { type: 'string', description: 'ranked list of the gaps worth closing, with estimated gain each' },
    deferred: { type: 'array', items: { type: 'string' } },
  },
  required: ['summary', 'commits', 'results_file', 'http_table', 'validity', 'breakdown', 'targets', 'deferred'],
}

const REVIEW = {
  type: 'object',
  properties: {
    numbers_trustworthy: { type: 'boolean' },
    reproduced: { type: 'string', description: 'which workloads you re-ran and what you got' },
    problems: { type: 'array', items: { type: 'string' } },
    corrected_table: { type: 'string', description: 'the HTTP table as you would publish it, with any correction' },
    phase7_targets: { type: 'string', description: 'ranked, evidence-backed list of what to optimize first' },
  },
  required: ['numbers_trustworthy', 'reproduced', 'problems', 'corrected_table', 'phase7_targets'],
}

const COMMON = `You are working in ${REPO}, an F# port of Basecamp's ONCE Campfire. Read AGENTS.md and plans/fsharp-port.md first. The port is functionally complete through Phase 5: the image campfire-fsharp:app is a drop-in for the reference image; campfire-rust:app (the Rust port) and campfire-reference:app (Rails) are built in colima. Commit on branch port with messages ending in a blank line and "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>", never push, never edit reference/, rust/ or vectors/. The methodology is non-negotiable: same machine, same seed (parity/.seed/default), one app at a time with alternating order, pinned CPUs, load average below the threshold before each run, and every number checked (HTTP status of every response class, rows actually written for posts, response bytes per app). Never manufacture a win or a loss: if a number looks off, find out why before reporting it. Rust builds use CARGO_TARGET_DIR=target/rust; run bin/clean-rust-builds and bin/clean-docker when done. Docker and bench scripts run inside colima: colima ssh -- bash -c 'cd ${REPO} && ...'. The VM has 8 CPUs: use SERVER_CPUS=0-3 and LOADGEN_CPUS=4-7 (four hardware threads per app, as the upstream README measures). Run commands synchronously in the foreground; never background a long command and wait on it. Ask nothing.`

phase('Bench')
const b = await agent(`${COMMON}

TASK: the whole-app baseline (plan step 5b).
1. Teach bench/run an "fsharp" app (FSHARP_IMAGE=campfire-fsharp:app), run exactly like the rust app (same env, ports, CPU pinning, storage layout, --network host), and an "fsharp-identity" twin like rust-identity. Keep reference and rust behavior unchanged. Point the loadgen build at target/rust (bench/run uses target/bench) and build it without installing Rust in the VM: run cargo inside a rust:1.98.1-trixie container with the repo and target/rust mounted, producing a Linux binary bench/run can execute in the VM.
2. Run bench/run --apps reference,rust,fsharp --reps 3 (all suites it supports: http, cable, upload), then bench/report on the results directory. If the full Rails run makes this impractical, run reference with fewer reps and say so. Check validity yourself: every response's status class, rows written vs posts sent for "post a message", response bytes per app per workload (note where F# and Rust differ in size, since a smaller response is not a fair win), and the load average recorded before each run.
3. Per-layer breakdown for F# on each of the five workloads: measure where a request's time goes (Kestrel accept/parse/write, Falco routing, Kit adapter and Ctx, cookie/session verification, authentication, SQLite reads/writes, rich text, templates and the fragment cache, gzip splice, GC pauses). Use dotnet-counters/dotnet-trace in the container, or a temporary in-process timing build, and compare with what rust/plans/perf-attribution.md and rust/bench/results say about Rust's costs. Say plainly what the Falco layer costs.
4. Write bench/results/baseline-<date>.md with the tables, the validity checks, the breakdown and a ranked list of targets, and commit it with the bench/run changes.
Return the structured result.`, { label: 'bench:baseline', phase: 'Bench', schema: BENCH, model: 'sonnet', effort: 'high' })
log(`Baseline: ${b && b.http_table}`)

phase('Review')
const r = await agent(`${COMMON}

You are the skeptical reviewer of the baseline below. Do not modify source files; you may run benchmarks. Assume the numbers are wrong until shown otherwise.
BASELINE REPORT: ${JSON.stringify(b)}
Check: the bench/run changes treat F# and Rust identically (env, CPUs, network, seed, warmup, user agent, Accept-Encoding); every workload hits the intended route with a valid session and returns 2xx/expected 3xx (no 401/302/404 masquerading as throughput); posts wrote one row each on every app; response sizes per app are comparable (F# should now render the same pages as Rust; flag any size gap); CPU pinning and load averages were honored; reps agree. Re-run at least two workloads (one page, plus post a message) for rust and fsharp yourself and compare. Check the breakdown's method is sound (no profiler overhead counted as app cost). Then give the table as you would publish it and a ranked, evidence-backed list of Phase 7 targets.
Return the structured result.`, { label: 'review:baseline', phase: 'Review', schema: REVIEW, model: 'opus', effort: 'high' })

return { b, r }
