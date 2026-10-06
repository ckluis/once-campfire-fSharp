# Campfire.Views's foundations against the Rust views crate

Unit 4.1 (views foundations). `bin/views-differential bench` renders the differential's cases with the Rust
`campfire_views` (askama, unmodified, from `rust/`) and with `Campfire.Views`, in one process each, and prints the time
per render. Nothing here was tuned: the whole-app baseline after Phase 5 (`bench/run`, step 5b) decides what to
optimize, and this records where the foundations start. Apple M4, Release builds, one thread, 40 rounds of the
~7,000 cases after a quarter as many to warm up; times are ns per render.

The first two rows are the ones to read: the page alone (the context built once, as a request builds it once, and
the template, its helpers and the layout rendering into a buffer that ends as the exact page) and the fragment cache
hit a room page makes for each of its ~40 messages. The other rows include building the `ViewContext` from the
case's JSON each time, which `System.Text.Json`'s `JsonElement` does more slowly than serde_json's `Value`, and (for
F#) decoding the answer into the string the comparison needs; they are the differential's cost as much as the
views'.

| Case | F# (ns) | Rust (ns) | F# / Rust | F# bytes allocated |
|---|---|---|---|---|
| welcome page, 21.6 KB (layout, lightbox, ~60 helper calls), context built once | 3,324 | 3,205 | 1.04 | 28,376 |
| fragment cache hit, one message of a room page (key built in the thread's buffer, looked up by span) | 96 | 114 | 0.84 | 48 |
| layout from parts, context built from JSON each time | 7,857 | 5,029 | 1.56 | 69,788 |
| welcome page, context built from JSON each time | 7,678 | 5,155 | 1.49 | 71,835 |
| pwa/_browser_settings | 2,818 | 2,182 | 1.29 | 6,289 |
| pwa/_install_instructions | 2,109 | 1,632 | 1.29 | 3,571 |
| pwa/_system_settings | 2,585 | 2,170 | 1.19 | 5,795 |
| accounts/_invite | 4,517 | 4,661 | 0.97 | 15,205 |
| users/_mention | 2,637 | 2,071 | 1.27 | 4,131 |
| layouts/_lightbox | 2,179 | 1,909 | 1.14 | 5,892 |
| messages/presentation (attachments, sounds) | 4,757 | 2,556 | 1.86 | 6,046 |
| helpers/form | 3,220 | 3,586 | 0.90 | 6,154 |
| helpers/tag | 774 | 763 | 1.01 | 1,358 |
| helpers/image_tag | 2,457 | 2,107 | 1.17 | 2,973 |
| fragment_cache/script (a script of fetches and gets with eviction) | 25,628 | 11,458 | 2.24 | 61,335 |

Reading it:

- The page itself is level with askama's (3.3 us against 3.2 us), and allocates the page's exact-size copy plus 7 KB
  (28.4 KB for a 21.6 KB page, `OutTests` pins "a page renders into a pooled buffer and allocates little more than
  itself"): the writer's array comes from `ArrayPool`, the attribute lists of the helpers are the rest. Rust's
  `render` allocates the page's `String` and grows it by doubling from the template's size hint.
- A fragment cache hit costs 96 ns, 16% less than Rust's (`thread_local!` store and mutex there, `AsyncLocal` and a
  monitor here), and allocates nothing in a template (the 48 bytes are the bench's own partial application of the key
  writer; a template writes it as a lambda, which is inlined).
- The ratios above 1.5 are the harness: `JsonElement` property lookups and the string the answer is decoded into
  (`welcome/show` allocates 72 KB with them and 28 KB without), `messages/presentation` parsing a message from
  JSON, and `fragment_cache/script` allocating a key string and a 1 KB fragment per step. None is on a request path.
- `bin/views-differential bench` prints these (and `bytes_allocated_per_render` for F# only); the numbers move a few
  percent from run to run.
