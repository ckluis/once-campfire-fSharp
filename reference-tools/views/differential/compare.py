#!/usr/bin/env python3
"""Compares the Rust and F# answers of bin/views-differential, case by case and byte for byte.

  compare.py cases.jsonl rust.jsonl fsharp.jsonl

Prints how many cases were rendered and how many are identical, per operation, then the first
differences (the case, and the bytes around the first one that differs). Exits 1 on any difference or
error answer.
"""
import json
import sys
from collections import defaultdict

cases = {}
for line in open(sys.argv[1], encoding="utf-8"):
    case = json.loads(line)
    if "id" in case:
        cases[case["id"]] = case


def load(path):
    answers = {}
    for line in open(path, encoding="utf-8"):
        answer = json.loads(line)
        answers[answer["id"]] = answer
    return answers


rust, fsharp = load(sys.argv[2]), load(sys.argv[3])
total = identical = 0
per_op = defaultdict(lambda: [0, 0])
failures = []
for id, case in cases.items():
    op = case["op"]
    total += 1
    per_op[op][0] += 1
    a, b = rust.get(id), fsharp.get(id)
    if a is None or b is None:
        failures.append((id, "missing answer: rust=%s fsharp=%s" % (a is not None, b is not None)))
    elif "error" in a or "error" in b:
        # an operation that fails the same way on both sides (a panic against an exception) is not a difference
        failures.append((id, "error: rust=%r fsharp=%r" % (a.get("error"), b.get("error"))))
    elif a == b:
        identical += 1
        per_op[op][1] += 1
    else:
        out_a, out_b = a["out"].encode("utf-8"), b["out"].encode("utf-8")
        if out_a != out_b:
            at = next((i for i in range(min(len(out_a), len(out_b))) if out_a[i] != out_b[i]), min(len(out_a), len(out_b)))
            lo = max(0, at - 40)
            failures.append(
                (id, "first difference at byte %d (rust %d bytes, fsharp %d)\n    rust:   %r\n    fsharp: %r" % (at, len(out_a), len(out_b), out_a[lo : at + 60].decode("utf-8", "replace"), out_b[lo : at + 60].decode("utf-8", "replace")))
            )
        else:
            failures.append((id, "same text, different parts: rust=%r fsharp=%r" % ({k: v for k, v in a.items() if k != "out"}, {k: v for k, v in b.items() if k != "out"})))

for op in sorted(per_op):
    rendered, same = per_op[op]
    print(f"  {op:40s} {same:5d}/{rendered:<5d}{'' if same == rendered else '  DIFFERENT'}")
print(f"views differential: {identical} of {total} renders identical")
for id, message in failures[:10]:
    print(f"DIFFERENT {id}: {message}")
if len(failures) > 10:
    print(f"... and {len(failures) - 10} more")
sys.exit(1 if failures else 0)
