# The remaining templates against the Rust views crate

Unit 4.3 (accounts, bots, custom styles, sessions, join, first run, users, profile, push subscriptions, PWA). Measured as
`views-hot-paths.md` measures the room page: `bin/views-differential bench` renders the busiest case of each kind with
the Rust `campfire_views` (askama, unmodified, from `rust/`) and with `Campfire.Views`, in one process each, with the
context and the view-model built once, 200 renders after 50 to warm up (rounds 4 x 50); ns per render, the middle of
three runs. Apple M4, Release builds, one thread. Nothing here was tuned: the whole-app baseline after Phase 5
(`bench/run`, step 5b) decides what to optimize.

Rust renders into the exact `String` askama returns; F# into a pooled writer, recorded, and copied out as the exact page
(`Render.page 0`, no `RenderSize`: a request's call site would size the buffer from the last render).

| Page | Bytes | F# (ns) | Rust (ns) | F# / Rust | F# bytes allocated |
|---|---|---|---|---|---|
| `accounts/edit`, the account page of the crowd seed (526 people) | 884,628 | 1,314,045 | 1,539,627 | 0.85 | 4,908,336 |
| `users/profiles/show`, the profile with the most rooms | 42,476 | 35,421 | 49,005 | 0.72 | 163,544 |
| `users/show`, a person's page | 24,888 | 7,790 | 8,813 | 0.88 | 46,704 |
| `sessions/new`, sign in | 24,997 | 8,771 | 10,716 | 0.82 | 52,840 |
| `pwa/manifest`, the JSON | 1,869 | 528 | 841 | 0.63 | 3,704 |

Reading it:

- F# is faster than Rust on every page here, as it is on the hot paths (`views-hot-paths.md`). The static text is
  copied from precomputed UTF-8 and the values are escaped straight into the buffer; askama writes through `fmt::Write`
  into a `String`.
- The account page is the one that allocates: 4.9 MB for an 885 KB page. Its 526 people are each an avatar, a form and
  buttons (about 1.7 KB of text each, from a dozen attribute lists), the writer starts at 4 KB and doubles, and
  `Render.page` copies the text out at the end, so the buffer's growth is two to three times the page. A call site
  that keeps a `RenderSize` (what the room page does) would start at the last length; not done here, because the account
  page is not one of the benchmarked workloads. The same page for an ordinary account (a few dozen people) is under
  100 KB.
- The manifest writes its seven JSON strings straight into the buffer (serde_json's escapes: quote, backslash and
  control characters) instead of building a string for each.
- The ordinary rows of `bin/views-differential bench` for these operations include building the case's view-model from
  JSON each time, which is the harness's cost (see `views-foundations.md`), so the table above is the pages alone.
