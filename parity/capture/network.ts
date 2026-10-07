// Server-output layers of a page capture (plans/rust-conversion.md, decision 4): every response
// the server sent the page, and every Action Cable frame, normalized so that two servers that
// sent the same bytes (up to the typed placeholders of normalize.ts) produce the same text.
import { createHash } from "node:crypto"
import type { Page, Request, Response } from "playwright"
import { maskText, normalizeFragment, normalizeResponse } from "./normalize.ts"
import type { NormalizeOptions } from "./normalize.ts"

// Headers whose presence says nothing about the app (transport, timing, per-request ids).
const TRANSPORT_HEADERS = new Set([
  "connection", "content-length", "date", "keep-alive", "server", "server-timing", "transfer-encoding", "x-request-id", "x-runtime",
])
// Headers whose value is part of the shape: what the response is, where it sends you, how it may be
// cached, which cookies it sets or clears (plans/rust-conversion.md, decision 3).
const VALUE_HEADERS = new Set(["content-type", "location", "cache-control", "content-disposition", "vary"])

// Records every response from the page's origin: method, path, status, header shape and a hash of
// the normalized body. Requests the harness answered itself (external fixtures) or never let
// through (held requests) aren't the server's output and are left out.
export class NetworkLog {
  // per request line (method, path): the latest response's description
  private pending: { key: string; entry: Promise<Entry | undefined> }[] = []
  private origin: string
  private options: NormalizeOptions
  private page: Page

  constructor(page: Page, origin: string, options: NormalizeOptions) {
    this.page = page
    this.origin = origin
    this.options = options
    page.on("response", (response) => this.pending.push({ key: `${response.request().method()} ${response.url()}`, entry: this.describe(response) }))
  }

  private async describe(response: Response): Promise<Entry | undefined> {
    const request = response.request()
    const url = new URL(response.url())
    if (!/^https?:$/.test(url.protocol) || url.origin !== this.origin || request.resourceType() === "websocket") return
    const headers = await response.allHeaders().catch(() => response.headers())
    const shape = Object.keys(headers).filter((name) => !TRANSPORT_HEADERS.has(name)).sort()
    const values = shape.filter((name) => VALUE_HEADERS.has(name)).map((name) => `${name}: ${maskText(normalizeHeaderValue(name, headers[name]), this.options)}`)
    const cookies = (await response.headersArray().catch(() => [])).filter((h) => h.name.toLowerCase() === "set-cookie").map((h) => cookieShape(h.value)).sort()
    const status = response.status()
    let body: string
    let normalized: string | undefined
    if (status >= 300 && status < 400) {
      body = "no body (redirect)"
    } else if (status === 206 || request.resourceType() === "media") {
      // Media is fetched in ranges whose sizes are the engine's choice; the total is the server's.
      body = `range of ${headers["content-range"]?.split("/")[1] ?? "?"} bytes`
    } else if (DIGESTED_ASSET.test(url.pathname)) {
      // Propshaft names an asset after its content's digest, and the path keeps that digest here.
      body = "named by its digest"
    } else if (request.method() !== "GET") {
      // A form submission's response is gone as soon as the page navigates, which may be before
      // it can be read; what it did shows in the pages and frames that follow.
      body = "not compared (not a GET)"
    } else {
      const buffer = await this.bodyOf(response)
      normalized = normalizeResponse(buffer, headers["content-type"] ?? "", this.options)
      body = buffer.length ? "" : "empty "
    }
    const head = [
      `${describeRequest(request, this.origin, this.options)} → ${status}`,
      `  headers: ${shape.join(" ")}`,
      ...values.map((v) => `  ${v}`),
      ...cookies.map((c) => `  set-cookie: ${c}`),
    ].join("\n")
    return { head, body, normalized }
  }

  // The browser drops a response's body when its document goes away, sometimes before it could be
  // read; a GET is then asked for again, with the page's cookies.
  private async bodyOf(response: Response): Promise<Buffer> {
    try {
      return await response.body()
    } catch {
      const again = await this.page.context().request.get(response.url(), { maxRedirects: 0, headers: { accept: response.request().headers()["accept"] ?? "*/*" } })
      return again.body()
    }
  }

  // The log as sorted text: requests run in parallel, so arrival order isn't the server's. `mask`
  // replaces a state's page-derived values (masks.values in screens.yml) before bodies are hashed.
  async text(mask: (text: string) => string = (t) => t): Promise<string> {
    // Whether a resource is requested once or twice (the memory cache, a preload) is the browser's
    // business, and so is what an earlier request for it got: a frame loaded while the page was
    // still marking the room read on another connection (the sidebar) may come back either way.
    // The latest response to each request counts.
    const latest = new Map<string, Promise<Entry | undefined>>()
    for (const { key, entry } of this.pending) latest.set(key, entry)
    const render = ({ head, body, normalized }: Entry) =>
      `${mask(head)}\n  body: ${normalized === undefined ? body : `${body}sha256:${createHash("sha256").update(mask(normalized)).digest("hex").slice(0, 16)}`}`
    const lines = new Set((await Promise.all(latest.values())).filter((e): e is Entry => !!e).map(render).map(maskBlobKeys))
    return [...lines].sort().join("\n") + "\n"
  }
}

// A response's description: the request line and header shape, and the body as a note (redirect,
// media range, digest-named asset) or as normalized text, hashed when the log is written.
interface Entry {
  head: string
  body: string
  normalized?: string
}

// Active Storage names a blob's file with a random key (ActiveStorage::Blob.generate_unique_secure_token),
// which shows in the disk service URLs that representation redirects point to. It's random by
// design, in the port too, so it's a placeholder here; the blob itself is compared by its signed
// id (in the pages) and its bytes (the body hash).
// It also names the file Users::AvatarsController sends (send_file of the variant's path).
function maskBlobKeys(text: string): string {
  return text
    .replace(/"key":"[a-z0-9]{28}"/g, '"key":"«blob-key»"')
    .replace(/filename="[a-z0-9]{28}"; filename\*=UTF-8''[a-z0-9]{28}/g, "filename=\"«blob-key»\"; filename*=UTF-8''«blob-key»")
}

const DIGESTED_ASSET = /^\/assets\/.+-[0-9a-f]{8,}\.[a-z0-9]+$/

function describeRequest(request: Request, origin: string, options: NormalizeOptions): string {
  const url = new URL(request.url())
  const nav = request.isNavigationRequest() ? " (navigation)" : ""
  const target = DIGESTED_ASSET.test(url.pathname) ? url.pathname + url.search : maskText(url.pathname + url.search, options)
  return `${request.method()} ${target}${nav}`.replace(origin, "")
}

function normalizeHeaderValue(name: string, value: string): string {
  if (name === "cache-control" || name === "vary") return value.split(",").map((s) => s.trim().toLowerCase()).sort().join(", ")
  return value
}

// A cookie's shape: its name, whether it's being cleared, and its attributes (not its value).
function cookieShape(value: string): string {
  const [pair, ...attributes] = value.split(";").map((s) => s.trim())
  const [name, cookieValue = ""] = pair.split("=")
  const attrs = attributes.map((a) => a.split("=")[0].toLowerCase()).filter((a) => a !== "expires" && a !== "max-age").sort()
  const cleared = cookieValue === "" || /max-age=0|expires=thu, 01 jan 1970/i.test(value)
  return `${name}${cleared ? " (cleared)" : ""}${attrs.length ? "; " + attrs.join("; ") : ""}`
}

// Action Cable frames per subscription. Frames of different subscriptions interleave in real
// time, and how often a subscription is dropped and made again is the client's business (Turbo
// replaces the sidebar's stream sources as frames load), so each subscription is summarized: how
// it ended (confirmed, rejected), the distinct actions the page sent on it, and the distinct
// messages the server sent on it, each normalized. Pings are left out.
//
// Messages count only while the page is settled (recording: from its first readiness until it
// navigates, and again from the next readiness): while a room page loads, its own
// PresenceChannel subscription broadcasts a read to the user's ReadRoomsChannel stream, which the
// page receives or not depending on whether that subscription's Redis SUBSCRIBE got in before the
// PUBLISH, a race inside the reference server. What the page's steps set off is all recorded.
//
// And only on a server the capture has to itself (a `mutates: true` state): on a shared server,
// every other capture signed in as the same user broadcasts to that user's streams too (each room
// page a read on ReadRoomsChannel), so what arrives there depends on what else is running. On a
// shared server the layer is how each subscription ended.
export class CableLog {
  recording = false
  private messages: boolean
  private subscriptions = new Map<string, { outcome: string; sent: Set<string>; received: Set<string> }>()
  private connection = new Set<string>()
  private options: NormalizeOptions

  constructor(options: NormalizeOptions, messages: boolean) {
    this.options = options
    this.messages = messages
  }

  sent(payload: string) {
    const message = parse(payload)
    if (!message?.identifier) return this.connection.add(`> ${this.mask(payload)}`)
    const subscription = this.subscription(message.identifier)
    if (message.command === "message" && this.recording && this.messages) subscription.sent.add(this.mask(typeof message.data === "string" ? message.data : JSON.stringify(message.data)))
  }

  received(payload: string) {
    const message = parse(payload)
    if (!message) return this.connection.add(`< ${this.mask(payload)}`)
    if (message.type === "ping") return
    if (!message.identifier) {
      this.connection.add(`< ${message.type}${message.reason ? ` ${message.reason}` : ""}${message.reconnect !== undefined ? ` reconnect=${message.reconnect}` : ""}`)
      return
    }
    const subscription = this.subscription(message.identifier)
    if (message.type === "confirm_subscription") subscription.outcome = "confirmed"
    else if (message.type === "reject_subscription") subscription.outcome = "rejected"
    else if (message.message !== undefined && this.recording && this.messages) {
      subscription.received.add(typeof message.message === "string" ? normalizeFragment(message.message, this.options).trim() : this.mask(JSON.stringify(message.message)))
    }
  }

  private subscription(identifier: string) {
    const key = this.mask(identifier)
    if (!this.subscriptions.has(key)) this.subscriptions.set(key, { outcome: "unconfirmed", sent: new Set(), received: new Set() })
    return this.subscriptions.get(key)!
  }

  private mask(text: string): string {
    return maskText(text, this.options)
  }

  text(mask: (text: string) => string = (t) => t): string {
    const sorted = (lines: Iterable<string>) => [...new Set([...lines].map(mask))].sort()
    const out = [`(connection)`, ...sorted(this.connection).map((l) => `  ${l}`)]
    const subscriptions = [...this.subscriptions.entries()].map(([id, s]) => [mask(id), s] as const).sort(([a], [b]) => a.localeCompare(b))
    for (const [id, s] of subscriptions) {
      out.push(`${id}: ${s.outcome}`, ...sorted(s.sent).map((l) => `  > ${l}`), ...sorted(s.received).map((l) => `  < ${l}`))
    }
    return out.join("\n") + "\n"
  }
}

function parse(payload: string): any {
  try {
    return JSON.parse(payload)
  } catch {
    return undefined
  }
}
