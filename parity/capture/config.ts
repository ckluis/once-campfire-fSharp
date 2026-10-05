// The support matrix from plans/rust-conversion.md ("What pixel perfect covers").
import path from "node:path"
import { fileURLToPath } from "node:url"

export const PARITY_DIR = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..")
export const REPO_DIR = path.resolve(PARITY_DIR, "..")
export const STYLESHEETS_DIR = path.join(REPO_DIR, "reference/app/assets/stylesheets")
export const SCREENS_FILE = path.join(PARITY_DIR, "screens.yml")
export const ALLOWLIST_FILE = path.join(PARITY_DIR, "allowlist.yml")
export const SEED_DIR = path.join(PARITY_DIR, ".seed")

export const ENGINES = ["chromium", "firefox", "webkit"] as const
export type Engine = (typeof ENGINES)[number]

export const SCHEMES = ["light", "dark"] as const
export type Scheme = (typeof SCHEMES)[number]

export interface Viewport {
  name: string
  width: number
  height: number
  deviceScaleFactor: number
  touch: boolean
  mobile: boolean
}

export const VIEWPORTS: Record<string, Viewport> = {
  desktop: { name: "desktop", width: 1440, height: 900, deviceScaleFactor: 1, touch: false, mobile: false },
  laptop: { name: "laptop", width: 1280, height: 800, deviceScaleFactor: 1, touch: false, mobile: false },
  tablet: { name: "tablet", width: 834, height: 1194, deviceScaleFactor: 2, touch: true, mobile: false },
  phone: { name: "phone", width: 390, height: 844, deviceScaleFactor: 3, touch: true, mobile: true },
}
export const VIEWPORT_NAMES = Object.keys(VIEWPORTS)

// Breakpoint sweep captures are desktop-like (fine pointer, no touch) at the swept width.
export function breakpointViewport(width: number): Viewport {
  return { name: `bp${width}`, width, height: 900, deviceScaleFactor: 1, touch: false, mobile: false }
}

// Everything the browser can observe about time, locale and fonts is fixed.
export const TIMEZONE = "UTC"
export const LOCALE = "en-US"

export const DEFAULT_TIMEOUT_MS = 30_000
