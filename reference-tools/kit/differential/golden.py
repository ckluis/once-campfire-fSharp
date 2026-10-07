#!/usr/bin/env python3
"""Picks a sample of the inputs and the Rust kit's answers to them, as lines of
{"input": ..., "rust": ...} for tests/Campfire.Kit.Tests/differential.jsonl.

  golden.py INPUTS RUST_OUTPUT PER_KIND
"""
import collections
import json
import sys

inputs_path, rust_path, per_kind = sys.argv[1], sys.argv[2], int(sys.argv[3])
taken = collections.Counter()
for line_in, line_out in zip(open(inputs_path), open(rust_path)):
    case = json.loads(line_in)
    if taken[case["op"]] >= per_kind:
        continue
    taken[case["op"]] += 1
    print(json.dumps({"input": case, "rust": json.loads(line_out)}, ensure_ascii=False))
