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

## After the Phase 4 verifier's findings

The verifier noted that the hot paths still made strings and per-tag objects the performance rules rule out. What was
cheap to fix without profiling was fixed, with a test or differential case each:

- `data-` and `aria-` attribute names are made once per distinct key (an interned table; the keys are templates'
  literals), not by `"data-" + key.Replace(...)` on every call.
- `dom_id` for an attribute (`Turbo.domIdValue`) and `"view-transition-name: avatar-#{id}"` are an `AttrValue.Numbered`,
  a literal prefix and an integer the tag formats straight into the buffer (`w.Int`); `Turbo.writeDomId` does the same
  for a template's text. The `"prefix_model_"` part is cached per pair.
- `body_classes` is written into the layout (`Application.writeBodyClasses`), not joined from a list.
- `epochMs` is integer arithmetic (the nearest double to `nanos / 1e9` by `UInt128` division and a round-half-even
  step), no decimal string parsed back; a test checks it against the string version on 40,000 timestamps, whole
  milliseconds included, and the differential's `messages/epoch_ms` cases compare it with Rust.
- `Layouts.frame` takes the page's `RenderSize`, so a Turbo-Frame render of a room page starts from the size of the last
  one (Rust's `C::SIZE_HINT`), not from 4 KB doubled; the layout's own render adds the head and a 512-byte allowance.

Same machine and method as above (the middle of three runs; Rust unchanged: warm room page 12,866 ns, messages page
5,611 ns, cold 190,953 and 183,074):

| Page | F# (ns) | F# / Rust | F# bytes allocated | before |
|---|---|---|---|---|
| room page, fragment cache warm | 10,540 | 0.82 | 50,792 | 51,408 |
| messages page, cache warm | 3,439 | 0.61 | 2,928 | 2,928 |
| room page, cache cold | 149,273 | 0.78 | 682,576 | 695,672 |
| messages page, cache cold | 144,697 | 0.79 | 634,712 | 647,192 |

Times are within the runs' noise of the table above (the cold rows read a few percent slower this time, Rust's did too);
the allocation fell by 616 bytes a warm room page and about 13 KB a cold one (the 13 KB over 40 messages is most likely the
`data-`/`aria-` names and message ids that are no longer built per tag).

Not done, because the whole-app baseline after Phase 5 decides what is worth it: a warm room page still allocates 50 KB,
most of it (not profiled) the `Attrs` of its tags (an object and two arrays each, an `AttrValue voption` of a few words per
entry), and the route paths and `ctx.Asset` results are still strings, as `Campfire.Routes` returns them (a Phase 1
type, shared with the controllers). Pooling or slimming `Attrs`, and writer-form route helpers, are the next steps.
