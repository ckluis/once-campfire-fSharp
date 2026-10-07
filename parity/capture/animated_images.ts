// Animated images (the /play sounds' WebPs in reference/app/assets/images/sounds) advance with
// wall-clock time, which no Web Animations API pauses and no engine setting freezes in all three
// browsers. The harness serves them reduced to their first frame, identically for both servers,
// so the capture shows a deterministic frame. Their bytes are compared elsewhere (assets).
import type { BrowserContext, Route } from "playwright"

export async function freezeAnimatedImages(context: BrowserContext, origin: string) {
  const handler = async (route: Route) => {
    const response = await route.fetch()
    const body = await response.body()
    const frozen = firstFrameWebp(body) ?? firstFrameGif(body)
    await route.fulfill({ response, body: frozen ?? body })
  }
  await context.route(`${origin}/**/*.webp`, handler)
  await context.route(`${origin}/**/*.gif`, handler)
}

// RIFF WebP: keep every chunk except ANMF frames after the first, and fix the RIFF size.
export function firstFrameWebp(buf: Buffer): Buffer | undefined {
  if (buf.length < 12 || buf.toString("ascii", 0, 4) !== "RIFF" || buf.toString("ascii", 8, 12) !== "WEBP") return
  const chunks: Buffer[] = []
  let frames = 0
  for (let offset = 12; offset + 8 <= buf.length;) {
    const size = buf.readUInt32LE(offset + 4)
    const end = offset + 8 + size + (size & 1)
    const fourcc = buf.toString("ascii", offset, offset + 4)
    if (fourcc === "ANMF") frames++
    if (fourcc !== "ANMF" || frames === 1) chunks.push(buf.subarray(offset, Math.min(end, buf.length)))
    offset = end
  }
  if (frames < 2) return
  const payload = Buffer.concat(chunks)
  const header = Buffer.alloc(12)
  header.write("RIFF", 0, "ascii")
  header.writeUInt32LE(payload.length + 4, 4)
  header.write("WEBP", 8, "ascii")
  return Buffer.concat([header, payload])
}

// GIF89a: keep everything up to the end of the first image, then the trailer.
export function firstFrameGif(buf: Buffer): Buffer | undefined {
  if (buf.length < 13 || !/^GIF8[79]a$/.test(buf.toString("ascii", 0, 6))) return
  const packed = buf[10]
  let offset = 13 + (packed & 0x80 ? 3 * (1 << ((packed & 0x07) + 1)) : 0)
  const skipSubBlocks = () => {
    while (offset < buf.length && buf[offset] !== 0) offset += buf[offset] + 1
    offset++
  }
  let images = 0
  let firstEnd = 0
  while (offset < buf.length) {
    const introducer = buf[offset]
    if (introducer === 0x21) { // extension: label, sub-blocks
      offset += 2
      skipSubBlocks()
    } else if (introducer === 0x2c) { // image descriptor, local color table, LZW min size, data
      const localPacked = buf[offset + 9]
      offset += 10 + (localPacked & 0x80 ? 3 * (1 << ((localPacked & 0x07) + 1)) : 0) + 1
      skipSubBlocks()
      images++
      if (images === 1) firstEnd = offset
    } else {
      break // 0x3b trailer, or garbage
    }
  }
  if (images < 2) return
  return Buffer.concat([buf.subarray(0, firstEnd), Buffer.from([0x3b])])
}
