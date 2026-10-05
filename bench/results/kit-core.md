# Campfire.Kit's request layer against campfire_kit

Unit 3.1 (kit core). This is the kit alone, not the app: `bench/kit/run` serves the same five
endpoints from Campfire.Kit on Kestrel and from the Rust `campfire_kit` (unmodified, from `rust/`) on
hyper, on loopback, driven by `wrk -t4 -c64`. The full five-workload comparison with the reference
and Rust images is `bench/run` in Phase 7, once there is an app to put in an image.

The endpoints: a tiny page; a 100 KB page held in memory (ETag by SHA-256; with `Accept-Encoding:
gzip`, the gzip comes from the cache after the first request); the same page after verifying the
signed `session_token` cookie; and a form post (urlencoded body parsed, `Sec-Fetch-Site` checked,
redirect). Both servers wrap everything in the deflater, as `config.ru` does.

## What is measured

Requests per second saturate wrk at about 200,000 here, so the number to read is the server
process's CPU time per request (`ps` cputime, user plus system, over the run). Machine: Apple
silicon, 8-second runs, other work on the machine (load average 10 to 15), so each figure moves by
a few microseconds between runs. Two runs of each server are shown.

| Scenario | Campfire.Kit (us/request) | campfire_kit (us/request) |
|---|---|---|
| tiny page | 27.4, 22.8 | 22.4, 23.1 |
| 100 KB page, identity | 104.1, 105.3 | 78.1, 78.5 |
| 100 KB page, gzip (cached member) | 62.1, 62.7 | 67.5, 68.1 |
| signed cookie, then the page | 113.8, 116.1 | 87.8, 86.9 |
| form post, redirect | 29.9, 30.7 | 25.2, 26.8 |

Reading it:

- A response that is gzipped from the cache, which is what a browser gets for a page (the bench's
  load generator sends `Accept-Encoding: gzip` by default), costs 8% less CPU than the Rust kit's.
- Small requests are at parity to 20% over, run to run. Raw Kestrel with the kit's pre-routing
  middleware and no action costs about 24 us here, so most of a small request is the host and its
  syscalls, not the kit.
- Identity bodies of 100 KB cost about 25 us more in Kestrel: it copies a response into its 4 KB
  pipe blocks where hyper writes the `Bytes` as they are. That is the host, not something the adapter
  adds (SHA-256 of the 100 KB page is 35 us in .NET against 39 us in the Rust crate, so the hash is not it).
  Trying `UnsafePreferInlineScheduling` moved it by less than the noise; left for Phase 7's profile.
- Signed cookie verification is 5.7 us in .NET (`Cookies.verifySigned`, 4.5 KB allocated).

## The adapter's own cost

In process (a `DefaultHttpContext` through the pre-routing middleware, the adapter and the writer,
response to a null stream, after warm-up):

| | before | after |
|---|---|---|
| tiny page, bytes allocated per request on Kestrel | 3,839 | 2,463 |
| 100 KB page, bytes per request on Kestrel | 5,630 | 4,250 |
| tiny page, microseconds per request, steady state | 1.89 | 1.73 |

including about 0.8 us and 1.5 KB for building the `DefaultHttpContext`. Raw Kestrel with the
middleware allocates 560 bytes per request; the rest is `Ctx`, `Request`, the params maps, the
response and its headers, and the state machines of the tasks. `PerfTests` keeps a ceiling on bytes per
request so that this doesn't regress, and prints the numbers.

## Reproduce

    bench/kit/run both --secs 8     # needs wrk, the .NET SDK, and mise's rust (the Rust build is offline)
    bench/kit/run fsharp            # one side only
    target/kit-bench/fsharp/KitBench micro   # the cookie and hash building blocks, after a run of the above
