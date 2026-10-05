#!/usr/bin/env python3
"""Compares the outputs of the Rust and F# differential tools line by line.

  compare.py parse|pipeline RUST_OUTPUT FSHARP_OUTPUT INPUTS [SHOWN]
"""
import json
import sys

mode, rust_path, fsharp_path, inputs_path = sys.argv[1:5]
shown_limit = int(sys.argv[5]) if len(sys.argv) > 5 else 5
labels = (
    ["body", "table", "tr", "td", "select", "template", "head", "html", "colgroup", "tbody", "caption", "frameset", "title", "textarea", "script", "style", "plaintext", "noscript", "p", "a"]
    if mode == "parse"
    else ["presentation", "plain text", "editable value", "mentioned users"]
)
rust = [json.loads(line) for line in open(rust_path)]
fsharp = [json.loads(line) for line in open(fsharp_path)]
inputs = [json.loads(line) for line in open(inputs_path)]
if not len(rust) == len(fsharp) == len(inputs):
    sys.exit("lengths differ: rust %d, f# %d, inputs %d" % (len(rust), len(fsharp), len(inputs)))
errors = sum(1 for r in rust for outcome in r if "err" in outcome)
differing = 0
for i, (r, f) in enumerate(zip(rust, fsharp)):
    if r != f:
        differing += 1
        if differing <= shown_limit:
            for label, a, b in zip(labels, r, f):
                if a != b:
                    print("input:", json.dumps(inputs[i])[:600])
                    print(label)
                    print("  rust:", json.dumps(a)[:700])
                    print("  f#:  ", json.dumps(b)[:700])
                    break
print("%s: %d inputs, %d differ (%d of the outputs are errors)" % (mode, len(rust), differing, errors))
sys.exit(1 if differing else 0)
