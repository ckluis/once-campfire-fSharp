import { test } from "node:test"
import assert from "node:assert/strict"
import { maskDeliberateNetworkDifferences } from "../divergences.ts"

const rails = `GET /session/new (navigation) → 200
  headers: cache-control content-type etag set-cookie vary
  set-cookie: _campfire_session; httponly; path; samesite
  set-cookie: session_token; httponly; path; samesite
  body: sha256:1e79006cec2b3d32
GET /webmanifest.json → 200
  headers: cache-control content-type set-cookie
  content-type: application/json; charset=utf-8
  body: sha256:2c2aaf5a651296e1
`

const rust = `GET /session/new (navigation) → 200
  headers: cache-control content-type etag vary
  body: sha256:1e79006cec2b3d32
GET /webmanifest.json → 200
  headers: cache-control content-type
  content-type: application/json; charset=utf-8
  body: sha256:0b1c2d3e4f5a6b7c
`

test("masks session cookie writes and the manifest's body", () => {
  assert.equal(maskDeliberateNetworkDifferences(rails), maskDeliberateNetworkDifferences(rust))
})

test("other cookies and bodies still differ", () => {
  const other = rust.replace("body: sha256:1e79006cec2b3d32", "body: sha256:ffffffffffffffff")
  assert.notEqual(maskDeliberateNetworkDifferences(rails), maskDeliberateNetworkDifferences(other))
  const cookie = rust.replace("  headers: cache-control content-type etag vary\n", "  headers: cache-control content-type etag vary\n  set-cookie: flash; path\n")
  assert.notEqual(maskDeliberateNetworkDifferences(rails), maskDeliberateNetworkDifferences(cookie))
})
