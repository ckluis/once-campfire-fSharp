// Waits for real readiness signals (plans/rust-conversion.md, "Determinism"): every data-controller
// element has connected controller instances, every Action Cable subscription in the consumer is
// confirmed by the server, fonts are loaded, images and posters decoded, Turbo idle, the network
// quiet, and the DOM stable for a short window.
import path from "node:path"
import { fileURLToPath } from "node:url"
import type { Page, Request, Route, WebSocket } from "playwright"
import type { CableLog } from "./network.ts"

export const READINESS_SCRIPT = path.join(path.dirname(fileURLToPath(import.meta.url)), "readiness.js")
export const DETERMINISM_SCRIPT = path.join(path.dirname(fileURLToPath(import.meta.url)), "determinism.js")

const QUIET_MS = 250 // network quiet before time may move
const POLL_MS = 40
// Fake time moves in ticks (see freezeClock in capture.ts). A tick happens only when the page has
// settled in real time: network idle, every module loaded, nothing pending that real time will
// resolve (cable confirmations, images, fonts), and the DOM unchanged for SETTLE_MS since the last
// change and since the last tick, so a tick's effects (a fetch it starts, a smooth scroll) are seen
// before the next one. The page is ready after QUIET_TICKS ticks in a row that changed nothing.
// How much fake time passes is then a function of the app's timers alone, never of how fast the
// machine is: every timer due within QUIET_TICKS * ADVANCE_MS of the last change has fired, and
// none later.
const ADVANCE_MS = 50
const SETTLE_MS = 60
const QUIET_TICKS = 8

// Excluded from "network idle": long-lived by design, and covered by their own signals.
const LONG_LIVED = new Set(["websocket", "eventsource", "media"])

export class PageTracker {
  readonly page: Page
  inflight = new Set<Request>()
  held = new Set<Request>()
  heldRoutes: Route[] = []
  lastNetworkActivity = Date.now()
  socket?: WebSocket
  confirmed = new Set<string>()
  rejected = new Set<string>()
  // subscribe commands sent minus confirmations/rejections received, per identifier, since the
  // last welcome: a resubscription (turbo-cable-stream-source reconnected by a frame load) needs
  // its own confirmation, not the previous one's.
  outstanding = new Map<string, number>()
  // The server closed the connection (a "disconnect" frame: close_remote_connections when the user
  // loses a membership). ActionCable's ConnectionMonitor would reconnect once pings look stale,
  // which it measures with Date, frozen here: the page stays disconnected, as it is at that moment
  // in a real browser.
  serverDisconnected = false
  errors: string[] = []
  networkErrors: string[] = []
  cableLog: string[] = [] // subscribe/unsubscribe/confirm/reject frames, for diagnosing readiness
  cable?: CableLog // every frame, normalized: the cable layer of the comparison
  console: string[] = []

  constructor(page: Page) {
    this.page = page
    page.on("request", (request) => {
      if (LONG_LIVED.has(request.resourceType()) || this.held.has(request)) return
      this.inflight.add(request)
      this.lastNetworkActivity = Date.now()
    })
    const settle = (request: Request) => {
      if (this.inflight.delete(request)) this.lastNetworkActivity = Date.now()
    }
    page.on("requestfinished", settle)
    page.on("requestfailed", (request) => {
      settle(request)
      // Requests the harness refused (external hosts) or cut (held requests) are expected; any
      // other failure (Chromium's ERR_NETWORK_CHANGED when Docker adds an interface on the host)
      // leaves a broken image on screen, so the capture fails and is retried.
      const error = request.failure()?.errorText ?? ""
      if (!this.held.has(request) && !/BLOCKED_BY_CLIENT|blocked|NS_ERROR_ABORT|cancelled|aborted/i.test(error)) {
        const origin = new URL(page.url()).origin
        if (request.url().startsWith(origin)) this.networkErrors.push(`${error} ${request.url()}`)
      }
    })
    page.on("websocket", (socket) => this.watchSocket(socket))
    page.on("pageerror", (error) => this.errors.push(String(error?.stack ?? error)))
    page.on("console", (message) => {
      if (message.type() === "error" || message.type() === "warning") this.console.push(`${message.type()}: ${message.text()}`)
    })
    page.on("response", (response) => {
      if (response.status() >= 400) this.console.push(`http: ${response.status()} ${response.request().method()} ${response.url()}`)
    })
    page.on("framenavigated", (frame) => {
      if (frame !== page.mainFrame()) return
      this.resetCable()
      if (this.cable) this.cable.recording = false
    })
  }

  private resetCable() {
    this.socket = undefined
    this.confirmed.clear()
    this.rejected.clear()
    this.outstanding.clear()
  }

  // Action Cable keeps no confirmed flag on a Subscription, so confirmation is read off the wire.
  private watchSocket(socket: WebSocket) {
    this.socket = socket
    this.confirmed.clear()
    this.rejected.clear()
    const short = (identifier: string) => {
      try {
        const id = JSON.parse(identifier)
        return `${id.channel}${id.room_id ? `:${id.room_id}` : ""}${id.signed_stream_name ? `:${id.signed_stream_name.slice(0, 12)}` : ""}`
      } catch {
        return identifier
      }
    }
    socket.on("framesent", ({ payload }) => {
      if (typeof payload !== "string") return
      this.cable?.sent(payload)
      try {
        const message = JSON.parse(payload)
        if (message.command) this.cableLog.push(`> ${message.command} ${short(message.identifier)}`)
        if (message.command === "subscribe" && socket === this.socket) {
          this.outstanding.set(message.identifier, (this.outstanding.get(message.identifier) ?? 0) + 1)
        }
      } catch {}
    })
    socket.on("framereceived", ({ payload }) => {
      if (typeof payload === "string") this.cable?.received(payload)
      if (socket !== this.socket || typeof payload !== "string") return
      let message: any
      try {
        message = JSON.parse(payload)
      } catch {
        return
      }
      if (message.type && message.type !== "ping") this.cableLog.push(`< ${message.type} ${message.identifier ? short(message.identifier) : ""}`)
      if (message.type === "welcome") {
        this.confirmed.clear()
        this.outstanding.clear()
        this.serverDisconnected = false
      } else if (message.type === "disconnect") {
        this.serverDisconnected = true
      } else if (message.type === "confirm_subscription" || message.type === "reject_subscription") {
        ;(message.type === "confirm_subscription" ? this.confirmed : this.rejected).add(message.identifier)
        this.outstanding.set(message.identifier, (this.outstanding.get(message.identifier) ?? 0) - 1)
      }
    })
    socket.on("close", () => {
      if (socket === this.socket) this.confirmed.clear()
    })
  }

  // A request deliberately left pending (hold_requests) isn't something to wait for.
  hold(request: Request, route: Route) {
    this.held.add(request)
    this.heldRoutes.push(route)
    if (this.inflight.delete(request)) this.lastNetworkActivity = Date.now()
  }

  // Held requests must never reach the server: closing a context with a route still pending can
  // let the request through (an upload from interactions/composer/upload_in_progress created a
  // message that later captures of the room showed). Abort them before the context closes.
  async abortHeld() {
    const routes = this.heldRoutes.splice(0)
    await Promise.all(routes.map((r) => r.abort("aborted").catch(() => {})))
  }

  networkIdleFor(): number {
    return this.inflight.size ? 0 : Date.now() - this.lastNetworkActivity
  }
}

export interface ReadinessResult {
  elapsedMs: number
  controllers: number
  subscriptions: string[]
  unknownControllers: string[]
}

export async function waitForReady(trackers: PageTracker[], timeoutMs: number, time?: number): Promise<ReadinessResult[]> {
  const results = await Promise.all(trackers.map((t) => settle(t, timeoutMs, time)))
  for (const t of trackers) if (t.cable) t.cable.recording = true
  return results
}

// Settles the page (see ADVANCE_MS): until it's ready, or, with `until`, until that holds (a step
// waiting for an element that appears after a debounce or a fetch). Time only moves while the page
// is settled, so a step's wait ends at the same fake time on every run.
export async function settle(tracker: PageTracker, timeoutMs: number, time: number | undefined, until?: () => Promise<boolean>): Promise<ReadinessResult> {
  const { page } = tracker
  const started = Date.now()
  if (tracker.networkErrors.length) throw new Error(`network failure: ${tracker.networkErrors[0]}`)
  let lastFingerprint = ""
  let changedAt = Date.now()
  let tickedAt = 0
  let quietTicks = 0
  let reasons: string[] = []
  let snapshot: any
  while (true) {
    if (until && (await until())) return readinessResult(snapshot, started)
    try {
      snapshot = await page.evaluate((socketSeen) => (window as any).__parity?.snapshot(socketSeen) ?? null, !!tracker.socket)
    } catch (error) {
      // The page navigated mid-evaluation; poll again on the new document.
      snapshot = null
      reasons = [`evaluate: ${String(error).split("\n")[0]}`]
    }
    if (tracker.networkErrors.length) throw new Error(`network failure: ${tracker.networkErrors[0]}`)
    if (snapshot) {
      if (snapshot.fingerprint !== lastFingerprint) {
        lastFingerprint = snapshot.fingerprint
        changedAt = Date.now()
        quietTicks = 0
      }
      reasons = unreadyReasons(snapshot, tracker)
      const now = Date.now()
      const settled = tracker.networkIdleFor() >= QUIET_MS && now - changedAt >= SETTLE_MS && now - tickedAt >= SETTLE_MS
      if (tracker.networkIdleFor() < QUIET_MS) reasons.push(`network: ${tracker.inflight.size} in flight`)
      if (settled && !reasons.length && !until && quietTicks >= QUIET_TICKS) return readinessResult(snapshot, started)
      // A Turbo visit or frame load may be waiting on a timer (a repaint); anything else pending is
      // for real time to resolve.
      if (settled && reasons.every((r) => r.startsWith("turbo busy"))) {
        await advanceClock(page, time)
        tickedAt = Date.now()
        quietTicks++
        continue
      }
      if (!settled) reasons.push(Date.now() - changedAt < SETTLE_MS ? "dom changing" : "settling")
    } else if (!reasons.length) {
      reasons = ["readiness script not installed"]
    }
    if (Date.now() - started > timeoutMs) {
      const inflight = [...tracker.inflight].map((r) => `${r.method()} ${r.url()}`)
      throw new Error(`not ready after ${timeoutMs}ms at ${page.url()}: ${until ? "waiting for the step's element; " : ""}${reasons.join("; ")}${inflight.length ? ` [${inflight.join(", ")}]` : ""}`)
    }
    await new Promise((resolve) => setTimeout(resolve, POLL_MS))
  }
}

function readinessResult(snapshot: any, started: number): ReadinessResult {
  return {
    elapsedMs: Date.now() - started,
    controllers: snapshot?.stimulus.connected ?? 0,
    subscriptions: snapshot?.cable.identifiers ?? [],
    unknownControllers: snapshot?.stimulus.unknown ?? [],
  }
}

// Fire the timers due in the next ADVANCE_MS, then put Date back to the frozen instant (unless the
// state lets it advance).
async function advanceClock(page: Page, time: number | undefined) {
  try {
    await page.clock.runFor(ADVANCE_MS)
    if (time !== undefined) await page.clock.setSystemTime(time)
  } catch {
    // navigated mid-advance; the next poll sees the new document
  }
}

function unreadyReasons(s: any, tracker: PageTracker): string[] {
  const reasons: string[] = []
  if (s.readyState !== "complete") reasons.push(`document ${s.readyState}`)
  if (s.fonts !== "loaded") reasons.push(`fonts ${s.fonts}`)
  if (!s.stimulus.present && s.stimulus.expectsApp) reasons.push("Stimulus application not started")
  if (s.stimulus.unregistered?.length) reasons.push(`controllers not registered: ${s.stimulus.unregistered.join(", ")}`)
  if (s.stimulus.missing?.length) reasons.push(`controllers not connected: ${s.stimulus.missing.slice(0, 5).join(", ")}`)
  const cable = s.cable
  if (cable.unsubscribedSources) reasons.push(`${cable.unsubscribedSources} turbo-cable-stream-source without subscription`)
  if (cable.streamSources && !tracker.socket) reasons.push("stream sources but no socket yet")
  if (tracker.socket && !(tracker.serverDisconnected && !cable.active)) {
    if (cable.error) reasons.push(`cable: ${cable.error}`)
    else if (!cable.open) reasons.push("cable connection not open")
    for (const identifier of cable.identifiers ?? []) {
      if ((!tracker.confirmed.has(identifier) && !tracker.rejected.has(identifier)) || (tracker.outstanding.get(identifier) ?? 0) > 0) {
        reasons.push(`subscription unconfirmed: ${identifier}`)
      }
    }
  }
  if (s.media.pendingImages.length) reasons.push(`images: ${s.media.pendingImages.slice(0, 3).join(", ")}`)
  if (s.media.pendingVideos.length) reasons.push(`video: ${s.media.pendingVideos.slice(0, 3).join(", ")}`)
  if (s.turboBusy.length) reasons.push(`turbo busy: ${s.turboBusy.join(", ")}`)
  if (s.undefinedElements.length) reasons.push(`custom elements not defined: ${s.undefinedElements.join(", ")}`)
  return reasons
}
