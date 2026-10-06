#!/usr/bin/env python3
"""Cuts an askama template (rust/crates/views/templates/...) into its literal text and its tags, with
askama's whitespace control (`{%-`, `-%}`, `{{-`, `-}}`, `{#-`, `-#}`) applied, and prints an F# skeleton:
each literal as a `Utf8.lit` binding (exact bytes, so a template port can't mistype its text) and
the render steps in order, with the original tag as a comment to translate by hand.

  reference-tools/views/transcribe.py layouts/application.html

The skeleton is a starting point: tags become `w.Text`, `w.Raw`, helper calls, `if`/`for` and block
functions by hand (see the module comment at the top of src/Campfire.Views/Core/Out.fs).
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2] / "rust/crates/views/templates"
TOKEN = re.compile(r"(\{\{[-+~]?.*?[-+~]?\}\}|\{%[-+~]?.*?[-+~]?%\}|\{#[-+~]?.*?[-+~]?#\})", re.S)


def fsharp_string(text):
    out = []
    for ch in text:
        if ch == "\\":
            out.append("\\\\")
        elif ch == '"':
            out.append('\\"')
        elif ch == "\n":
            out.append("\\n")
        elif ch == "\t":
            out.append("\\t")
        elif ch == "\r":
            out.append("\\r")
        else:
            out.append(ch)
    return '"' + "".join(out) + '"'


def parts(source):
    pieces = TOKEN.split(source)
    items = []  # ("lit", text) | ("tag", text)
    for i, piece in enumerate(pieces):
        items.append(("tag" if i % 2 else "lit", piece))
    # whitespace control
    for i, (kind, text) in enumerate(items):
        if kind != "tag":
            continue
        inner_start = text[2]
        inner_end = text[-3]
        if inner_start == "-" and i > 0:
            items[i - 1] = ("lit", items[i - 1][1].rstrip(" \t\r\n"))
        if inner_end == "-" and i + 1 < len(items):
            items[i + 1] = ("lit", items[i + 1][1].lstrip(" \t\r\n"))
    return [(k, t) for k, t in items if not (k == "lit" and t == "")]


def pascal(name):
    return "".join(part[:1].upper() + part[1:] for part in name.split("_"))


def split_args(text):
    """Splits `a, f(b, c), "d,e"` at the top-level commas."""
    args, depth, current, quote = [], 0, "", False
    for ch in text:
        if ch == '"':
            quote = not quote
        if not quote:
            if ch in "([{":
                depth += 1
            elif ch in ")]}":
                depth -= 1
            elif ch == "," and depth == 0:
                args.append(current.strip())
                current = ""
                continue
        current += ch
    if current.strip():
        args.append(current.strip())
    return args


def fs_attrs(chain):
    """`h::attrs().aria_hidden().size(20)` as the F# builder chain."""
    chain = chain.replace("h::attrs()", "@@")
    chain = re.sub(r"\.([a-z_0-9]+)\(", lambda m: "." + pascal(m.group(1).rstrip("_")) + "(", chain)
    return chain.replace("@@", "Tag.attrs()")


def fs_path(path):
    """A field path like `ctx.platform.operating_system` as F#, or None if it is anything else."""
    path = re.sub(r"\.as_(str|ref|deref)\(\)", "", path)
    if not re.fullmatch(r"[a-z_][a-z_0-9]*(\.[a-z_][a-z_0-9]*)*", path):
        return None
    head, *rest = path.split(".")
    return ".".join([head] + [pascal(part) for part in rest])


def fs_condition(cond):
    cond = re.sub(r"!\s*\(", "not (", cond)
    cond = re.sub(r"!\s*([a-z_][a-z_0-9.]*)", lambda m: "not " + (fs_path(m.group(1)) or m.group(1)), cond)
    return re.sub(r"[a-z_][a-z_0-9]*(?:\.[a-z_][a-z_0-9]*)+(?:\(\))?", lambda m: fs_path(m.group(0).replace("()", "")) or m.group(0), cond)


def fs_expression(expr):
    """The statement an interpolation `{{ expr }}` is, when it is one of the common forms."""
    expr = expr.strip().rstrip("-").strip()
    m = re.fullmatch(r"h::image_tag\(ctx, (.*)\)", expr, re.S)
    if m:
        args = split_args(m.group(1))
        if len(args) == 2:
            return f"Assets.imageTag w ctx {args[0]} ({fs_attrs(args[1])})"
    m = re.fullmatch(r"h::capitalize\((.*)\)", expr)
    if m and fs_path(m.group(1)):
        return f"w.Text(Application.capitalize {fs_path(m.group(1))})"
    m = re.fullmatch(r"ctx\.url\((.*)\)", expr)
    if m:
        return f"w.Text(ctx.Url({m.group(1)}))"
    if fs_path(expr):
        return f"w.Text {fs_path(expr)}"
    return None


def main():
    path = sys.argv[1]
    source = (ROOT / path).read_text()
    # Askama drops one trailing newline from a template file (the tail of an `include` too).
    if source.endswith("\n"):
        source = source[:-1]
    items = parts(source)
    print(f"// transcribed from rust/crates/views/templates/{path} (reference-tools/views/transcribe.py)")
    n = 0
    steps = []
    for kind, text in items:
        if kind == "lit":
            print(f"let private t{n} = Utf8.lit {fsharp_string(text)}")
            steps.append(f"    w.Lit t{n}")
            n += 1
        else:
            one_line = " ".join(text.split())
            steps.append(f"    // {one_line}")
    print()
    print("let render (w: Out) =")
    print(indented(items))


def indented(items):
    """The render steps with `if`/`elif`/`else`/`for`/`filter` blocks indented as F# would be."""
    depth = 1
    n = 0
    steps = []

    def add(text):
        steps.append("    " * depth + text)

    for kind, text in items:
        if kind == "lit":
            add(f"w.Lit t{n}")
            n += 1
            continue
        inner = " ".join(text.strip("{}%#-+~ ").split()) if text.startswith("{%") else None
        if inner is None:
            one_line = " ".join(text.split())
            translated = fs_expression(text[2:-2].strip("-+~ ")) if text.startswith("{{") else None
            if translated:
                add(translated)
            else:
                add("// " + one_line)
            continue
        word = inner.split(" ", 1)[0]
        rest = inner[len(word):].strip()
        if word == "if":
            add(f"if {fs_condition(rest)} then")
            depth += 1
        elif word == "elif":
            depth -= 1
            add(f"elif {fs_condition(rest)} then")
            depth += 1
        elif word == "else":
            depth -= 1
            add("else")
            depth += 1
        elif word in ("endif", "endfor"):
            depth -= 1
        elif word == "for":
            add(f"for {rest.replace(' in ', ' in ', 1)} do")
            depth += 1
        elif word == "filter":
            add(f"{rest} (fun w ->")
            depth += 1
        elif word == "endfilter":
            depth -= 1
            add(")")
        else:
            add("// " + inner)
    return "\n".join(steps)


if __name__ == "__main__":
    main()
