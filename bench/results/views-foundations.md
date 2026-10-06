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
| welcome page, 21.6 KB (layout, lightbox, ~60 helper calls), context built once | 3,271 | 3,124 | 1.05 | 27,688 |
| fragment cache hit, one message of a room page (key built in the thread's buffer, looked up by span) | 84 | 115 | 0.73 | 48 |
| layout from parts, context built from JSON each time | 7,674 | 5,192 | 1.48 | 69,705 |
| welcome page, context built from JSON each time | 7,686 | 5,370 | 1.43 | 71,816 |
| pwa/_browser_settings | 2,780 | 2,170 | 1.28 | 6,154 |
| pwa/_install_instructions | 2,093 | 1,713 | 1.22 | 3,910 |
| pwa/_system_settings | 2,615 | 2,177 | 1.20 | 5,711 |
| accounts/_invite | 4,730 | 4,691 | 1.01 | 14,945 |
| users/_mention | 2,616 | 2,109 | 1.24 | 4,099 |
| layouts/_lightbox | 2,405 | 1,937 | 1.24 | 5,850 |
| messages/presentation (attachments, sounds) | 4,376 | 2,604 | 1.68 | 5,874 |
| helpers/form | 3,424 | 3,866 | 0.89 | 6,922 |
| helpers/tag | 804 | 796 | 1.01 | 1,401 |
| helpers/image_tag | 2,584 | 2,167 | 1.19 | 2,990 |
| fragment_cache/script (a script of fetches and gets with eviction) | 19,042 | 10,276 | 1.85 | 53,843 |

Reading it:

- The page itself is level with askama's (3.3 us against 3.1 us), and allocates the page's exact-size copy plus 6 KB
  (27.7 KB for a 21.6 KB page, `OutTests` pins "a page renders into a pooled buffer and allocates little more than
  itself"): the writer's array comes from `ArrayPool`, the attribute lists of the helpers are the rest. Rust's
  `render` allocates the page's `String` and grows it by doubling from the template's size hint.
- A fragment cache hit costs 84 ns, 27% less than Rust's (`thread_local!` store and mutex there, `AsyncLocal` and a
  monitor here), and allocates nothing in a template (the 48 bytes are the bench's own partial application of the key
  writer; a template writes it as a lambda, which is inlined).
- The ratios above 1.5 are the harness: `JsonElement` property lookups and the string the answer is decoded into
  (`welcome/show` allocates 72 KB with them and 28 KB without), `messages/presentation` parsing a message from
  JSON, and `fragment_cache/script` allocating a key string and a 1 KB fragment per step. None is on a request path.
- `bin/views-differential bench` prints these (and `bytes_allocated_per_render` for F# only); the numbers move a few
  percent from run to run.
