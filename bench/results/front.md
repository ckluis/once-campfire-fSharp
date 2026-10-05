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

## Reproduce

    bench/front/run both --secs 6     # needs wrk, the .NET SDK, and mise's rust (the Rust build is offline)
    bench/front/run fsharp            # one side only
