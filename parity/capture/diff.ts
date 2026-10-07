// Exact comparisons. Pixels: decode both PNGs to RGBA and compare the buffers byte for byte;
// pixelmatch (includeAA, threshold 0) only draws the diff image. Text: a Myers line diff.
import fs from "node:fs"
import { PNG } from "pngjs"
import pixelmatch from "pixelmatch"

export interface PixelResult {
  equal: boolean
  width?: number
  height?: number
  differentPixels?: number
  sizeMismatch?: string
}

export function comparePngs(expectedFile: string, actualFile: string, diffFile: string): PixelResult {
  const expected = PNG.sync.read(fs.readFileSync(expectedFile))
  const actual = PNG.sync.read(fs.readFileSync(actualFile))
  if (expected.width !== actual.width || expected.height !== actual.height) {
    return { equal: false, sizeMismatch: `${expected.width}x${expected.height} vs ${actual.width}x${actual.height}` }
  }
  const { width, height } = expected
  if (expected.data.equals(actual.data)) return { equal: true, width, height }
  const diff = new PNG({ width, height })
  const differentPixels = pixelmatch(expected.data, actual.data, diff.data, width, height, { threshold: 0, includeAA: true, alpha: 0.2 })
  fs.writeFileSync(diffFile, PNG.sync.write(diff))
  // pixelmatch with threshold 0 still ignores sub-threshold deltas of zero; any byte difference is
  // a failure even if it reports 0 pixels (e.g. a differing alpha on a transparent pixel).
  return { equal: false, width, height, differentPixels: Math.max(differentPixels, 1) }
}

export interface TextDiff {
  equal: boolean
  hunks: string // unified-style, with context
  added: number
  removed: number
}

export function diffText(expected: string, actual: string, context = 3, maxLines = 400): TextDiff {
  if (expected === actual) return { equal: true, hunks: "", added: 0, removed: 0 }
  const a = expected.split("\n")
  const b = actual.split("\n")
  const ops = myers(a, b)
  let added = 0, removed = 0
  for (const op of ops) {
    if (op.kind === "+") added++
    else if (op.kind === "-") removed++
  }
  return { equal: false, hunks: formatHunks(ops, context, maxLines), added, removed }
}

interface Op {
  kind: " " | "+" | "-"
  line: string
  aLine: number
  bLine: number
}

const MAX_EDIT_DISTANCE = 4000

// Myers' O(ND) diff, keeping only the live diagonals of each round for the backtrack.
function myers(a: string[], b: string[]): Op[] {
  // Trim common prefix and suffix first; most DOM diffs are small.
  let start = 0
  while (start < a.length && start < b.length && a[start] === b[start]) start++
  let endA = a.length, endB = b.length
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) { endA--; endB-- }
  const A = a.slice(start, endA), B = b.slice(start, endB)
  const n = A.length, m = B.length, max = n + m
  const offset = max + 1
  const v = new Int32Array(2 * max + 3)
  const trace: Int32Array[] = []
  let found = n === 0 && m === 0
  for (let d = 0; d <= Math.min(max, MAX_EDIT_DISTANCE) && !found; d++) {
    trace.push(v.slice(offset - d - 1, offset + d + 2)) // v[k] for k in [-d-1, d+1]
    for (let k = -d; k <= d; k += 2) {
      let x = k === -d || (k !== d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1
      let y = x - k
      while (x < n && y < m && A[x] === B[y]) { x++; y++ }
      v[offset + k] = x
      if (x >= n && y >= m) { found = true; break }
    }
  }
  if (!found) {
    // Too different to be worth aligning: report it as a replacement.
    const ops: Op[] = a.slice(0, start).map((line, i) => ({ kind: " " as const, line, aLine: i, bLine: i }))
    A.forEach((line, i) => ops.push({ kind: "-", line, aLine: start + i, bLine: start }))
    B.forEach((line, i) => ops.push({ kind: "+", line, aLine: endA, bLine: start + i }))
    for (let i = endA, j = endB; i < a.length; i++, j++) ops.push({ kind: " ", line: a[i], aLine: i, bLine: j })
    return ops
  }
  // Backtrack.
  const middle: Op[] = []
  let x = n, y = m
  for (let d = trace.length - 1; d >= 0 && (x > 0 || y > 0); d--) {
    const vd = trace[d]
    const at = (k: number) => vd[k + d + 1]
    const k = x - y
    const prevK = k === -d || (k !== d && at(k - 1) < at(k + 1)) ? k + 1 : k - 1
    const prevX = at(prevK)
    const prevY = prevX - prevK
    while (x > prevX && y > prevY) { x--; y--; middle.push({ kind: " ", line: A[x], aLine: start + x, bLine: start + y }) }
    if (d > 0) {
      if (x === prevX) { y--; middle.push({ kind: "+", line: B[y], aLine: start + x, bLine: start + y }) }
      else { x--; middle.push({ kind: "-", line: A[x], aLine: start + x, bLine: start + y }) }
    }
  }
  middle.reverse()
  const ops: Op[] = []
  for (let i = 0; i < start; i++) ops.push({ kind: " ", line: a[i], aLine: i, bLine: i })
  ops.push(...middle)
  for (let i = endA, j = endB; i < a.length; i++, j++) ops.push({ kind: " ", line: a[i], aLine: i, bLine: j })
  return ops
}

function formatHunks(ops: Op[], context: number, maxLines: number): string {
  const out: string[] = []
  const changed = ops.map((op) => op.kind !== " ")
  let i = 0
  while (i < ops.length && out.length < maxLines) {
    if (!changed[i]) { i++; continue }
    const from = Math.max(0, i - context)
    let to = i
    while (to < ops.length) {
      if (changed[to]) { to++; continue }
      let next = to
      while (next < ops.length && !changed[next] && next - to < 2 * context) next++
      if (next < ops.length && changed[next] && next - to < 2 * context) { to = next; continue }
      break
    }
    const end = Math.min(ops.length, to + context)
    out.push(`@@ -${ops[from].aLine + 1} +${ops[from].bLine + 1} @@`)
    for (let j = from; j < end && out.length < maxLines; j++) out.push(`${ops[j].kind}${ops[j].line}`)
    i = end
  }
  if (out.length >= maxLines) out.push(`… (truncated)`)
  return out.join("\n")
}
