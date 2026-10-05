// The capture's only way out of its network namespace (parity/capture/sandbox/run.sh).
//
// Captures run with no network of their own (docker --network none, bwrap --unshare-net): only
// loopback, where the browsers talk to the harness's proxies (proxy.ts). Chromium fails every
// request in flight with ERR_NETWORK_CHANGED when an interface appears or goes in its namespace,
// and on the host network that happens whenever Docker starts or stops a container (a veth pair),
// which the harness itself does for every capture of a mutating state. So the proxies reach the
// servers under test through this forwarder instead, which runs on the host network and listens
// on a Unix socket in a directory both share. Each connection starts with one line naming the
// server, "127.0.0.1:3100\n"; after that bytes flow both ways untouched. Only loopback servers are
// reachable through it.
//
//   node capture/forward.ts SOCKET
import fs from "node:fs"
import net from "node:net"

export const UPSTREAM_SOCKET_ENV = "PARITY_UPSTREAM_SOCKET"
const LOOPBACK = new Set(["127.0.0.1", "localhost", "::1", "[::1]"])

// A connection to host:port, directly or through the forwarder when the capture has no network.
export function connectUpstream(host: string, port: number, onConnect?: () => void): net.Socket {
  const socketPath = process.env[UPSTREAM_SOCKET_ENV]
  if (!socketPath) return net.connect(port, host, onConnect)
  const socket = net.connect(socketPath, () => {
    socket.write(`${host}:${port}\n`)
    onConnect?.()
  })
  return socket
}

function serve(socketPath: string) {
  fs.rmSync(socketPath, { force: true })
  const server = net.createServer((client) => {
    let head = Buffer.alloc(0)
    const onData = (chunk: Buffer) => {
      head = Buffer.concat([head, chunk])
      const newline = head.indexOf(10)
      if (newline < 0) {
        if (head.length > 256) client.destroy()
        return
      }
      client.off("data", onData)
      client.pause()
      const target = head.subarray(0, newline).toString()
      const rest = head.subarray(newline + 1)
      const m = /^(.+):(\d+)$/.exec(target)
      if (!m || !LOOPBACK.has(m[1])) return client.destroy()
      const upstream = net.connect(Number(m[2]), m[1].replace(/^\[|\]$/g, ""), () => {
        if (rest.length) upstream.write(rest)
        client.pipe(upstream)
        upstream.pipe(client)
        client.resume()
      })
      const close = () => {
        client.destroy()
        upstream.destroy()
      }
      upstream.on("error", close)
      upstream.on("close", close)
      client.on("error", close)
      client.on("close", close)
    }
    client.on("data", onData)
    client.on("error", () => client.destroy())
  })
  server.listen(socketPath, () => fs.chmodSync(socketPath, 0o666))
  const stop = () => server.close(() => process.exit(0))
  process.on("SIGTERM", stop)
  process.on("SIGINT", stop)
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const socketPath = process.argv[2]
  if (!socketPath) {
    console.error("usage: node capture/forward.ts SOCKET")
    process.exit(2)
  }
  serve(socketPath)
}
