// The HTML report: one section per state, failures first, each failing cell with expected,
// actual and diff images plus the DOM and accessibility diffs.
import fs from "node:fs"
import path from "node:path"
import type { AllowEntry } from "./allowlist.ts"
import type { CellComparison, Status } from "./compare.ts"

export interface ReportInfo {
  title: string
  expected: string
  actual: string
  startedAt: string
  durationMs: number
  unusedAllowlist: AllowEntry[]
}

export function summarize(results: CellComparison[]): Record<Status, number> {
  const counts: Record<Status, number> = { pass: 0, fail: 0, allowed: 0, error: 0 }
  for (const r of results) counts[r.status]++
  return counts
}

export function writeReport(runDir: string, name: string, results: CellComparison[], info: ReportInfo): string {
  const file = path.join(runDir, `${name}.html`)
  const rel = (p?: string) => (p ? path.relative(runDir, p).split(path.sep).map(encodeURIComponent).join("/") : "")
  const counts = summarize(results)
  const byState = new Map<string, CellComparison[]>()
  for (const r of results) {
    if (!byState.has(r.state)) byState.set(r.state, [])
    byState.get(r.state)!.push(r)
  }
  const rank: Record<Status, number> = { error: 0, fail: 1, allowed: 2, pass: 3 }
  const states = [...byState.entries()].sort(([a, ra], [b, rb]) => {
    const worst = (rs: CellComparison[]) => Math.min(...rs.map((r) => rank[r.status]))
    return worst(ra) - worst(rb) || a.localeCompare(b)
  })

  const flaky = results.filter((r) => r.flaky)
  const masked = [...new Map(results.filter((r) => r.masks).map((r) => [r.state, r.masks!])).entries()]
  const attemptsHtml = (r: CellComparison) => {
    if (!r.attempts) return ""
    const rows = r.attempts.map((a) => {
      const pixels = a.sizeMismatch ? `size ${a.sizeMismatch}` : a.differentPixels ? `${a.differentPixels} px differ` : "identical"
      const images = a.images ? ` · <a href="${rel(a.images.expected)}">expected</a> <a href="${rel(a.images.actual)}">actual</a>${a.images.diff ? ` <a href="${rel(a.images.diff)}">diff</a>` : ""}` : ""
      return `<li>attempt ${a.attempt}: ${a.status} (${esc(pixels)})${images}</li>`
    })
    return `<p class="muted">Server output identical, pixels differed: captured again (pixel flake policy).</p><ol class="attempts">${rows.join("")}</ol>`
  }

  const cellHtml = (r: CellComparison) => {
    const badge = r.flaky ? `<span class="badge pass">pass</span> <span class="badge flaky">flaky</span>` : `<span class="badge ${r.status}">${r.status}</span>`
    const head = `<summary>${badge} <code>${esc(r.cell)}</code> ${r.layers.filter((l) => !l.equal).map((l) => `<span class="layer">${l.layer}${l.allowed ? " (allowed)" : ""}</span>`).join(" ")}${r.attempts ? ` <span class="muted">${r.attempts.length} attempts</span>` : ""}</summary>`
    if (r.status === "pass") return `<details class="cell">${head}${attemptsHtml(r)}<p class="muted">identical (${r.layers.map((l) => l.layer).join(", ")})</p></details>`
    const body: string[] = [attemptsHtml(r)]
    if (r.error) body.push(`<pre class="error">${esc(r.error)}</pre>`)
    const pixels = r.layers.find((l) => l.layer === "pixels")
    if (pixels && !pixels.equal) {
      const note = pixels.pixels?.sizeMismatch ? `size ${pixels.pixels.sizeMismatch}` : `${pixels.pixels?.differentPixels} px differ of ${(pixels.pixels?.width ?? 0) * (pixels.pixels?.height ?? 0)}`
      body.push(`<p>${esc(note)}${pixels.allowed ? allowNote(pixels.allowed) : ""}</p><div class="images">
        <figure><figcaption>expected</figcaption><a href="${rel(r.expected.base + ".png")}"><img loading="lazy" src="${rel(r.expected.base + ".png")}"></a></figure>
        <figure><figcaption>actual</figcaption><a href="${rel(r.actual.base + ".png")}"><img loading="lazy" src="${rel(r.actual.base + ".png")}"></a></figure>
        ${r.diffImage ? `<figure><figcaption>diff</figcaption><a href="${rel(r.diffImage)}"><img loading="lazy" src="${rel(r.diffImage)}"></a></figure>` : ""}
      </div>`)
    }
    for (const layer of r.layers.filter((l) => l.text && !l.equal)) {
      body.push(`<h4>${layer.layer} <small>+${layer.text!.added} −${layer.text!.removed}</small>${layer.allowed ? allowNote(layer.allowed) : ""}</h4><pre class="diff">${colorDiff(layer.text!.hunks)}</pre>`)
    }
    for (const side of ["expected", "actual"] as const) {
      const meta = r[side].meta
      const noise = [...(meta?.pageErrors ?? []), ...(meta?.console ?? [])]
      if (noise.length) body.push(`<h4>${side} console</h4><pre>${esc(noise.join("\n"))}</pre>`)
    }
    return `<details class="cell" ${r.status === "fail" || r.status === "error" ? "open" : ""}>${head}${body.join("\n")}</details>`
  }

  const html = `<!DOCTYPE html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Parity report</title>
<style>
  :root { --bg: #fff; --fg: #1d1d1f; --muted: #6e6e73; --line: #d2d2d7; --pass: #1a7f37; --fail: #cf222e; --allowed: #9a6700; --error: #8250df; --add: #dafbe1; --del: #ffebe9; }
  @media (prefers-color-scheme: dark) { :root { --bg: #0d1117; --fg: #e6edf3; --muted: #8b949e; --line: #30363d; --add: #12361f; --del: #3d1519; } }
  body { font: 14px/1.45 system-ui, sans-serif; background: var(--bg); color: var(--fg); margin: 0 auto; padding: 16px; max-width: 1600px; }
  h1 { font-size: 20px; } h2 { font-size: 16px; margin: 24px 0 6px; border-top: 1px solid var(--line); padding-top: 12px; } h4 { margin: 12px 0 4px; }
  .summary span { margin-right: 12px; } .muted { color: var(--muted); }
  .badge { display: inline-block; min-width: 56px; text-align: center; border-radius: 4px; color: #fff; font-size: 12px; padding: 0 6px; }
  .badge.pass { background: var(--pass); } .badge.flaky { background: var(--allowed); } .badge.fail { background: var(--fail); } .badge.allowed { background: var(--allowed); } .badge.error { background: var(--error); }
  .layer { font-size: 12px; border: 1px solid var(--line); border-radius: 4px; padding: 0 4px; }
  details.cell { margin: 2px 0 2px 12px; } summary { cursor: pointer; }
  .images { display: grid; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); gap: 8px; }
  .images img { max-width: 100%; border: 1px solid var(--line); image-rendering: pixelated; }
  figure { margin: 0; } figcaption { color: var(--muted); font-size: 12px; }
  pre { overflow-x: auto; background: color-mix(in srgb, var(--fg) 5%, transparent); padding: 8px; font-size: 12px; }
  pre.error { color: var(--error); }
  .diff .add { background: var(--add); display: block; } .diff .del { background: var(--del); display: block; } .diff .hunk { color: var(--muted); display: block; } .diff .ctx { display: block; }
</style></head><body>
<h1>${esc(info.title)}</h1>
<p class="muted">expected <code>${esc(info.expected)}</code> · actual <code>${esc(info.actual)}</code> · ${esc(info.startedAt)} · ${(info.durationMs / 1000).toFixed(1)}s</p>
<p class="summary">${(Object.keys(counts) as Status[]).map((s) => `<span><span class="badge ${s}">${s}</span> ${counts[s]}</span>`).join("")} <span><span class="badge flaky">flaky</span> ${flaky.length}</span> <span class="muted">${results.length} cells in ${byState.size} states</span></p>
${flaky.length ? `<p class="muted">Flaky (passed on a later capture, pixels only): ${flaky.map((r) => `<a href="#${esc(r.state)}"><code>${esc(r.state)} @ ${esc(r.cell)}</code></a> (${r.attempts!.length} attempts)`).join(", ")}</p>` : ""}
${masked.length ? `<h3>Masks</h3><ul>${masked.map(([state, masks]) => `<li><code>${esc(state)}</code>: ${masks.map((m) => `<code>${esc(m)}</code>`).join(", ")}</li>`).join("")}</ul>` : ""}
${info.unusedAllowlist.length ? `<p class="muted">Unused allowlist entries: ${info.unusedAllowlist.map((e) => `<code>${esc(e.state)}</code> (${esc(e.owner)})`).join(", ")}</p>` : ""}
${states.map(([state, rs]) => {
    const c = summarize(rs)
    return `<h2 id="${esc(state)}">${esc(state)} <small class="muted">${(Object.keys(c) as Status[]).filter((s) => c[s]).map((s) => `${c[s]} ${s}`).join(", ")}</small></h2>\n${rs.sort((a, b) => rank[a.status] - rank[b.status] || a.cell.localeCompare(b.cell)).map(cellHtml).join("\n")}`
  }).join("\n")}
</body></html>
`
  fs.writeFileSync(file, html)
  fs.writeFileSync(path.join(runDir, `${name}.json`), JSON.stringify({ info: { ...info, counts, flaky: flaky.length, masks: Object.fromEntries(masked) }, results: results.map(({ expected, actual, ...r }) => ({ ...r, layers: r.layers.map(({ text, ...l }) => ({ ...l, added: text?.added, removed: text?.removed })) })) }, null, 2))
  return file
}

function allowNote(entry: AllowEntry): string {
  return ` <span class="muted">allowed: ${esc(entry.reason)} (${esc(entry.owner)})</span>`
}

function colorDiff(hunks: string): string {
  return hunks.split("\n").map((line) => {
    const cls = line.startsWith("+") ? "add" : line.startsWith("-") ? "del" : line.startsWith("@@") ? "hunk" : "ctx"
    return `<span class="${cls}">${esc(line) || " "}</span>`
  }).join("")
}

function esc(text: string): string {
  return text.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]!)
}
