// Loads parity/screens.yml (format in parity/SCREENS.md) and expands it over the support matrix.
import fs from "node:fs"
import path from "node:path"
import YAML from "yaml"
import { ENGINES, SCHEMES, VIEWPORTS, VIEWPORT_NAMES, SEED_DIR, breakpointViewport } from "./config.ts"
import type { Engine, Scheme, Viewport } from "./config.ts"

export type Step =
  | { click: string; actor?: string }
  | { fill: { selector: string; text: string }; actor?: string }
  | { hover: string; actor?: string }
  | { press: string | { selector?: string; key: string }; actor?: string }
  | { wait_for: string; actor?: string }
  | { pause_animations_at: number; actor?: string }
  | { goto: string; actor?: string }
  | { type: { selector: string; text: string }; actor?: string }
  | { upload: { selector: string; files: string[] }; actor?: string }
  | { scroll: { selector: string; to: "top" | "bottom" }; actor?: string }
  | { hold_requests: string; actor?: string }

export interface State {
  id: string
  as?: string
  path: string | Record<string, string>
  seed: string
  matrix?: { viewports?: string[]; schemes?: Scheme[]; engines?: Engine[] }
  steps: Step[]
  actors?: Record<string, string>
  capture?: string
  covers?: string[]
  // Extensions beyond SCREENS.md (documented at the top of parity/screens.yml):
  kind?: "page" | "fragment" // fragment: compare the response only (status, type, normalized body)
  request?: { method?: string; body?: string; headers?: Record<string, string> } // fragments only
  headers?: Record<string, string> // extra headers on every request of the state
  user_agent?: string // key of parity/seeds/user_agents.yml, or a literal UA string
  accept_dialogs?: boolean // accept window.confirm (turbo_confirm) instead of dismissing
  expect_status?: number // HTTP status of the main document (default 200)
  mutates?: boolean // changes the database: runs serially, each capture on a freshly reset server
  breakpoints?: boolean // include in the breakpoint sweep (besides DEFAULT_BREAKPOINT_STATES)
  notifications?: "denied" | "granted" // the browser's notification state (default denied)
  clock?: "frozen" | "advancing" // the browser's Date: frozen at the seed instant (default), or advancing with fake time
  masks?: Masks
}

// Values a server makes up at random while a state runs (parity/SCREENS.md, "Masks"), declared
// per state. Each value is read from the captured page, on each server, and replaced by a typed
// placeholder «name» in every text layer; pixel masks paint the listed elements' boxes over in
// the screenshot.
export interface Masks {
  values?: Record<string, ValueMask>
  pixels?: string[] // selectors
}

export interface ValueMask {
  selector: string // the element that shows the value
  attribute?: string // read this attribute (default: the text content)
  match?: string // a regular expression with one group: the value is that group of what was read
}

export interface Cell {
  engine: Engine
  viewport: Viewport
  scheme: Scheme
}

export interface Job {
  state: State
  cell: Cell
}

export function cellId(cell: Cell): string {
  return `${cell.engine}-${cell.viewport.name}-${cell.scheme}`
}

export function jobId(job: Job): string {
  return `${job.state.id} @ ${cellId(job.cell)}`
}

export function loadInventory(file: string): State[] {
  const raw = YAML.parse(fs.readFileSync(file, "utf8"))
  const list = Array.isArray(raw) ? raw : raw?.states
  if (!Array.isArray(list)) throw new Error(`${file}: expected a list of states`)
  const seen = new Set<string>()
  return list.map((entry: any, index: number) => {
    const state = validateState(entry, `${file}[${index}]`)
    if (seen.has(state.id)) throw new Error(`${file}: duplicate state id ${state.id}`)
    seen.add(state.id)
    return state
  })
}

function validateState(entry: any, where: string): State {
  if (!entry || typeof entry !== "object") throw new Error(`${where}: not a mapping`)
  if (typeof entry.id !== "string" || !entry.id) throw new Error(`${where}: missing id`)
  if (!/^[A-Za-z0-9_\-\/.]+$/.test(entry.id)) throw new Error(`${where}: id ${entry.id} must be path-like`)
  if (typeof entry.path !== "string" && (typeof entry.path !== "object" || !entry.path)) {
    throw new Error(`${entry.id}: missing path`)
  }
  const steps = entry.steps ?? []
  if (!Array.isArray(steps)) throw new Error(`${entry.id}: steps must be a list`)
  if (entry.actors) {
    if (!entry.capture || !entry.actors[entry.capture]) {
      throw new Error(`${entry.id}: multi-user states need capture: <actor>`)
    }
    for (const step of steps) {
      if (step.actor && !entry.actors[step.actor]) throw new Error(`${entry.id}: unknown actor ${step.actor}`)
    }
  }
  for (const vp of entry.matrix?.viewports ?? []) {
    if (!VIEWPORTS[vp]) throw new Error(`${entry.id}: unknown viewport ${vp}`)
  }
  for (const e of entry.matrix?.engines ?? []) {
    if (!ENGINES.includes(e)) throw new Error(`${entry.id}: unknown engine ${e}`)
  }
  for (const s of entry.matrix?.schemes ?? []) {
    if (!SCHEMES.includes(s)) throw new Error(`${entry.id}: unknown scheme ${s}`)
  }
  if (entry.clock && !["frozen", "advancing"].includes(entry.clock)) throw new Error(`${entry.id}: clock must be frozen or advancing`)
  if (entry.notifications && !["denied", "granted"].includes(entry.notifications)) throw new Error(`${entry.id}: notifications must be denied or granted`)
  if (entry.kind && !["page", "fragment"].includes(entry.kind)) throw new Error(`${entry.id}: unknown kind ${entry.kind}`)
  validateMasks(entry)
  return { seed: "default", ...entry, steps }
}

function validateMasks(entry: any) {
  const masks = entry.masks
  if (masks === undefined) return
  if (!masks || typeof masks !== "object") throw new Error(`${entry.id}: masks must be a mapping`)
  for (const [name, mask] of Object.entries<any>(masks.values ?? {})) {
    if (!/^[a-z0-9_]+$/.test(name)) throw new Error(`${entry.id}: mask value name ${name} must be snake_case`)
    if (typeof mask?.selector !== "string") throw new Error(`${entry.id}: masks.values.${name} needs a selector`)
    if (mask.match !== undefined && !/\((?!\?)/.test(mask.match)) {
      throw new Error(`${entry.id}: masks.values.${name}.match needs a capture group`)
    }
  }
  if (masks.pixels !== undefined && (!Array.isArray(masks.pixels) || masks.pixels.some((s: any) => typeof s !== "string"))) {
    throw new Error(`${entry.id}: masks.pixels must be a list of selectors`)
  }
}

export interface MatrixFilter {
  engines?: Engine[]
  viewports?: string[]
  schemes?: Scheme[]
  only?: string[] // state id globs
  breakpoints?: "include" | "only" | "exclude"
  breakpointStates?: string[] // state id globs swept across breakpoints (default DEFAULT_BREAKPOINT_STATES)
  matrix?: "lean" | "full" // default lean (see expandJobs)
}

// The lean matrix (plans/rust-conversion.md, decision 4). The frontend is byte-identical and the
// browsers are pinned, so a port can only change pixels by sending different bytes, and the
// server-output layers (server HTML, live DOM, accessibility tree, every response, every cable
// frame) are compared in every cell. Pixels back that up thinly: Chromium on desktop and phone in
// light and dark for every state, the breakpoint sweep on Chromium, and the smoke states below on
// Firefox and WebKit, desktop and phone, light. The full matrix is for release checks.
export const LEAN_VIEWPORTS = ["desktop", "phone"]
export const LEAN_SMOKE_STATES = [
  "realtime/**",
  "auth/sign_in",
  "rooms/show/designers",
  "interactions/composer/with_text",
  "interactions/lightbox",
  "interactions/mention_autocomplete/results",
]

// Representative layouts swept 1px either side of every width breakpoint: signed out, a room
// (group, direct, composer in use), settings forms, profile, search, and the empty state.
export const DEFAULT_BREAKPOINT_STATES = [
  "auth/sign_in",
  "rooms/show/designers",
  "rooms/show/direct",
  "interactions/composer/with_text",
  "interactions/actions_menu",
  "account/edit/admin",
  "rooms/opens/new",
  "users/profile",
  "users/show/self",
  "search/results",
  "welcome/no_rooms",
]

export function isFragment(state: State): boolean {
  return state.kind === "fragment"
}

export function expandJobs(states: State[], filter: MatrixFilter, breakpointWidths: Record<Engine, number[]>): Job[] {
  const jobs: Job[] = []
  const onlyRes = filter.only?.map(globToRegExp)
  const sweepRes = (filter.breakpointStates ?? DEFAULT_BREAKPOINT_STATES).map(globToRegExp)
  const smokeRes = LEAN_SMOKE_STATES.map(globToRegExp)
  const lean = (filter.matrix ?? "lean") === "lean"
  for (const state of states) {
    if (onlyRes && !onlyRes.some((re) => re.test(state.id))) continue
    const stateEngines = state.matrix?.engines ?? ENGINES
    const stateViewports = state.matrix?.viewports ?? VIEWPORT_NAMES
    const stateSchemes = state.matrix?.schemes ?? SCHEMES
    // (engines, viewports, schemes) groups this state is captured in
    const groups: [readonly Engine[], readonly string[], readonly Scheme[]][] = []
    if (!lean) {
      groups.push([stateEngines, stateViewports, stateSchemes])
    } else {
      // A state narrowed to other viewports (tablet only, say) keeps its first one.
      const leanViewports = stateViewports.filter((v) => LEAN_VIEWPORTS.includes(v))
      const viewports = leanViewports.length ? leanViewports : stateViewports.slice(0, 1)
      const primary = stateEngines.includes("chromium") ? ["chromium" as Engine] : stateEngines.slice(0, 1)
      groups.push([primary, viewports, stateSchemes])
      if (smokeRes.some((re) => re.test(state.id))) {
        const others = stateEngines.filter((e) => !primary.includes(e))
        const light = stateSchemes.includes("light") ? ["light" as Scheme] : stateSchemes.slice(0, 1)
        groups.push([others, viewports, light])
      }
    }
    if (isFragment(state)) {
      // A response, not a rendering: one capture, whatever the matrix.
      const engines = intersect(stateEngines, filter.engines)
      if (filter.breakpoints !== "only" && engines.length) {
        jobs.push({ state, cell: { engine: engines[0], viewport: VIEWPORTS.desktop, scheme: "light" } })
      }
      continue
    }
    if (filter.breakpoints !== "only") {
      for (const [groupEngines, groupViewports, groupSchemes] of groups) {
        for (const engine of intersect(groupEngines, filter.engines)) for (const vp of intersect(groupViewports, filter.viewports)) for (const scheme of intersect(groupSchemes, filter.schemes)) {
          jobs.push({ state, cell: { engine, viewport: VIEWPORTS[vp], scheme } })
        }
      }
    }
    const swept = state.breakpoints || sweepRes.some((re) => re.test(state.id))
    if (swept && filter.breakpoints !== "exclude") {
      const sweepEngines = intersect(lean ? groups[0][0] : stateEngines, filter.engines)
      for (const engine of sweepEngines) for (const width of breakpointWidths[engine] ?? []) for (const scheme of intersect(stateSchemes, filter.schemes)) {
        jobs.push({ state, cell: { engine, viewport: breakpointViewport(width), scheme } })
      }
    }
  }
  return jobs
}

function intersect<T>(values: readonly T[], filter?: readonly T[]): T[] {
  return filter ? values.filter((v) => filter.includes(v)) : [...values]
}

export function globToRegExp(glob: string): RegExp {
  const escaped = glob.replace(/[.+^${}()|[\]\\]/g, "\\$&").replace(/\*\*/g, "\u0000").replace(/\*/g, "[^/]*").replace(/\?/g, ".")
  return new RegExp(`^${escaped.replace(/\u0000/g, ".*")}$`)
}

// {{table.label}} interpolation from parity/.seed/<seed>/labels.json, written by parity/bin/seed
// as a flat map ({"rooms.designers": 3, "clock.now": "2026-…"}); a nested map works too.
export type Labels = Record<string, any>

export function label(labels: Labels, table: string, name: string): string | number | undefined {
  return labels[`${table}.${name}`] ?? labels[table]?.[name]
}

const labelCache = new Map<string, Labels>()

export function loadLabels(seed: string, seedDir = SEED_DIR): Labels {
  if (!labelCache.has(seed)) {
    const file = path.join(seedDir, seed, "labels.json")
    labelCache.set(seed, fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, "utf8")) : {})
  }
  return labelCache.get(seed)!
}

export function interpolate(template: string, labels: Labels, where: string): string {
  return template.replace(/\{\{\s*([A-Za-z0-9_]+)\.([A-Za-z0-9_]+)\s*\}\}/g, (_, table, name) => {
    const value = label(labels, table, name)
    if (value === undefined) throw new Error(`${where}: no fixture label {{${table}.${name}}}`)
    return String(value)
  })
}

// Deep-interpolates every string in a step.
export function interpolateStep<T>(step: T, labels: Labels, where: string): T {
  const walk = (value: any): any =>
    typeof value === "string" ? interpolate(value, labels, where)
    : Array.isArray(value) ? value.map(walk)
    : value && typeof value === "object" ? Object.fromEntries(Object.entries(value).map(([k, v]) => [k, walk(v)]))
    : value
  return walk(step)
}

// Seed metadata: the instant both servers' clocks are frozen at, if the seed records it.
export function seedTime(seed: string, seedDir = SEED_DIR): string | undefined {
  for (const name of ["time", "clock", "time.txt"]) {
    const file = path.join(seedDir, seed, name)
    if (fs.existsSync(file)) return fs.readFileSync(file, "utf8").trim()
  }
  const meta = path.join(seedDir, seed, "meta.json")
  if (fs.existsSync(meta)) {
    const json = JSON.parse(fs.readFileSync(meta, "utf8"))
    return json.time ?? json.clock ?? json.frozen_at
  }
  const now = label(loadLabels(seed, seedDir), "clock", "now")
  return typeof now === "string" ? now : undefined
}
