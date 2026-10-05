// parity/allowlist.yml: known, owned exceptions, each a deliberate divergence no mask can express.
//
// - state: rooms/show/busy        # glob over state ids (* within a segment, ** across)
//   cells: "webkit-phone-*"        # optional glob over cell ids (engine-viewport-scheme)
//   layers: [pixels, server, live, aria]   # optional; default all
//   reason: Safari renders the emoji at a different baseline in the port
//   owner: views
import fs from "node:fs"
import YAML from "yaml"
import { globToRegExp } from "./inventory.ts"

export const LAYERS = ["pixels", "server", "live", "aria", "network", "cable"] as const
export type Layer = (typeof LAYERS)[number]

export interface AllowEntry {
  state: string
  cells?: string
  layers?: Layer[]
  reason: string
  owner: string
}

export class Allowlist {
  readonly entries: AllowEntry[]
  private used = new Set<AllowEntry>()

  constructor(entries: AllowEntry[]) {
    this.entries = entries
  }

  static load(file: string): Allowlist {
    if (!fs.existsSync(file)) return new Allowlist([])
    const raw = YAML.parse(fs.readFileSync(file, "utf8")) ?? []
    if (!Array.isArray(raw)) throw new Error(`${file}: expected a list`)
    raw.forEach((entry: any, i: number) => {
      for (const key of ["state", "reason", "owner"]) {
        if (typeof entry?.[key] !== "string" || !entry[key].trim()) throw new Error(`${file}[${i}]: ${key} is required`)
      }
      for (const layer of entry.layers ?? []) {
        if (!LAYERS.includes(layer)) throw new Error(`${file}[${i}]: unknown layer ${layer}`)
      }
    })
    return new Allowlist(raw)
  }

  match(state: string, cell: string, layer: Layer): AllowEntry | undefined {
    const entry = this.entries.find((e) =>
      globToRegExp(e.state).test(state) &&
      (!e.cells || globToRegExp(e.cells).test(cell)) &&
      (!e.layers || e.layers.includes(layer)))
    if (entry) this.used.add(entry)
    return entry
  }

  unused(): AllowEntry[] {
    return this.entries.filter((e) => !this.used.has(e))
  }
}
