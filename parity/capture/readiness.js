// Test-only, injected by the harness with addInitScript. Reports whether the page is really ready:
// Stimulus controllers connected, Action Cable subscriptions known, fonts, images and posters
// decoded, Turbo idle. It reads the app's state and never changes it: the Stimulus application is
// already exposed as window.Stimulus (reference/app/javascript/controllers/application.js), and the
// Action Cable consumer is the one turbo-rails creates, reached through the page's own import map.
// Confirmation of each subscription is observed on the wire by the harness (see readiness.ts),
// because Action Cable keeps no "confirmed" flag on a Subscription.
;(() => {
  if (window.__parity) return
  const posterState = new Map() // poster URL -> "pending" | "done"
  let turboCable

  // Controllers with `static shouldLoad` false (soft_keyboard_controller.js off touch devices) are
  // never registered; importing the module (already loaded, or being loaded, by the app's eager
  // loader) tells them apart from ones that just haven't registered yet.
  const shouldLoad = new Map() // import-map key -> true | false | "pending"
  function expectsRegistration(key) {
    if (!shouldLoad.has(key)) {
      shouldLoad.set(key, "pending")
      import(key).then(
        (module) => shouldLoad.set(key, !(module.default && module.default.shouldLoad === false)),
        () => shouldLoad.set(key, false),
      )
    }
    return shouldLoad.get(key)
  }

  function expectedControllerIdentifiers() {
    // Mirrors @hotwired/stimulus-loading: every controllers/*_controller entry in the import map.
    const map = document.querySelector('script[type="importmap"]')
    if (!map) return []
    try {
      const imports = JSON.parse(map.textContent).imports || {}
      return Object.keys(imports)
        .filter((key) => /^controllers\/.*_controller$/.test(key))
        .map((key) => ({ key, id: key.replace(/^controllers\//, "").replace(/_controller$/, "").replace(/\//g, "--").replace(/_/g, "-") }))
    } catch {
      return []
    }
  }

  function describe(el) {
    return el.tagName.toLowerCase() + (el.id ? `#${el.id}` : "") + (el.className && typeof el.className === "string" ? "." + el.className.trim().split(/\s+/).join(".") : "")
  }

  function stimulusState() {
    const app = window.Stimulus
    const hasApp = !!(app && app.router)
    const scripts = document.querySelector('script[type="importmap"]')
    if (!hasApp) return { present: false, expectsApp: !!scripts, missing: [], unregistered: [], connected: 0 }
    const registered = app.router.modulesByIdentifier
    const unregistered = expectedControllerIdentifiers()
      .filter(({ id, key }) => !registered.has(id) && expectsRegistration(key) !== false)
      .map(({ id }) => id)
    const missing = []
    const unknown = new Set()
    let connected = 0
    for (const el of document.querySelectorAll("[data-controller]")) {
      for (const id of (el.getAttribute("data-controller") || "").split(/\s+/).filter(Boolean)) {
        if (!registered.has(id)) {
          unknown.add(id)
          continue
        }
        if (app.getControllerForElementAndIdentifier(el, id)) connected++
        else missing.push(`${describe(el)}:${id}`)
      }
    }
    return { present: true, missing, unregistered, unknown: [...unknown], connected }
  }

  async function cableState(socketSeen) {
    const streamSources = [...document.querySelectorAll("turbo-cable-stream-source")]
    const unsubscribedSources = streamSources.filter((el) => !el.subscription).length
    if (!socketSeen) return { consumer: false, open: false, identifiers: [], unsubscribedSources, streamSources: streamSources.length }
    try {
      turboCable ??= (await import("@hotwired/turbo-rails")).cable
      const consumer = await turboCable.getConsumer() // exists already: this page opened its socket
      return {
        consumer: true,
        open: consumer.connection.isOpen(),
        active: consumer.connection.isActive(), // open or connecting
        identifiers: consumer.subscriptions.subscriptions.map((s) => s.identifier),
        pending: consumer.subscriptions.guarantor.pendingSubscriptions.map((s) => s.identifier),
        unsubscribedSources,
        streamSources: streamSources.length,
      }
    } catch (error) {
      return { consumer: false, error: String(error), open: false, identifiers: [], unsubscribedSources, streamSources: streamSources.length }
    }
  }

  function inViewport(el) {
    const r = el.getBoundingClientRect()
    return r.bottom >= 0 && r.right >= 0 && r.top <= innerHeight && r.left <= innerWidth && r.width > 0 && r.height > 0
  }

  function mediaState() {
    const pendingImages = []
    for (const img of document.images) {
      if (!img.currentSrc && !img.src) continue
      if (img.loading === "lazy" && !inViewport(img)) continue
      // complete means fetched and decodable; calling img.decode() here was not neutral: Chromium
      // then rendered a large downscaled image (the lightbox) from a different decode.
      if (!img.complete) pendingImages.push(img.currentSrc || img.src)
    }
    const pendingVideos = []
    for (const video of document.querySelectorAll("video")) {
      if (video.poster) {
        const state = posterState.get(video.poster)
        if (state !== "done") {
          pendingVideos.push(`poster ${video.poster}`)
          if (!state) {
            posterState.set(video.poster, "pending")
            const img = new Image()
            img.src = video.poster
            img.decode().catch(() => {}).finally(() => posterState.set(video.poster, "done"))
          }
        }
      }
      if (video.preload !== "none" && video.readyState < 1 && !video.error && (video.currentSrc || video.src || video.querySelector("source"))) {
        pendingVideos.push(`video ${video.currentSrc || video.src}`)
      }
    }
    return { pendingImages, pendingVideos }
  }

  function scrollState() {
    let state = `${scrollX},${scrollY}`
    for (const el of document.querySelectorAll("[data-controller~=messages] , .messages, #message-area")) state += `;${el.scrollTop}`
    return state
  }

  function turboState() {
    const busy = []
    if (document.documentElement.hasAttribute("aria-busy")) busy.push("html[aria-busy]")
    for (const frame of document.querySelectorAll("turbo-frame[busy], turbo-frame[aria-busy='true']")) busy.push(describe(frame))
    const progress = document.querySelector(".turbo-progress-bar")
    if (progress && progress.isConnected && getComputedStyle(progress).opacity !== "0") busy.push("progress bar")
    return busy
  }

  // Where focus went and from what code, for diagnosing captures that differ in focus.
  const focusLog = []
  addEventListener("focusin", (event) => {
    const frames = (new Error().stack || "").split("\n").slice(2, 6).map((l) => l.trim().replace(/^at /, "").replace(/https?:\/\/[^/]+\/assets\//, "").replace(/-[0-9a-f]{8,}\.js/, ".js"))
    focusLog.push(`${describe(event.target)} <- ${frames.join(" < ")}`)
  }, true)

  // Where the mouse last was in this document (the harness moves it back there before a capture).
  let pointer = null
  addEventListener("mousemove", (event) => {
    if (event.isTrusted) pointer = [event.clientX, event.clientY]
  }, true)

  // Tags with a custom-element name that nothing ever defines: Action Text's attachment markup,
  // which Lexxy keeps as-is inside the editor (a mention in the edit form).
  const NEVER_DEFINED = new Set(["action-text-attachment"])

  window.__parity = {
    async snapshot(socketSeen) {
      const body = document.body
      const stimulus = stimulusState()
      return {
        readyState: document.readyState,
        fonts: document.fonts ? document.fonts.status : "loaded",
        stimulus,
        cable: await cableState(socketSeen),
        media: mediaState(),
        turboBusy: turboState(),
        undefinedElements: [...new Set([...document.querySelectorAll(":not(:defined)")].map((el) => el.localName))].filter((name) => !NEVER_DEFINED.has(name)),
        fingerprint: body ? `${body.getElementsByTagName("*").length}:${body.innerHTML.length}:${describe(document.activeElement || body)}:${scrollState()}:${stimulus.connected}` : "",
      }
    },

    // Pause every animation at the state's declared time; transitions (hover, focus) are finished
    // so the target style shows. Without a declared time, infinite animations are paused at 0 and
    // finite ones just before their end, so no animationend fires (the flash removes itself on it).
    focusLog() {
      return focusLog.slice()
    },

    pointer() {
      return pointer
    },

    pauseAnimations(at) {
      const report = []
      for (const animation of document.getAnimations()) {
        const name = animation.animationName || animation.transitionProperty || animation.id || animation.constructor.name
        if (typeof CSSTransition !== "undefined" && animation instanceof CSSTransition) {
          if (animation.playState === "running") animation.finish()
          report.push(`transition ${name}: finished`)
          continue
        }
        if (animation.playState === "finished") continue
        const timing = animation.effect ? animation.effect.getComputedTiming() : {}
        let time = at
        if (time === undefined || time === null) {
          time = Number.isFinite(timing.endTime) ? Math.max(0, timing.endTime - 1) : 0
        } else if (Number.isFinite(timing.endTime)) {
          time = Math.min(time, Math.max(0, timing.endTime - 1))
        }
        animation.pause()
        animation.currentTime = time
        report.push(`animation ${name}: paused at ${time}`)
      }
      return report
    },
  }
})()
