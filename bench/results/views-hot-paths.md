# The hot-path templates against the Rust views crate

Unit 4.2 (rooms, messages, search, sidebar, mentions prompt). `bin/views-differential bench` renders a busy room page
and a page of messages with the Rust `campfire_views` (askama, unmodified, from `rust/`) and with `Campfire.Views`, in
one process each, as the app serves them: recorded (`render_sized!` there, `RenderSize.Render` here), into a buffer
sized from the last render, with the context built once. Nothing here was tuned: the whole-app baseline after Phase 5
(`bench/run`, step 5b) decides what to optimize, and this records where the templates start. Apple M4, Release
builds, one thread, 1,000 renders after 250 to warm up (100 for the cold rows); ns per render, the middle of three runs
(they agree within 2%).

The pages are the first cases with the most messages in the differential's generated set, taken from the parity seed:
the room page is "All Talk" (a closed room, its last 40 of 131 messages), 411,439 bytes of which the 40 fragments are the
messages; the messages page is the same room's `messages/index`, 378,208 bytes, 40 fragments. The seed's messages are
the reference's fixtures: text, a few with boosts or an attachment.

| Page | F# (ns) | Rust (ns) | F# / Rust | F# bytes allocated |
|---|---|---|---|---|
| room page, fragment cache warm (every message already rendered) | 10,516 | 12,688 | 0.83 | 51,408 |
| messages page, cache warm | 3,405 | 5,608 | 0.61 | 2,928 |
| room page, cache cold (emptied before each render, so all 40 messages render) | 146,336 | 185,011 | 0.79 | 695,672 |
| messages page, cache cold | 135,240 | 177,549 | 0.76 | 647,192 |

Reading it:

- A warm page is a layout, a few attribute lists and 40 fragments handed to the recorded page, not copied: the text of
  the room page is ~11 KB and its 40 fragments are 400 KB that are never written again, which is why the messages page
  (no layout, no composer) allocates 2.9 KB, nearly all the recorded page's own arrays.
- A cold page is the 40 `messages/_message` renders (about 3.4 us each; Rust 4.4), each into its own pooled writer and kept
  as the cache's exact-size copy, so the 650 KB allocated is the 40 fragments' bytes (378 KB) and the rest of what a
  render makes (their attribute lists and the key strings of the cache).
- These are the template and cache alone. The rows `bin/views-differential bench` prints per operation include building
  the case's view-model from JSON each time (`JsonElement` against serde_json's `Value`), which is the harness's cost:
  `rooms/show` shows 192 us against Rust's 94 us there, and `messages/index` 85 against 43. They are not the pages' cost.
- The room page's warm 51 KB is what is left once the messages are fragments: the recorded page's exact-size text copy
  (11 KB), the layout's and the helpers' attribute lists, the strings of the page's few URLs and the closures of the
  layout's regions. No string is made for a message. Where it all goes was not profiled (Phase 7).
