// Signing in as fixture users through the real sign-in form (reference/app/views/sessions/new.html.erb).
// SessionsController#create is rate limited to 10 per 3 minutes per IP, so each (server, user)
// signs in once per run and later contexts reuse that browser's cookies.
import type { Browser, BrowserContext, BrowserContextOptions } from "playwright"
import type { Target } from "./capture.ts"
import { label } from "./inventory.ts"
import type { Labels } from "./inventory.ts"

export interface Credentials {
  email: string
  password: string
}

type StorageState = Awaited<ReturnType<BrowserContext["storageState"]>>

// Seeds label every user's email as emails.<user> and the shared password as passwords.all
// (fixture passwords are all "secret123456", reference/test/fixtures/users.yml).
export function credentialsFor(user: string, labels: Labels): Credentials {
  return {
    email: String(label(labels, "emails", user) ?? `${user}@37signals.com`),
    password: String(label(labels, "passwords", user) ?? label(labels, "passwords", "all") ?? "secret123456"),
  }
}

export class SessionCache {
  private states = new Map<string, Promise<StorageState>>()

  // A reset server has a fresh copy of the seed, without the sessions signed in before.
  forget(target: Target) {
    for (const key of [...this.states.keys()]) if (key.startsWith(`${target.url} `)) this.states.delete(key)
  }

  // Keyed by the real server: every target shares one browser-facing origin.
  get(browser: Browser, target: Target, proxy: BrowserContextOptions["proxy"], user: string, labels: Labels): Promise<StorageState> {
    const key = `${target.url} ${user}`
    if (!this.states.has(key)) {
      const promise = signIn(browser, target.origin, proxy, credentialsFor(user, labels))
      // Retry a failed sign-in in later cells, except when rate limited: retrying only spends
      // more of the budget.
      promise.catch((error) => {
        if (!/rate limited/.test(String(error))) this.states.delete(key)
      })
      this.states.set(key, promise)
    }
    return this.states.get(key)!
  }
}

async function signIn(browser: Browser, baseUrl: string, proxy: BrowserContextOptions["proxy"], credentials: Credentials): Promise<StorageState> {
  const context = await browser.newContext({ timezoneId: "UTC", locale: "en-US", proxy })
  try {
    const page = await context.newPage()
    await page.goto(new URL("/session/new", baseUrl).href)
    await page.locator("input[name=email_address]").fill(credentials.email)
    await page.locator("input[name=password]").fill(credentials.password)
    const [response] = await Promise.all([
      page.waitForResponse((r) => r.request().method() === "POST" && new URL(r.url()).pathname === "/session"),
      page.locator("button[name=log_in], button[type=submit]").first().click(),
    ])
    if (response.status() === 429) throw new Error(`sign-in rate limited at ${baseUrl} (SessionsController rate_limit); restart the server or wait 3 minutes`)
    if (response.status() >= 400) throw new Error(`sign-in as ${credentials.email} failed at ${baseUrl}: HTTP ${response.status()}`)
    await page.waitForURL((url) => url.pathname !== "/session" && url.pathname !== "/session/new")
    const state = await context.storageState()
    if (!state.cookies.some((c) => c.name === "session_token")) {
      throw new Error(`sign-in as ${credentials.email} at ${baseUrl} set no session_token cookie`)
    }
    return state
  } finally {
    await context.close()
  }
}
