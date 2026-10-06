// Port of rust/crates/views/templates/pwa/service_worker.js (reference/app/views/pwa/service_worker.js)
/// `pwa/service_worker.js`, served verbatim.
module Campfire.Views.Templates.Pwa.ServiceWorker

open Campfire.Views

/// The script, as text.
let js: string =
    "self.addEventListener(\"push\", async (event) => {\n" +
    "  const data = await event.data.json()\n" +
    "  event.waitUntil(Promise.all([ showNotification(data), updateBadgeCount(data.options) ]))\n" +
    "})\n" +
    "\n" +
    "async function showNotification({ title, options }) {\n" +
    "  return self.registration.showNotification(title, options)\n" +
    "}\n" +
    "\n" +
    "async function updateBadgeCount({ data: { badge } }) {\n" +
    "  return self.navigator.setAppBadge?.(badge || 0)\n" +
    "}\n" +
    "\n" +
    "self.addEventListener(\"notificationclick\", (event) => {\n" +
    "  event.notification.close()\n" +
    "\n" +
    "  const url = new URL(event.notification.data.path, self.location.origin).href\n" +
    "  event.waitUntil(openURL(url))\n" +
    "})\n" +
    "\n" +
    "async function openURL(url) {\n" +
    "  const clients = await self.clients.matchAll({ type: \"window\" })\n" +
    "  const focused = clients.find((client) => client.focused)\n" +
    "\n" +
    "  if (focused) {\n" +
    "    await focused.navigate(url)\n" +
    "  } else {\n" +
    "    await self.clients.openWindow(url)\n" +
    "  }\n" +
    "}\n"

/// The script as UTF-8, encoded once.
let bytes: byte[] = Utf8.lit js
