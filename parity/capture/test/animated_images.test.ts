import { test } from "node:test"
import assert from "node:assert/strict"
import fs from "node:fs"
import path from "node:path"
import { firstFrameGif, firstFrameWebp } from "../animated_images.ts"
import { REPO_DIR } from "../config.ts"

const sounds = path.join(REPO_DIR, "reference/app/assets/images/sounds")

test("reduces animated WebPs to one frame", () => {
  const original = fs.readFileSync(path.join(sounds, "56k.webp"))
  const frozen = firstFrameWebp(original)!
  assert.ok(frozen.length < original.length)
  assert.equal(frozen.toString("latin1").split("ANMF").length - 1, 1)
  assert.equal(frozen.readUInt32LE(4), frozen.length - 8)
})

test("leaves still WebPs alone", () => {
  assert.equal(firstFrameWebp(fs.readFileSync(path.join(sounds, "yay.webp"))), undefined)
})

test("reduces animated GIFs to one frame", () => {
  const original = fs.readFileSync(path.join(sounds, "56k.gif"))
  const frozen = firstFrameGif(original)
  assert.ok(frozen && frozen.length < original.length && frozen[frozen.length - 1] === 0x3b)
})
