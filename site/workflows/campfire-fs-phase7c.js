export const meta = {
  name: 'campfire-fs-phase7c',
  description: 'Phase 7c: one time-boxed tuning unit on thread-pool growth, c=1 spinning and p99 GC tails; then a correctness check',
  phases: [
    { title: 'Tune', detail: 'thread pool and GC tail, F#-only against the phase7-final Rust medians' },
    { title: 'Check', detail: 'correctness and parity smoke', model: 'opus' },
  ],
}

const REPO = '/Users/clank/Desktop/projects/once-campfire-fsharp'
const RESULT = {
  type: 'object',
  properties: {
    summary: { type: 'string' },
    commits: { type: 'array', items: { type: 'string' } },
    before_after: { type: 'string' },
    deferred: { type: 'array', items: { type: 'string' } },
  },
  required: ['summary', 'commits', 'before_after', 'deferred'],
}
const CHECK = {
  type: 'object',
  properties: { verify_passed: { type: 'boolean' }, findings: { type: 'array', items: { type: 'string' } }, parity: { type: 'string' } },
  required: ['verify_passed', 'findings', 'parity'],
}

const COMMON = `You are working in ${REPO}, an F# port of Basecamp's ONCE Campfire. Read AGENTS.md (Phase 7 measuring section), bench/results/phase7-final.md and its review notes first. Phase 7 and Phase 6 are done: F# beats Rust 1.22-1.33x under load and matches Rust on all 214 parity cells. This is a last, hard time-boxed unit before publishing at about 05:30 EDT; it is now about 03:45 EDT. Same rules as Phase 7: matched work only (no caching Rust doesn't do except pure deterministic results), behaviour unchanged (bin/verify with CAMPFIRE_REQUIRE_SEED=1 and the three differentials), F# measured alone against the stored Rust medians in bench/results/phase7-final/ (use bench/quick with --mounted for tier 2, the image for tier 3), every tier 2/3 run logged with bench/quick log (which stamps the build's commit), never manufacture a win. Commit on branch port, messages ending in a blank line and "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"; never push. Docker and bench run inside colima; check df -h /Users/clank first (stop under 15 GB); bin/clean-docker after image rebuilds. Synchronous foreground commands only. Ask nothing.`

phase('Tune')
const u = await agent(`${COMMON}

UNIT 7.5 (hard stop by 05:00 EDT including its tier 3; if a change isn't proven by then, revert it). Targets, in the reviewer's order:
1. Thread-pool injection: after about 80 s under load the CLR grows the pool from 38 to 43-46 threads and the room page's CPU per request rises from 93 to 100 us (about 6%). Try fixing min/max threads to what the 4 CPUs need (ThreadPool.SetMinThreads/SetMaxThreads or the runtimeconfig knobs), measured with a run long enough to cross 80 s.
2. Spinning at c=1 (1.2-2x Rust's CPU per request): the spin-limit knob cost c=16 before; look for a change that cuts c=1 CPU without hurting c=16 (e.g. fewer worker threads, DOTNET_ThreadPool_UsePortableThreadPool settings, the processor count the runtime sees).
3. p99 at c=16/64 (1.2-3.2x Rust's, drifting up over minutes): measure GC pause counts and lengths (dotnet-counters in the container) and try GC settings (concurrent off, conserve memory, gen0 budget) that cut tail pauses without costing throughput.
Keep a change only if it helps its target and costs no workload more than noise at c=16. Finish with a tier 3: rebuild the image from HEAD, bench/quick F#-only on all five workloads at c=1 and c=16 (3 reps), compared with phase7-final's F# and Rust medians, and include p99 in the before/after.`, { label: 'tune:7.5-threads-gc', phase: 'Tune', schema: RESULT, model: 'opus', effort: 'high' })
log(`7.5: ${u && u.before_after}`)

phase('Check')
const c = await agent(`${COMMON}

Correctness check of unit 7.5 (do not modify source files). Review its commits (since a441ef2) for behaviour changes; run bin/verify with CAMPFIRE_REQUIRE_SEED=1 and the three differentials; run one parity compare on the default seed (lean, chromium, desktop, light, no breakpoints) with the rebuilt image and confirm it still matches Phase 6 (191 pass, 1 allowed on default). Report findings.
UNIT 7.5: ${JSON.stringify(u)}`, { label: 'check:7.5', phase: 'Check', schema: CHECK, model: 'opus', effort: 'high' })

return { u, c }
