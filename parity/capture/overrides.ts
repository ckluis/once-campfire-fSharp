// The Rust app's own versions of reference assets (crates/assets/overrides, README "Known
// differences") differ from the reference's on purpose, and so do their digests. Where a capture
// names one by its digested path, the digest becomes a placeholder; every other digest-named asset
// still has to match exactly, since its digest is its content.
import fs from "node:fs"
import path from "node:path"
import { REPO_DIR } from "./config.ts"

const OVERRIDES_DIR = path.join(REPO_DIR, "crates/assets/overrides")

// Logical paths under the overrides directory, e.g. "models/file_uploader.js".
export function overriddenAssets(dir = OVERRIDES_DIR): string[] {
  if (!fs.existsSync(dir)) return []
  return fs.readdirSync(dir, { recursive: true, withFileTypes: true })
    .filter((entry) => entry.isFile())
    .map((entry) => path.relative(dir, path.join(entry.parentPath, entry.name)))
}

function escape(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")
}

export function maskOverriddenAssets(text: string, logicalPaths: string[] = OVERRIDDEN): string {
  return logicalPaths.reduce((masked, logical) => {
    const ext = path.extname(logical)
    const stem = logical.slice(0, logical.length - ext.length)
    const digested = new RegExp(`/assets/${escape(stem)}-[0-9a-f]{7,64}${escape(ext)}`, "g")
    return masked.replace(digested, `/assets/${stem}-«override»${ext}`)
  }, text)
}

const OVERRIDDEN = overriddenAssets()
