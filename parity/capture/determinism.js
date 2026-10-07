// Test-only, injected by the harness with addInitScript into every document of both servers.
//
// Math.random is replaced with a seeded PRNG so client-generated ids (Lexxy's link input ids,
// composer_controller.js client message ids) are the same on every run. One global sequence isn't
// enough: modules evaluate in network order, so which caller drew first varied. Each call site
// (its script, with asset digests stripped, line and column) gets its own sequence instead.
;(() => {
  const counters = new Map()
  const hash = (text) => {
    let h = 0x811c9dc5
    for (let i = 0; i < text.length; i++) h = Math.imul(h ^ text.charCodeAt(i), 0x01000193)
    return h >>> 0
  }
  const mulberry32 = (seed) => {
    let t = (seed + 0x6d2b79f5) | 0
    t = Math.imul(t ^ (t >>> 15), 1 | t)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
  const callSite = () => {
    const lines = (new Error().stack || "").split("\n").filter((l) => l.trim() && !/^Error/.test(l))
    // [0] is callSite, [1] is random, [2] is the caller (V8, SpiderMonkey and JSC agree on this).
    return (lines[2] || "").replace(/-[0-9a-f]{7,64}(\.[a-z]+)/g, "$1").replace(/^\s*at\s+/, "").trim()
  }
  Math.random = function random() {
    const site = callSite()
    const n = (counters.get(site) || 0) + 1
    counters.set(site, n)
    return mulberry32(hash(`${site}#${n}`))
  }
})()

// Notification permission is reported as denied on every engine (headless engines disagree on
// the default), which is also what the notification-help states need (parity/screens.yml).
;(() => {
  if (typeof Notification === "undefined") return
  Object.defineProperty(Notification, "permission", { configurable: true, get: () => "denied" })
  Notification.requestPermission = () => Promise.resolve("denied")
})()

// CSS animations run on the document timeline, in real time, which no fake clock slows: the flash
// (appear-then-fade 3s after 300ms) faded out and removed itself (animationend) before capture on
// a slow run and not on a fast one. Every CSS animation is held paused from the start instead; the
// capture then sets each one to its state's declared time (readiness.js pauseAnimations), so what
// shows never depends on how long the steps took. Transitions still run (the app waits for some,
// with failsafe timers). An adopted stylesheet, so the page's DOM is untouched.
;(() => {
  try {
    const sheet = new CSSStyleSheet()
    sheet.replaceSync("*, *::before, *::after { animation-play-state: paused !important; }")
    document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet]
  } catch {}
})()

