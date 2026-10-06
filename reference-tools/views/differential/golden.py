#!/usr/bin/env python3
"""Picks a sample of the cases and the Rust views crate's answers to them, as lines of
{"case": ..., "rust": ...} (after the `shared` line) for tests/Campfire.Views.Tests/differential.jsonl.
A few of each operation, more of the small ones; the every-code-point cases are sampled evenly.

  golden.py CASES RUST_OUTPUT
"""
import collections
import json
import sys

cases_path, rust_path = sys.argv[1], sys.argv[2]
QUOTA = {
    "layouts/application_wrapper": 6, "welcome/show": 6, "recorded/page": 12, "helpers/users": 40, "helpers/rooms": 20,
    "helpers/application": 24, "pwa/_browser_settings": 24, "pwa/_install_instructions": 24, "pwa/_system_settings": 24,
    "users/_mention": 16, "fragment_cache/keys": 24, "messages/epoch_ms": 8, "messages/iso8601": 4, "messages/json_time": 4,
    "rooms/show": 6, "users/sidebars/show": 4, "searches/index": 4, "messages/index": 4, "messages/_message": 16,
    "rooms/opens/new": 4, "rooms/opens/edit": 4, "rooms/closeds/new": 4, "rooms/closeds/edit": 4, "rooms/directs/new": 4, "rooms/directs/edit": 4,
    "messages/_actions": 8, "messages/show": 6, "messages/edit": 8, "rooms/refreshes/show": 6, "messages/message_cached": 4,
    "helpers/application:to_lowercase": 100,
    # unit 4.3: pages carry the whole layout (~50 KB), so a couple of each, a framed one among them
    "accounts/edit": 2, "accounts/bots/index": 2, "accounts/bots/new": 2, "accounts/bots/edit": 3, "accounts/custom_styles/edit": 2,
    "first_runs/show": 2, "sessions/new": 2, "sessions/incompatible_browser": 2, "sessions/transfers/show": 2, "users/new": 2,
    "users/show": 4, "users/profiles/show": 3, "users/push_subscriptions/index": 2, "users/_ban_button": 6, "users/avatars/show": 6,
    "users/profiles/_membership": 6, "users/profiles/_transfer": 6, "accounts/users/_user": 12, "pwa/manifest": 12,
}
DEFAULT = 8
# A page's answer can be hundreds of kilobytes (a room with 40 messages): the sample keeps one such answer per kind of case,
# which is what shows recorded fragments, and otherwise takes the smaller ones.
BIG = 60_000
# an account page of a crowd of hundreds is nearly a megabyte (twice, with its recorded text); bin/views-differential covers it live
HUGE = 400_000
EVEN = ("capitalize_each", "initials_each")

answers = {}
for line in open(rust_path):
    answer = json.loads(line)
    answers[answer["id"]] = answer
rows = []
for line in open(cases_path):
    case = json.loads(line)
    if "shared" in case:
        print(line.strip())
        continue
    rows.append((case, answers[case["id"]]))

taken = collections.Counter()
even = [(c, r) for c, r in rows if c.get("args", {}).get("name") in EVEN]
step = max(1, len(even) // 10)
chosen = {c["id"] for c, _ in even[::step]}
for case, answer in rows:
    op = case["op"]
    if case.get("args", {}).get("name") == "to_lowercase":
        op += ":to_lowercase"
    if case.get("args", {}).get("name") in EVEN:
        keep = case["id"] in chosen
    else:
        size = len(answer.get("out", ""))
        if size > HUGE:
            keep = False
        elif size > BIG:
            keep = taken[op + " (big)"] < 1 and taken[op] < QUOTA.get(op, DEFAULT)
            taken[op + " (big)"] += keep
        else:
            keep = taken[op] < QUOTA.get(op, DEFAULT)
        taken[op] += keep
    if keep:
        # the replay compares `out` and `fragments`; `text` (a recorded page without its fragments) is `out` again for most pages
        answer = {key: value for key, value in answer.items() if key != "text"}
        print(json.dumps({"case": case, "rust": answer}, ensure_ascii=False))
