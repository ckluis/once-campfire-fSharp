import { test } from "node:test"
import assert from "node:assert/strict"
import { maskOverriddenAssets, overriddenAssets } from "../overrides.ts"

test("masks the digests of overridden assets only", () => {
  const text = "GET /assets/models/file_uploader-d3a5c44b.js → 200\nGET /assets/models/message_paginator-0123abcd.js → 200\n"
  assert.equal(
    maskOverriddenAssets(text, ["models/file_uploader.js"]),
    "GET /assets/models/file_uploader-«override».js → 200\nGET /assets/models/message_paginator-0123abcd.js → 200\n",
  )
})

test("finds the repository's overrides", () => {
  assert.ok(overriddenAssets().includes("models/file_uploader.js"))
})
