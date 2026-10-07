// Runs a state's steps (parity/SCREENS.md, plus the extensions listed at the top of
// parity/screens.yml). Touch cells tap instead of clicking, as a phone would.
import path from "node:path"
import type { Page } from "playwright"
import { REPO_DIR } from "./config.ts"
import type { Step } from "./inventory.ts"
import { settle } from "./readiness.ts"
import type { PageTracker } from "./readiness.ts"

export interface StepContext {
  pages: Record<string, Page> // actor -> page
  trackers: Record<string, PageTracker>
  defaultActor: string
  touch: boolean
  baseUrl: string
  timeoutMs: number
  time?: number // the instant Date is put back to after each tick (undefined: it advances)
  pauseAnimationsAt?: number
}

export async function runStep(step: Step, ctx: StepContext): Promise<void> {
  const actor = step.actor ?? ctx.defaultActor
  const page = ctx.pages[actor]
  if (!page) throw new Error(`step for unknown actor ${actor}`)
  const timeout = ctx.timeoutMs
  // Timers run on the paused fake clock (capture.ts freezeClock), so whatever a step waits for
  // may need time to pass first (pagination, debounced autocomplete): time moves while waiting,
  // in the same settled ticks as readiness (readiness.ts settle).
  const target = targetSelector(step)
  if (target) await waitWithClock(page, ctx.trackers[actor], target.selector, target.state, ctx)
  if ("click" in step) {
    const target = page.locator(step.click).first()
    if (ctx.touch) await target.tap({ timeout })
    else await target.click({ timeout })
  } else if ("fill" in step) {
    await page.locator(step.fill.selector).first().fill(step.fill.text, { timeout })
  } else if ("type" in step) {
    await page.locator(step.type.selector).first().pressSequentially(step.type.text, { timeout })
  } else if ("hover" in step) {
    await page.locator(step.hover).first().hover({ timeout })
  } else if ("press" in step) {
    if (typeof step.press === "string") await page.keyboard.press(step.press)
    else if (step.press.selector) await page.locator(step.press.selector).first().press(step.press.key, { timeout })
    else await page.keyboard.press(step.press.key)
  } else if ("upload" in step) {
    const files = step.upload.files.map((f) => path.resolve(REPO_DIR, f))
    await page.locator(step.upload.selector).first().setInputFiles(files, { timeout })
  } else if ("scroll" in step) {
    await page.locator(step.scroll.selector).first().evaluate((el, to) => {
      el.scrollTop = to === "top" ? 0 : el.scrollHeight
    }, step.scroll.to)
  } else if ("hold_requests" in step) {
    await holdRequests(page, ctx.trackers[actor], step.hold_requests)
  } else if ("wait_for" in step) {
    // waited for above
  } else if ("pause_animations_at" in step) {
    ctx.pauseAnimationsAt = step.pause_animations_at
  } else if ("goto" in step) {
    await page.goto(new URL(step.goto, ctx.baseUrl).href, { timeout })
  } else {
    throw new Error(`unknown step ${JSON.stringify(step)}`)
  }
}

function targetSelector(step: Step): { selector: string; state: "visible" | "attached" } | undefined {
  if ("click" in step) return { selector: step.click, state: "visible" }
  if ("fill" in step) return { selector: step.fill.selector, state: "visible" }
  if ("type" in step) return { selector: step.type.selector, state: "visible" }
  if ("hover" in step) return { selector: step.hover, state: "visible" }
  if ("wait_for" in step) return { selector: step.wait_for, state: "visible" }
  if ("upload" in step) return { selector: step.upload.selector, state: "attached" }
  if ("scroll" in step) return { selector: step.scroll.selector, state: "attached" }
  if ("press" in step && typeof step.press !== "string" && step.press.selector) return { selector: step.press.selector, state: "visible" }
}

async function waitWithClock(page: Page, tracker: PageTracker, selector: string, state: "visible" | "attached", ctx: StepContext) {
  const locator = page.locator(selector).first()
  const present = async () => (state === "visible" ? await locator.isVisible().catch(() => false) : (await locator.count().catch(() => 0)) > 0)
  try {
    await settle(tracker, ctx.timeoutMs, ctx.time, present)
  } catch (error: any) {
    throw new Error(`step: ${selector} not ${state}: ${String(error?.message ?? error).split("\n")[0]}`)
  }
}

// "POST **/rooms/1/messages": matching requests never get a response, so in-flight UI (upload
// progress, pending message) stays on screen. Held requests don't count against network idle.
async function holdRequests(page: Page, tracker: PageTracker, spec: string) {
  const m = /^(?:([A-Z]+)\s+)?(\S+)$/.exec(spec.trim())
  if (!m) throw new Error(`hold_requests: can't parse ${spec}`)
  const [, method, glob] = m
  await page.route(glob, (route, request) => {
    if (method && request.method() !== method) return route.fallback()
    tracker.hold(request, route)
  })
}
