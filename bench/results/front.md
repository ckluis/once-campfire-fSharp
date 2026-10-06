# Campfire.Kit's front server against campfire_kit's

Unit 3.2 (kit front server). `bench/front/run` serves the kit bench's endpoints, and a cacheable asset, behind
each front server on loopback: Campfire.Kit's (`Front.serve`, Kestrel) and the Rust `campfire_kit::front::serve`
(unmodified, from `rust/`, hyper), driven by `wrk -t4 -c64`. Both put the app's `Rack::Deflater` around the app
as `config.ru` does, with the front's defaults (cache, compression with jitter, forwarded headers,
timeouts; no request log). The five-workload comparison with the reference and Rust images is `bench/run` in
Phase 7, once there is an app to put in an image.

Requests per second saturate wrk at about 200,000 here, so the number to read is the server process's CPU time
per request (`ps` cputime, user plus system, over the run). Apple silicon, 6-second runs, other work on the
machine.

| Scenario | Campfire.Kit (us/request) | campfire_kit (us/request) |
|---|---|---|
| tiny page | 30.3 | 29.7 |
| 100 KB page, identity | 109.0 | 85.3 |
| 100 KB page, gzip (cached member) | 64.1 | 72.8 |
| asset, front cache hit | 31.7 | 24.3 |
| asset, zstd, front cache hit | 42.4 | 74.2 |
| signed cookie, then the page | 116.8 | 94.1 |
| form post, redirect | 32.8 | 31.5 |

Reading it:

- The front adds about 1 to 1.5 us to a request: the same endpoints on Kestrel without it (`bench/kit/run
  fsharp`, same machine, same minute) took 28.8 us for the tiny page, 105.9 for the identity page, 62.8 for
  the gzip page and 31.7 for the form post. The page a browser gets (gzip, from the cached member) is 12%
  cheaper than behind Rust's front, and a small request is level with it.
- Identity bodies of 100 KB cost 24 us more, and a cache hit of a 20 KB asset 7 us more (about 5 of them the
  copy): Kestrel copies a response into its pipe where hyper writes the `Bytes` as they are. That is the
  host, as in `kit-core.md`; the front goes straight to it for these bodies and holds nothing back.
- The signed-cookie row is the 100 KB identity page (`/session` in `bench/front/fsharp/Program.fs`) with a signed
  cookie to read, so its gap to Rust is the identity page's gap plus the cookie's. The cookie's own cost is the
  difference of the two rows: 116.8 - 109.0 = 7.8 us in F# against 94.1 - 85.3 = 8.8 us in Rust. Reading and
  verifying a signed cookie is 1 us cheaper here; the 22.7 us between the rows is the 23.7 us Kestrel's copy of
  an identity body costs (above), not the cookie path, `MessageVerifier` or the cookie jar.
- Zstd (for clients that ask for zstd and not gzip) is 43% cheaper than Rust's libzstd at level 1 on a 20 KB
  body, in managed code.

## The front's own allocations

On Kestrel, bytes allocated per request (`GC.GetTotalAllocatedBytes` over a wrk run):

| | kit alone | behind the front |
|---|---|---|
| tiny page | 2,544 | 3,146 (3,378 before the front's first trimming) |
| asset, cache hit | | 674 (762 before) |

What the front keeps per request is its response object, the cache key, the forwarded headers, one task and
the `OnStarting` callback; the cache's lookup takes no lock, `Variant` is built only when a stored response
or a cacheable one needs it, and `X-Request-Start`'s text is shared by every request in a millisecond.

## Page parts: splice, ETag and gzip of a page of cached fragments

`FrontBench micro` and `splice` (`bench/front/rust/src/bin/splice.rs`) time what a page of 40 cached fragments
(10 KB each, a 15 KB layout before them and 1.4 KB after, 424 KB in all) costs a request once its gzip pieces
are stored: `PageParts::splice`, the ETag and the gzip member, on a fresh copy of the layout text each time
(as a template renders it). Single thread, steady state, microseconds per page:

| | Campfire.Kit | campfire_kit |
|---|---|---|
| splice (text hashed or recognised, parts built) | 1.12 | 1.28 |
| ETag (SHA-256 over the parts' digests) | 0.99 | 0.98 |
| gzip member from stored pieces (CRCs combined) | 1.17 | 1.45 |
| the whole page, with the copy of the text | 3.92 | 3.99-4.10 |

What got it there, from 5.5 us for the first version: the gzip step (1.58 us then) combines the parts' CRCs by
carry-less multiplication and Barrett's reduction where zlib's `multmodp` is a 32-step loop for each part;
the splice step (2.27 us then) recognises the layout text by comparing it with the text in a slot picked by a
few of its bytes (hashing 15 KB takes 0.6 us even on the CRC-32C instructions, and the compare, 0.25 us, is
what keeps a collision from handing back another text's pieces), and builds the parts and gaps into arrays of
their exact size.

## Reproduce

    bench/front/run both --secs 6     # needs wrk, the .NET SDK, and mise's rust (the Rust build is offline)
    bench/front/run fsharp            # one side only
    target/front-bench/fsharp/FrontBench micro 300000   # the page-parts table, after a run of the above
    target/front-bench/rust/release/splice 300000       # (cargo build --release --bin splice in bench/front/rust)
