#!/usr/bin/env python3
"""Compares the outputs of the Rust and F# differential tools line by line.

  compare.py INPUTS RUST_OUTPUT FSHARP_OUTPUT [SHOWN]

Prints the first few inputs whose answers differ, and a count per kind of input; exits 1 if any do.
Answers are compared as JSON values (an integer is not a float, and key order counts), not as text:
the two sides write a float differently (`1.5e-7` or `0.00000015`) and mean the same number.
"""
import collections
import json
import sys

inputs_path, rust_path, fsharp_path = sys.argv[1:4]
shown_limit = int(sys.argv[4]) if len(sys.argv) > 4 else 5
inputs = [json.loads(line) for line in open(inputs_path)]
rust = [json.loads(line) for line in open(rust_path)]
fsharp = [json.loads(line) for line in open(fsharp_path)]
if not len(inputs) == len(rust) == len(fsharp):
    sys.exit("lengths differ: inputs %d, rust %d, f# %d" % (len(inputs), len(rust), len(fsharp)))


def same(a, b):
    if isinstance(a, bool) or isinstance(b, bool):
        return type(a) is type(b) and a == b
    if isinstance(a, int) and isinstance(b, int):
        return a == b
    if isinstance(a, float) and isinstance(b, float):
        return a == b
    if isinstance(a, dict) and isinstance(b, dict):
        return list(a.keys()) == list(b.keys()) and all(same(a[k], b[k]) for k in a)
    if isinstance(a, list) and isinstance(b, list):
        return len(a) == len(b) and all(same(x, y) for x, y in zip(a, b))
    return type(a) is type(b) and a == b


totals = collections.Counter()
differing = collections.Counter()
shown = 0
for i, (case, r, f) in enumerate(zip(inputs, rust, fsharp)):
    totals[case["op"]] += 1
    if not same(r, f):
        differing[case["op"]] += 1
        if shown < shown_limit:
            shown += 1
            print("input:", json.dumps(case, ensure_ascii=False)[:500])
            print("  rust:", json.dumps(r, ensure_ascii=False)[:500])
            print("  f#:  ", json.dumps(f, ensure_ascii=False)[:500])
for op in totals:
    print("%-10s %6d inputs, %d differ" % (op, totals[op], differing[op]))
sys.exit(1 if differing else 0)
