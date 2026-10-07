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


def fs_value(expr):
    """A Rust value expression (a route, a path, a string with `~`) as F#, best effort."""
    expr = expr.strip()
    strings = []
    def stash(m):
        strings.append(m.group(0))
        return f"\u0001{len(strings) - 1}\u0002"
    expr = re.sub(r'"(?:[^"\\]|\\.)*"', stash, expr)
    expr = re.sub(r"\.as_(str|ref|deref)\(\)", "", expr)
    expr = re.sub(r"h::routes::([a-z_0-9]+)\(([^()]*)\)",
                  lambda m: "Routes." + camel(m.group(1)) + "(" + m.group(2).replace(", ", ") (") + ")" if m.group(2) else "Routes." + camel(m.group(1)) + " ()", expr)
    expr = expr.replace("ctx.can_administer()", "ctx.CanAdminister")
    expr = re.sub(r"ctx\.is_current_user\(([^()]*)\)", r"ctx.IsCurrentUser(\1)", expr)
    expr = re.sub(r"ctx\.url\((.*)\)", r"ctx.Url(\1)", expr)
    expr = expr.replace(" ~ ", " + ")
    expr = re.sub(r"\b([a-z_][a-z_0-9]*)\.([a-z_][a-z_0-9]*)\(\)", lambda m: m.group(1) + "." + pascal(m.group(2)), expr)
    expr = re.sub(r"(?<!Routes)\.([a-z_][a-z_0-9]*)", lambda m: "." + pascal(m.group(1)), expr)
    return re.sub("\u0001(\\d+)\u0002", lambda m: strings[int(m.group(1))], expr)


def camel(name):
    p = pascal(name)
    return p[:1].lower() + p[1:]


def fs_expression(expr):
    """The statement an interpolation `{{ expr }}` is, when it is one of the common forms."""
    expr = expr.strip().rstrip("-").strip()
    simple = {
        "h::link_back(ctx)": "Application.linkBack w ctx",
        "h::link_back_to_last_room_visited(ctx)": "Application.linkBackToLastRoomVisited w ctx",
        "h::version_badge(ctx)": "Application.versionBadge w ctx",
        "h::profile_form_submit_button(ctx)": "UsersHelper.profileFormSubmitButton w ctx",
        "h::turbo_page_requires_reload_tag()": "Turbo.turboPageRequiresReloadTag w",
        "h::user_filter_search_tag()": "UsersHelper.userFilterSearchTag w",
    }
    if expr in simple:
        return simple[expr]
    m = re.fullmatch(r"h::translation_button\(ctx, (.*)\)", expr)
    if m:
        return f"Translations.translationButton w ctx {m.group(1)}"
    m = re.fullmatch(r"h::link_back_to\(ctx, (.*)\)", expr)
    if m:
        return f"Application.linkBackTo w ctx ({fs_value(m.group(1))})"
    m = re.fullmatch(r"h::account_logo_tag\(ctx, (.*)\)", expr)
    if m:
        return f"UsersHelper.accountLogoTag w ctx ({m.group(1)})"
    m = re.fullmatch(r"h::avatar_tag\(ctx, (.*?), (h::attrs\(\).*)\)", expr)
    if m:
        return f"UsersHelper.avatarTag w ctx ({fs_value(m.group(1))}) ({fs_attrs(m.group(2))})"
    m = re.fullmatch(r"h::button_to_direct_room_with\(ctx, (.*)\)", expr)
    if m:
        return f"UsersHelper.buttonToDirectRoomWith w ctx ({fs_value(m.group(1))})"
    m = re.fullmatch(r"h::button_to_change_involvement\(ctx, (.*)\)", expr)
    if m:
        return f"// RoomsHelper.buttonToChangeInvolvement w ctx ({fs_value(m.group(1))})"
    m = re.fullmatch(r"form\.(file_field|text_field|email_field|url_field|password_field|text_area|hidden_field)\((.*)\)", expr, re.S)
    if m:
        args = split_args(m.group(2))
        kind = m.group(1)
        name = args[0]
        rest = args[1:]
        fn = {"file_field": "FileField", "text_field": "TextField", "email_field": "EmailField", "url_field": "UrlField",
              "password_field": "PasswordField", "text_area": "TextArea", "hidden_field": "HiddenField"}[kind]
        out = [name]
        if kind in ("text_field", "email_field", "url_field", "text_area", "hidden_field"):
            out.append(rest[0].replace("Some(", "Some(").replace("None", "None"))
            rest = rest[1:]
        out.append("(" + fs_attrs(rest[0]) + ")")
        return f"form.{fn}(w, {', '.join(out)})"
    m = re.fullmatch(r"h::image_tag\(ctx, (.*)\)", expr, re.S)
    if m:
        args = split_args(m.group(1))
        if len(args) == 2:
            return f"Assets.imageTag w ctx ({fs_value(args[0])}) ({fs_attrs(args[1])})"
    m = re.fullmatch(r"h::capitalize\((.*)\)", expr)
    if m and fs_path(m.group(1)):
        return f"w.Text(Application.capitalize {fs_path(m.group(1))})"
    m = re.fullmatch(r"ctx\.url\((.*)\)", expr)
    if m:
        return f"w.Text(ctx.Url({m.group(1)}))"
    if fs_path(expr):
        return f"w.Text {fs_path(expr)}"
    if expr.startswith(("user.", "bot.", "room.", "membership.", "push_subscription.")) or re.fullmatch(r"[a-z_]+", expr):
        return f"w.Text({fs_value(expr)})"
    return None


FILTERS = {
    "link_to": lambda a: f"Filters.linkTo w ({fs_value(a[0])}) ({fs_attrs(a[1])}) (fun w ->",
    "button_to": lambda a: f"Filters.buttonTo w ({fs_value(a[0])}) ({fs_attrs(a[1])}) (fun w ->",
    "button": lambda a: f"Filters.button w ({fs_attrs(a[0])}) (fun w ->",
    "form_with": lambda a: f"Filters.formWith w {a[0]} (fun w ->",
    "turbo_frame_tag": lambda a: f"Filters.turboFrameTag w {a[0]} ({fs_attrs(a[1])}) (fun w ->",
    "link_to_zoom_qr_code": lambda a: f"Filters.linkToZoomQrCode w ({fs_value(a[0])}) (fun w ->",
    "button_to_copy_to_clipboard": lambda a: f"Filters.buttonToCopyToClipboard w ({fs_value(a[0])}) (fun w ->",
    "web_share_session_button": lambda a: f"Filters.webShareSessionButton w ({fs_value(a[0])}) ({a[1]}) ({a[2]}) (fun w ->",
}


def fs_filter(rest):
    m = re.fullmatch(r"([a-z_]+)\((.*)\)", rest, re.S)
    if m and m.group(1) in FILTERS:
        try:
            return FILTERS[m.group(1)](split_args(m.group(2)))
        except IndexError:
            pass
    return f"{rest} (fun w ->"


def fs_let(rest):
    m = re.fullmatch(r"([a-z_]+) = (.*)", rest, re.S)
    if not m:
        return "// let " + rest
    name, value = m.groups()
    if value.startswith("h::form_with("):
        inner = value[len("h::form_with("):]
        depth, i = 1, 0
        while depth:
            depth += {"(": 1, ")": -1}.get(inner[i], 0)
            i += 1
        url, chain = inner[:i - 1], inner[i:]
        chain = re.sub(r"\.([a-z_0-9]+)\(", lambda mm: "." + pascal(mm.group(1)) + "(", chain)
        return f"let {name} = (Forms.formWith ({fs_value(url)})){chain}"
    return f"let {name} = {fs_value(value)}"


def main():
    path = sys.argv[1]
    source = (ROOT / path).read_text()
    # Askama drops one trailing newline from a template file (the tail of an `include` too).
    if source.endswith("\n"):
        source = source[:-1]
    items = parts(source)
    extends = any(k == "tag" and re.match(r"\{%-?\s*extends", t) for k, t in items)
    # In an extending template only what is inside a block is rendered.
    kept, in_block = [], not extends
    for kind, text in items:
        if kind == "tag" and re.match(r"\{%[-+~]?\s*block\b", text):
            in_block = True
        elif kind == "tag" and re.match(r"\{%[-+~]?\s*endblock\b", text):
            kept.append((kind, text))
            in_block = not extends
            continue
        if in_block or (kind == "tag" and re.match(r"\{%[-+~]?\s*(extends)\b", text)):
            kept.append((kind, text))
    items = kept
    print(f"// transcribed from rust/crates/views/templates/{path} (reference-tools/views/transcribe.py)")
    n = 0
    for kind, text in items:
        if kind == "lit":
            print(f"let private t{n} = Utf8.lit {fsharp_string(text)}")
            n += 1
    print()
    if not extends:
        print("let render (w: Out) (ctx: ViewContext) : unit =")
    print(indented(items, extends))


def indented(items, extends=False):
    """The render steps with `if`/`elif`/`else`/`for`/`filter` blocks indented as F# would be."""
    depth = 0 if extends else 1
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
        if word == "block":
            add("")
            add(f"let {rest} (w: Out) (ctx: ViewContext) : unit =")
            depth = 1
        elif word == "endblock":
            depth = 0
        elif word == "if":
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
            add(f"for {rest} do")
            depth += 1
        elif word == "filter":
            add(fs_filter(rest))
            depth += 1
        elif word == "endfilter":
            depth -= 1
            add(")")
        elif word == "let":
            add(fs_let(rest))
        elif word == "include":
            add("// include " + rest)
        else:
            add("// " + inner)
    return "\n".join(steps)


if __name__ == "__main__":
    main()
