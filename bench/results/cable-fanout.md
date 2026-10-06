# Campfire.Cable's fan-out against campfire_cable

Unit 3.3 (cable). This is the cable alone, not the app: `bench/cable/run` serves the same two endpoints from
Campfire.Cable on Kestrel and from the Rust `campfire_cable` (unmodified, from `rust/`) on axum, on loopback,
and `bench/cable/driver.py` connects 1,000 Action Cable clients to each. `GET /cable` is the endpoint, and
`POST /broadcast` broadcasts Turbo Stream appends of about 600 bytes of HTML to the stream the clients
subscribed to. The full comparison with the reference and Rust images is `bench/run` (its `cable` suite) in
Phase 7, once there is an app to put in an image.

Machine: Apple silicon, 10 cores, macOS; the driver (one Python process) shares it with the server, so it is
the slower side and latencies are the driver's. Numbers are for 1,000 clients and 600-byte messages, two runs
of each, the second in the same server process as the first (so its memory figure only says what the first
left behind). Each run warms the server first: 30 single broadcasts and 3 bursts of 200 that aren't measured, so
the JIT has compiled what a server that has been up has.

## What is measured

- **latency**: a POST of one message to the last client having it (p50 and worst of 60 rounds). The driver
  reading 1,000 frames is most of it; both servers sit at the same 12 to 19 ms.
- **single**: the server process's CPU per delivered message over those 60 lone broadcasts (`ps`, user plus
  system). A lone broadcast gives nothing to batch, and each write wakes a client that is waiting, which the
  kernel pays for; it also counts the idle time between the rounds (about 10 ms of CPU a second for
  1,000 idle clients, the 3-second ping among it).
- **burst**: one POST that broadcasts 200 messages (the stream capacity is 256) back to back, and the CPU per
  delivered message (200,000 of them). Connections write whatever is ready in one write, so this is what a
  server costs when the work is the writing, whatever the driver's speed.
- **memory**: resident set with 1,000 idle clients subscribed, per client, from the first run only.

| | latency p50 | single (us/delivery) | burst (us/delivery) | memory per client |
|---|---|---|---|---|
| Rust, plain | 18.2, 18.8 ms | 18.7, 18.0 | 0.60, 0.65 (user 0.15, system 0.45) | 9.3 kB |
| F#, plain | 12.6, 13.7 ms | 47.8, 41.3 | 1.75, 1.60 (user 0.85, system 0.65 to 0.90) | 30.3 kB |
| F#, plain, tuned host | 14.8, 12.7 ms | 31.5, 29.7 | 1.20, 1.15 | 32.0 kB |
| Rust, permessage-deflate | 17.5, 17.5 ms | 15.3, 16.8 | 0.40, 0.40 | |
| F#, permessage-deflate | 16.1, 16.5 ms | 26.8, 24.8 | 0.35, 0.40 | |
| F#, permessage-deflate, tuned host | 16.3, 16.7 ms | 22.2, 22.5 | 0.40, 0.30 | |

"Tuned host" is `bench/cable/run fsharp --tuned`: the thread pool's workers don't spin
(`DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=0`, runtimeconfig `System.Threading.ThreadPool.UnfairSemaphoreSpinLimit`)
and Kestrel's socket transport sends where the write is made (`SocketTransportOptions.UnsafePreferInlineScheduling`).
They are properties of the host the cable is mounted in, so the cable can't set them; they are for the app's
`runtimeconfig.json` and Kestrel setup (Phase 5), to be judged against the HTTP workloads in Phase 7.

## Reading it

- With compression, which is what a browser negotiates, bursts cost the same CPU as Rust's (0.3 to 0.4 us per
  delivery): one deflate per broadcast, shared by every subscriber, and a frame of about 110 bytes to write.
- Uncompressed, the F# server costs 2 to 3 times Rust's CPU per delivery (1.6 to 1.75 us against 0.6 to 0.65),
  1.9 to 2 times with the host tuned. The cable's own share of that is small: in process, with sockets that take
  bytes and say nothing, a delivery costs 0.3 to 0.4 us and allocates 28 bytes (see below). The rest is the
  host: a write through Kestrel goes into a pipe, which a transport loop sends from, and the CPU per delivery
  grows with the bytes where Rust's `writev` of the shared payload does not copy them in user space. Writing to
  the transport's pipe directly instead of Kestrel's response (`IConnectionTransportFeature`) measured the
  same, and writing to the socket directly is not possible, because Kestrel's own write of the 101 response
  can still be in its pipe when the first frame is sent (it came out ahead of the 101 in an experiment).
- A lone broadcast costs more than it does in Rust for the same reason, plus a thread pool that spins looking for
  the next of the 1,000 continuations it has just been given: with the spinning off the in-process cost of a lone
  broadcast halves (4.7 to 2.1 us per delivery).
- Memory per connection is about 9 kB in the cable (in process, `GC.GetTotalMemory` before and after 1,000
  connections), and 20 to 30 kB with Kestrel's per-connection objects and buffers; Rust's resident set
  per client is 9 kB. The process itself is 68 MB at boot against 3 MB (the runtime and Server GC, which also
  lets the heap grow to 150 to 300 MB over a burst before it collects); Phase 7's.
- Rejected, measured: running a connection's continuations inline on the publishing thread (15 us per delivery:
  nothing is batched, because each message is written before the next is published) and a thread of the cable's
  own for connections like Rust's (no better than the pool with its spinning off).

## In process

`tests/Campfire.Cable.Tests/FanOutTests.fs` runs 1,000 connections on quiet sockets (what is written is counted
and checked against the frames it should be, byte for byte) and broadcasts through the same path, so the hub,
the connections and the writer are measured and Kestrel and the kernel are not. Median of 20 rounds of 100
messages, or of 100 lone messages, with the whole process's CPU:

| | per delivery | allocated per delivery | writes per connection per round |
|---|---|---|---|
| burst of 100, plain | 0.34 to 0.40 us | 28 B | 5 to 8 |
| burst of 100, deflate | 0.29 to 0.30 us | 30 B | 4 |
| lone message, plain | 4.0 to 4.3 us (2.1 with the pool not spinning) | 29 B | 1 |
| lone message, deflate | 4.3 to 4.5 us (2.2) | 31 B | 1 |

A subscription holds about 9 kB while idle. Publishing one message to 1,000 subscribers takes 25 to 60 us on the
publishing thread (a frame built once for all of them, 1,000 wakes).

### Before and after

Writing a batch of frames used to build an array of their payloads and go through a `Task` per write; it now
makes the write without a task when the socket takes it at once, and the connection reads the internal channel
without a reference cell per wake. That took the allocation per delivery in a burst from 72 to 28 bytes and the
lone-message allocation from 694 (the test's own assertions included, which were then removed from the loop) to 29.
