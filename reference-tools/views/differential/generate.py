#!/usr/bin/env python3
"""Writes the cases of bin/views-differential: JSON lines, the first being the `shared` values. Their
data comes from the parity seeds (parity/.seed/*: the accounts, users, rooms and messages the parity
harness runs the reference app against) and from the views crate's golden facts (the importmap and
stylesheet tags the reference rendered, and the platform flags its user agents give); the rest are
generated variations (hostile strings, every combination of platform flags, attribute kinds, sizes).

  generate.py [count] [seed] > cases.jsonl

`count` scales how many variations of each kind are made (default 1: ~6,000 cases).
"""
import json
import random
import sqlite3
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
FACTS = ROOT / "rust/crates/views/tests/golden/a/facts.json"
SEEDS = ROOT / "parity/.seed"
SCALE = int(sys.argv[1]) if len(sys.argv) > 1 else 1
RNG = random.Random(int(sys.argv[2]) if len(sys.argv) > 2 else 1)

HOSTILE = [
    "", " ", "plain", "Plain Name", "a&b", "<script>alert(1)</script>", '"quoted"', "it's", "&amp; already", "a -> b", "a<b>c",
    "é ü ñ", "日本語 テキスト", "😀 emoji 👍🏽", "back\\slash", "line\nbreak", "tab\there", "x" * 300, "&lt;", "Ünal-Smith", "o'Brien",
    "ß straße", "İstanbul", "ǆ ǈ", "ΐ", "ŉ", "ﬃ", "https://example.com/?a=1&b=2", "mailto:a@b.c", "100%", "a+b c", "~tilde", "ａｂｃ",
    # what `trim` and `compact_blank` call blank, and what they don't
    "\u001f", "\u0085", "\u00a0", "\u2003 ", "\u180e", "\u200b", "\u3000", "\u2028", "\u001c x", "\ufeff", "\u000b", "\u0000",
    "a\u0301 b", "𝒜𝒷", "👍🏽 x", "\ud7ff", "\ue000",
]
KINDS = ["text", "safe", "bool", "int", "none"]
ATTR_NAMES = [
    "class", "id", "title", "alt", "role", "href", "src", "value", "name", "type", "style", "tabindex", "data-x", "data-turbo-frame",
    "aria-label", "hidden", "disabled", "required", "checked", "selected", "autofocus", "readonly", "multiple", "open", "async",
    "data-action", "data-controller", "content", "rel", "target", "size", "width", "height", "placeholder", "maxlength",
]


def hostile():
    return RNG.choice(HOSTILE)


def rfc3339(row_time):
    return row_time.replace(" ", "T") + ("Z" if "." not in row_time else "Z")


def seed_data():
    data = {"users": [], "accounts": [], "rooms": [], "messages": []}
    for seed in sorted(SEEDS.iterdir()):
        path = seed / "db/production.sqlite3"
        if not path.exists():
            continue
        db = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
        for id, name, bio, email, role, status, created in db.execute(
            "select id, name, bio, email_address, role, status, created_at from users"
        ):
            data["users"].append(
                {
                    "id": id,
                    "name": name,
                    "bio": bio,
                    "email_address": email,
                    "role": ["member", "administrator", "bot"][role],
                    "status": ["active", "deactivated", "banned"][status],
                    "avatar_path": f"/users/{id}/avatar?v={created.replace('-', '').replace(':', '').replace(' ', '')}",
                    "attachable_sgid": f"sgid-{id}-{seed.name}",
                }
            )
        for name, join_code, css in db.execute("select name, join_code, custom_styles from accounts"):
            data["accounts"].append({"name": name, "join_code": join_code, "custom_styles": css})
        for id, name, kind in db.execute("select id, name, type from rooms"):
            data["rooms"].append({"id": id, "name": name, "kind": kind.split("::")[1].lower()})
        for id, client_id, created, updated, room_id in db.execute(
            "select id, client_message_id, created_at, updated_at, room_id from messages"
        ):
            data["messages"].append(
                {"id": id, "client_message_id": client_id, "created_at": rfc3339(created), "updated_at": rfc3339(updated), "room_id": room_id}
            )
        db.close()
    return data


SEED = seed_data()
FACTS_JSON = json.loads(FACTS.read_text())
PLATFORMS = FACTS_JSON["platforms"]
USERS = SEED["users"] or [{"id": 1, "name": "Nobody", "bio": None, "email_address": None, "role": "member", "status": "active", "avatar_path": "/u", "attachable_sgid": "s"}]
ACCOUNTS = SEED["accounts"] or [{"name": "Campfire", "join_code": "AAAA-BBBB-CCCC", "custom_styles": None}]

counter = [0]


def case(op, args=None, ctx=None):
    counter[0] += 1
    out = {"id": f"{counter[0]:05d}-{op}", "op": op}
    if ctx is not None:
        out["ctx"] = ctx
    if args is not None:
        out["args"] = args
    print(json.dumps(out, ensure_ascii=False))


def user_ctx(user):
    return {
        "id": user["id"],
        "name": user["name"],
        "administrator": user["role"] == "administrator",
        "bot": user["role"] == "bot",
        "avatar_url": user["avatar_path"],
    }


def random_platform():
    if RNG.random() < 0.5:
        return dict(PLATFORMS[RNG.choice(sorted(PLATFORMS))])
    flags = ["ios", "android", "mac", "windows", "chrome", "firefox", "safari", "edge", "mobile", "desktop", "apple_messages"]
    platform = {flag: RNG.random() < 0.4 for flag in flags}
    platform["browser"] = RNG.choice(["Chrome", "Safari", "Firefox", "Edge", "Opera", "", "chrome", "ÉDGE", hostile()])
    platform["operating_system"] = RNG.choice(["macOS", "Windows", "iPhone", "Android", "", hostile()])
    return platform


def random_ctx(**overrides):
    user = RNG.choice(USERS) if RNG.random() < 0.8 else None
    account = RNG.choice(ACCOUNTS)
    base = RNG.choice(["http://campfire.test", "https://chat.example.com:8443", "http://localhost:3000", "https://x.y/z"])
    path = RNG.choice(["/", "/rooms/1", "/account/edit", "/users/me/profile?x=1&y=2"])
    ctx = {
        "current_user": user_ctx(user) if user else None,
        "account": {
            "name": RNG.choice([account["name"], hostile()]),
            "logo_url": RNG.choice(["/account/logo", "/account/logo?v=20260926130029", "/account/logo?size=small&v=1"]),
            "has_logo": RNG.random() < 0.4,
        },
        "flash_notice": RNG.choice([None, None, "Saved.", hostile()]),
        "flash_alert": RNG.choice([None, None, None, "Not allowed.", hostile()]),
        "platform": random_platform(),
        "vapid_public_key": RNG.choice([None, FACTS_JSON["vapid_public_key"], hostile()]),
        "custom_styles": RNG.choice([None, None, account["custom_styles"], hostile(), "a { color: red }\n</style>"]),
        "cable_url": RNG.choice(["/cable", "/campfire/cable"]),
        "base_url": base,
        "request_url": base + path,
        "referrer": RNG.choice([None, base + path, base + "/rooms/2", "https://other.example/x?a=1&b=2"]),
        "last_room_visited_id": RNG.choice([None, None, 1, 654632876]),
        "app_version": RNG.choice(["0", "abc1234", "parity", "1.0 <beta>"]),
    }
    ctx.update(overrides)
    return ctx


def safe_html():
    return RNG.choice(["", "<p>hi</p>", "<b>a</b> &amp; <i>b</i>", "text only", "<div class=\"x\">é 日本</div>\n", hostile()])


def attrs_list():
    attrs = []
    for _ in range(RNG.randrange(0, 7)):
        name = RNG.choice(ATTR_NAMES)
        kind = RNG.choice(KINDS)
        value = {"text": hostile, "safe": lambda: RNG.choice(["a->b", 'say "hi"', "&amp;", "<b>"]), "bool": lambda: RNG.random() < 0.5, "int": lambda: RNG.randrange(-5, 400), "none": lambda: None}[kind]()
        attrs.append([name, kind, value])
    return attrs


timestamps_for_cache = ["2024-06-01T12:00:00.000123Z", "2026-09-26T12:23:46.483521Z", "1970-01-01T00:00:00Z", "2038-01-19T03:14:08.5Z"]


def n(base):
    return max(1, base * SCALE)


# the header: values every case's ctx falls back to
print(json.dumps({"shared": {"importmap_tags": FACTS_JSON["importmap_tags"], "stylesheet_tags": FACTS_JSON["stylesheet_tags"]}}))

# ---- layouts ---------------------------------------------------------------------------------------------
for _ in range(n(80)):
    case(
        "layouts/application_wrapper",
        {
            "page_title": RNG.choice([None, "Room", hostile()]),
            "body_class": RNG.choice([None, "sidebar", "sidebar searches", hostile()]),
            "head": safe_html(), "nav": safe_html(), "content": safe_html(), "footer": safe_html(), "sidebar": safe_html(),
        },
        random_ctx(),
    )
for _ in range(n(15)):
    case("layouts/_lightbox", None, random_ctx())
for _ in range(n(20)):
    case("layouts/turbo_rails/frame", {"head": safe_html(), "content": safe_html()}, random_ctx())
sizes = [0, 5, 100, 1023, 1024, 1025, 3000]
for _ in range(n(40)):
    case(
        "recorded/page",
        {"sizes": [RNG.choice(sizes) for _ in range(RNG.randrange(0, 9))], "unhanded": RNG.choice(sizes), "frame": RNG.random() < 0.5},
        random_ctx(),
    )

# ---- shared partials ---------------------------------------------------------------------------------------
for _ in range(n(40)):
    admin = RNG.choice(USERS)
    contact = None if RNG.random() < 0.2 else {"name": RNG.choice([admin["name"], hostile()]), "email_address": RNG.choice([admin["email_address"] or "x@y.z", hostile()])}
    case("accounts/_help_contact", {"help_contact": contact}, random_ctx())
for account in ACCOUNTS:
    case("accounts/_invite", {"join_code": account["join_code"]}, random_ctx())
for _ in range(n(30)):
    case("accounts/_invite", {"join_code": RNG.choice([hostile(), "AB-CD", "a b"])}, random_ctx())
for op in ["pwa/_install_instructions", "pwa/_browser_settings", "pwa/_system_settings"]:
    for label in sorted(PLATFORMS):
        case(op, None, random_ctx(platform=PLATFORMS[label]))
    for _ in range(n(120)):
        case(op, None, random_ctx())
for user in USERS:
    case("users/_mention", {"user": user, "attachable_sgid": user["attachable_sgid"]}, random_ctx())
for _ in range(n(30)):
    user = dict(RNG.choice(USERS), name=hostile(), bio=RNG.choice([None, hostile()]), avatar_path=RNG.choice(["/a", "/a?v=1&w=2", hostile()]))
    case("users/_mention", {"user": user, "attachable_sgid": hostile()}, random_ctx())
for _ in range(n(10)):
    case("users/autocompletables/_template", None, random_ctx())
for _ in range(n(40)):
    case("welcome/show", {"current_user_name": RNG.choice([RNG.choice(USERS)["name"], hostile()])}, random_ctx())

def sidebar_direct(updated_at):
    members = [RNG.choice(USERS) for _ in range(RNG.randrange(1, 7))]
    if RNG.random() < 0.3:
        members = [dict(RNG.choice(USERS), name=hostile())]
    return {
        "room_id": RNG.randrange(1, 10**9), "unread": RNG.random() < 0.5, "updated_at_epoch": str(RNG.randrange(10**12, 2 * 10**12)),
        "members": members, "membership_id": RNG.randrange(1, 10**6), "membership_updated_at": updated_at,
    }


for _ in range(n(40)):
    case("users/sidebars/rooms/_direct", {"membership": sidebar_direct(RNG.choice(timestamps_for_cache))}, random_ctx())
    case("users/sidebars/rooms/_shared", {"room": {"id": RNG.randrange(1, 10**9), "param_key": RNG.choice(["rooms_open", "rooms_closed"]), "name": RNG.choice([RNG.choice(SEED["rooms"])["name"] or "HQ", hostile()]), "unread": RNG.random() < 0.5}})
    case("users/sidebars/rooms/_direct_placeholder", {"user": RNG.choice(USERS)}, random_ctx())
    first = sidebar_direct(RNG.choice(timestamps_for_cache))
    second = dict(first, members=[RNG.choice(USERS) for _ in range(RNG.randrange(1, 4))], unread=not first["unread"])
    case("users/direct_room", {"membership": first, "second": second}, random_ctx())

# ---- helpers: tags ---------------------------------------------------------------------------------------------
for _ in range(n(200)):
    case(
        "helpers/tag",
        {
            "kind": RNG.choice(["content_tag", "content_tag_text", "content_tag_block", "builder_tag", "legacy_tag"]),
            "name": RNG.choice(["div", "span", "a", "textarea", "turbo_frame", "turbo-frame", "meta", "img", "input", "br", "p", "button"]),
            "attrs": attrs_list(),
            "content": RNG.choice([hostile(), "<b>x</b>"]),
        },
    )
for _ in range(n(80)):
    case(
        "helpers/image_tag",
        {
            "source": RNG.choice(["arrow-left.svg", "external/install.svg", "/already/absolute.png", "https://x.test/a.png", "data:image/png;base64,AAA", "cid:abc", "a-b://x", "1x://y", "://z", "campfire-icon.png", hostile()]),
            "attrs": attrs_list() + RNG.choice([[], [["size", "int", 20]], [["size", "text", "20x30"]], [["size", "text", "x5"]], [["size", "bool", True]]]),
        },
        random_ctx(),
    )
for source in ["x.svg", "/x", "http://a", "https://a/b?c", "data:x", "cid:y", "ftp://z", "a_b://c", "", ":///", "x://"]:
    case("helpers/asset_path", {"source": source}, random_ctx())
for _ in range(n(80)):
    case(
        "helpers/link",
        {
            "kind": RNG.choice(["link_to", "link_to_text", "link_to_if", "mail_to"]),
            "url": RNG.choice(["/rooms/1", "https://x.test/?a=1&b=2", hostile()]),
            "text": RNG.choice([hostile(), "Name <b>"]),
            "condition": RNG.random() < 0.5,
            "attrs": attrs_list(),
            "content": RNG.choice([hostile(), "<i>x</i>"]),
        },
    )
for _ in range(n(8)):
    case("helpers/link", {"kind": "mail_to", "url": "", "text": RNG.choice(["a@b.c", "a+b@c.d", "é@x.y", "a b@c.d", "x@y.z?subject=hi&body=1"]), "condition": True, "attrs": [], "content": ""})


def form_args():
    fields = []
    for _ in range(RNG.randrange(0, 6)):
        kind = RNG.choice(["text", "email", "url", "password", "hidden", "file", "text_area", "check_box"])
        field = {
            "type": kind,
            "method": RNG.choice(["name", "email_address", "password", "webhook_url", "bio", "join code", "x]y", "custom_styles"]),
            "value": RNG.choice([None, hostile()]),
            "attrs": [a for a in attrs_list() if a[0] not in ("size",)] if RNG.random() < 0.6 else [],
            "fields_for": RNG.choice([None, None, None, "settings", "a][b"]),
        }
        if kind == "check_box":
            field.update({"checked_value": "1", "unchecked_value": "0", "current": RNG.choice(["1", "0", ""])})
        fields.append(field)
    return {
        "url": RNG.choice(["/session", "/account.1", "/rooms/opens", hostile()]),
        "model": RNG.choice([None, "user", "account", "rooms_open", "a b"]),
        "method": RNG.choice([None, "patch", "post", "get", "put", "delete", "PATCH", "Delete"]),
        "id": RNG.choice([None, None, "form_1"]),
        "class": RNG.choice([None, None, "center", "a b"]),
        "data": RNG.choice([[], [["controller", "form"]], [["action", "x->y#z"], ["turbo_frame", "_top"]]]),
        "auto_submit": RNG.random() < 0.2,
        "multipart": RNG.random() < 0.15,
        "open": RNG.random() < 0.2,
        "fields": fields,
    }


for _ in range(n(120)):
    case("helpers/form", form_args())
for _ in range(n(80)):
    case(
        "helpers/button",
        {
            "kind": RNG.choice(["button_to", "button_to_block", "button_tag", "hidden_field_tag", "method_tag"]),
            "url": RNG.choice(["/rooms/1", "/x?a=1&b=2"]),
            "name": RNG.choice(["user[name]", "a]b", "x y", "_method", "delete", hostile()]),
            "value": RNG.choice([None, hostile()]),
            "attrs": attrs_list() + RNG.choice([[], [["method", "text", RNG.choice(["delete", "put", "patch", "post", "get", "weird"])]], [["form_class", "text", "my-form"]]]),
            "content": RNG.choice([hostile(), "<span>x</span>"]),
        },
    )

# ---- helpers: application, users, rooms ------------------------------------------------------------------------
def app(name, **args):
    case("helpers/application", dict(name=name, content=RNG.choice([hostile(), "<b>c</b>"]), **args), random_ctx())


for _ in range(n(12)):
    app("page_title_tag", page_title=RNG.choice([None, hostile()]))
    app("current_user_meta_tags")
    app("script_aware_action_cable_meta_tag")
    app("custom_styles_tag")
    app("body_classes", body_class=RNG.choice([None, "", "sidebar", hostile()]))
    app("link_back")
    app("link_back_to", destination=RNG.choice(["/rooms/1", hostile()]))
    app("link_back_to_last_room_visited")
    app("version_badge")
    app("button_to_copy_to_clipboard", url=RNG.choice(["http://x/y?z=1", hostile()]))
    app("link_to_zoom_qr_code", url=RNG.choice(["http://x/?a", "http://x/?>?", "", hostile()]))
    app("web_share_session_button", url=hostile(), title=hostile(), text=hostile())
    app("truncate", text=RNG.choice([hostile(), "abcdef", "😀😀😀😀😀"]), length=RNG.randrange(0, 12), omission=RNG.choice(["…", "...", "", "😀"]))
    app("capitalize", text=hostile())
    app("to_sentence", items=[hostile() for _ in range(RNG.randrange(0, 5))], connector=RNG.choice([" and ", "+", ", "]))
# every code point: capitalized (first character up, rest down) and as a word character before an "x"
chars = [chr(c) for c in range(0x110000) if not 0xD800 <= c <= 0xDFFF]
for start in range(0, len(chars), 4000):
    chunk = "".join(chars[start : start + 4000])
    case("helpers/application", {"name": "capitalize_each", "text": chunk})
    case("helpers/users", {"name": "initials_each", "text": chunk})
    if start >= 0x30000 // SCALE and SCALE == 1 and start > 0x40000:
        break
case("helpers/application", {"name": "capitalize", "text": "".join(chars[:4000])})

for user in USERS:
    avatar = {"id": user["id"], "title": user["name"] + (" – " + user["bio"] if user["bio"] else ""), "avatar_path": user["avatar_path"]}
    case("helpers/users", dict(name="avatar_tag", attrs=attrs_list(), **avatar), random_ctx())
    case("helpers/users", {"name": "avatar_background_color", "id": user["id"]})
    case("helpers/users", {"name": "initials", "text": user["name"]})
    case("helpers/users", {"name": "user_title", "text": user["name"], "bio": user["bio"]})
    case("helpers/users", {"name": "button_to_direct_room_with", "id": user["id"]}, random_ctx())
for i in range(0, 60):
    case("helpers/users", {"name": "avatar_background_color", "id": RNG.choice([i, i * 7919, RNG.randrange(1, 10**12)])})
for _ in range(n(40)):
    text = hostile()
    case("helpers/users", {"name": "initials", "text": RNG.choice([text, "David Heinemeier Hansson", "Élodie Ünal-Smith o'Brien 3po _x ñ", "a_b c-d e.f"])})
    case("helpers/users", {"name": "user_title", "text": text, "bio": RNG.choice([None, "", " ", hostile()])})
    case("helpers/users", {"name": "curl_text_line", "text": hostile()})
    case("helpers/users", {"name": "curl_upload_line", "text": hostile()})
    case("helpers/users", {"name": "account_logo_tag", "style": RNG.choice([None, "small", hostile()])}, random_ctx())
    case("helpers/users", {"name": "profile_form_submit_button"}, random_ctx())
    case("helpers/users", {"name": "sidebar_turbo_frame_tag", "src": RNG.choice([None, "/users/me/sidebar", hostile()]), "content": RNG.choice(["", "<b>x</b>"])})
    case("helpers/users", {"name": "user_filter_menu_tag", "content": RNG.choice(["", "<li>x</li>"])})
    case("helpers/users", {"name": "user_filter_search_tag"})
for _ in range(n(30)):
    case("helpers/rooms", {"name": "link_to_room", "room_id": RNG.randrange(1, 10**9), "attrs": attrs_list() + RNG.choice([[], [["data-x", "text", "1"]], [["data-rooms-list-target", "text", "mine"], ["class", "text", "c"]]]), "content": RNG.choice(["", "<b>x</b>", hostile()])})
    for involvement in ["mentions", "everything", "nothing", "invisible", "other"]:
        direct = RNG.random() < 0.5
        case("helpers/rooms", {"name": "humanize_involvement", "involvement": involvement})
        case("helpers/rooms", {"name": "next_involvement", "direct": direct, "involvement": involvement})
        case("helpers/rooms", {"name": "button_to_change_involvement", "room_id": RNG.randrange(1, 10**6), "param_key": RNG.choice(["rooms_open", "rooms_closed", "rooms_direct"]), "direct": direct, "involvement": involvement}, random_ctx())
for key in ["email_address", "password", "update_password", "user_name", "account_name", "room_name", "invite_message", "incompatible_browser_messsage", "bio", "webhook_url", "chat_bots", "bot_name", "custom_styles"]:
    case("helpers/translations", {"kind": "translations_for", "key": key})
    case("helpers/translations", {"kind": "translation_button", "key": key}, random_ctx())
for _ in range(n(40)):
    params = []
    for _ in range(RNG.randrange(0, 5)):
        value = [hostile() for _ in range(RNG.randrange(0, 4))] if RNG.random() < 0.3 else hostile()
        params.append([RNG.choice(["z", "a", "q", "user_ids", "b c", "é", "a[b]", hostile()]), value])
    case("helpers/url", {"kind": "with_query", "path": RNG.choice(["/x", "/rooms/directs", hostile()]), "params": params})
    case("helpers/url", {"kind": "directs", "user_ids": [RNG.randrange(1, 10**6) for _ in range(RNG.randrange(0, 4))]})
    case("helpers/url", {"kind": "cgi_escape", "text": hostile()})
    case("helpers/turbo", {"kind": "turbo_stream_from", "name": RNG.choice(["abc--def", hostile()])})
    case("helpers/turbo", {"kind": "dom_id", "model": RNG.choice(["message", "rooms_open"]), "id": RNG.randrange(0, 10**6), "prefix": RNG.choice([None, "boost", ""])})
    case("helpers/turbo", {"kind": "turbo_frame_tag", "id": RNG.choice(["f", hostile()]), "attrs": attrs_list() + RNG.choice([[], [["src", "text", "/x"]], [["target", "text", "_top"], ["src", "text", "/y"]]]), "content": RNG.choice(["", "<b>x</b>"])})
case("helpers/turbo", {"kind": "page_requires_reload"})

# ---- fragment cache -----------------------------------------------------------------------------------------------
timestamps = [
    "1970-01-01T00:00:00Z", "1970-01-01T00:00:00.000001Z", "1999-12-31T23:59:59.999999Z", "2000-02-29T12:34:56.5Z",
    "2024-02-29T23:59:59.000999Z", "2024-06-01T12:00:00.000123Z", "2024-12-31T23:59:59.1Z", "2038-01-19T03:14:08Z",
    "2100-03-01T00:00:00.00001Z", "9999-12-30T21:59:59.999999Z", "1969-12-31T23:59:59.5Z", "1900-01-01T00:00:00Z",
    "2024-11-03T01:30:00.123456-07:00", "2026-09-26T12:23:46.483521Z", "2026-09-26T11:23:46Z",
]
for message in SEED["messages"][:60]:
    timestamps.append(message["created_at"])
for _ in range(n(40)):
    timestamps.append(f"{RNG.randrange(1970, 2100)}-{RNG.randrange(1, 13):02d}-{RNG.randrange(1, 29):02d}T{RNG.randrange(24):02d}:{RNG.randrange(60):02d}:{RNG.randrange(60):02d}.{RNG.randrange(10**6):06d}Z")
for at in timestamps:
    for table, template in [("messages", "messages/_message"), ("boosts", "messages/boosts/_boost"), ("memberships", "users/sidebars/rooms/_direct")]:
        case("fragment_cache/keys", {"at": at, "id": RNG.choice([0, 1, 7, 10, 99, 100, 12345, 10**9, 2**62, -1]), "table": table, "template": template, "digest": "0123456789abcdef"})
    # the timestamps the message helpers format
    case("messages/epoch_ms", {"at": at})
    case("messages/iso8601", {"at": at})
    case("messages/json_time", {"at": at})
for _ in range(n(30)):
    keys = [f"k{i}" for i in range(RNG.randrange(2, 12))]
    steps = [[RNG.choice(["fetch", "fetch", "get"]), RNG.choice(keys), RNG.choice([0, 10, 100, 500, 1000, 4000])] for _ in range(RNG.randrange(5, 80))]
    case("fragment_cache/script", {"max_bytes": RNG.choice([2000, 5000, 10000, 100000]), "steps": steps})

# ---- messages ------------------------------------------------------------------------------------------------------------
def bits(number):
    return struct.pack(">d", number).hex()


for number in [0, 1, 641, -3, -4, 1200, 1e15, 1e-5, 600.0, 16 / 9, 0.1, 2.5, 1e22, 123456789.123456789, 3, -1.5, 1790425426483, 5e-324, 1.7976931348623157e308, 100.0, 0.0001, 0.00001, 123456789012345.6, 1e16]:
    for half in (False, True):
        # integers as JSON numbers, floats as their exact bits (serde_json's float parsing is not exact)
        args = {"int": number} if isinstance(number, int) else {"float_bits": bits(number)}
        case("messages/ruby_number", dict(args, half=half))


def user_view():
    user = RNG.choice(USERS)
    return {"id": user["id"], "name": user["name"], "title": user["name"], "avatar_url": user["avatar_path"]}


def message_view(content):
    message = RNG.choice(SEED["messages"]) if SEED["messages"] else {"id": 1, "client_message_id": "0001", "created_at": "2026-01-01T00:00:00Z", "updated_at": "2026-01-01T00:00:00Z", "room_id": 1}
    return {
        "id": message["id"], "client_message_id": message["client_message_id"], "room_id": message["room_id"], "room_name": hostile(),
        "creator": user_view(), "created_at": message["created_at"], "updated_at": message["updated_at"], "all_emoji": RNG.random() < 0.2,
        "content": content, "boosts": [],
    }


def random_dimension():
    return RNG.choice([None, 100, 641, 1200, 1201, 2000, 3, 640.0, 1920.5, 4000.25, 799, 801])


for _ in range(n(120)):
    kind = RNG.choice(["text", "sound", "attachment", "unrenderable"])
    if kind == "text":
        content = {"type": "text", "html": RNG.choice(["<p>hi</p>", hostile(), "<div>a &amp; b</div>"])}
    elif kind == "sound":
        content = {"type": "sound", "url": RNG.choice(["/assets/a.mp3", hostile()]), "image": RNG.choice([None, {"src": RNG.choice(["/i.png", hostile()]), "width": RNG.randrange(0, 400), "height": RNG.randrange(0, 400)}]), "text": RNG.choice([None, hostile()])}
    elif kind == "attachment":
        content = {
            "type": "attachment", "filename": RNG.choice(["a.png", hostile()]), "blob_path": RNG.choice(["/rails/blobs/a?x=1&y=2", hostile()]),
            "download_path": RNG.choice(["/rails/blobs/a?disposition=attachment", hostile()]),
            "preview": RNG.choice([{"type": "video", "poster_url": hostile()}, {"type": "image", "thumb_url": hostile()}, {"type": "file"}]),
            "width": random_dimension(), "height": random_dimension(),
        }
    else:
        content = {"type": "unrenderable"}
    case("messages/presentation", {"message": message_view(content)}, random_ctx())


def user_json():
    user = RNG.choice(USERS)
    return {"id": user["id"], "name": RNG.choice([user["name"], hostile()]), "role": user["role"], "avatar_url": "http://campfire.test" + user["avatar_path"]}


def message_json():
    message = RNG.choice(SEED["messages"]) if SEED["messages"] else {"id": 1, "room_id": 1, "created_at": "x"}
    return {"id": message["id"], "created_at": "2026-09-26T12:26:46.848Z", "body": {"plain_text": hostile(), "html": RNG.choice(["<p>x</p>", hostile(), "a & <b>"])}, "creator": user_json(), "room": {"id": message["room_id"]}, "url": f"http://campfire.test/rooms/{message['room_id']}/messages/{message['id']}"}


for _ in range(n(15)):
    case("messages/json_by_bots_index", {"input": [message_json() for _ in range(RNG.randrange(0, 4))]})
    case("messages/json_by_bots_show", {"input": message_json()})
    case("messages/json_boosts_by_bots_show", {"input": {"id": RNG.randrange(1, 10**6), "content": hostile(), "created_at": "2026-09-26T12:26:46.848Z", "booster": user_json(), "message": {"id": 5, "url": "http://campfire.test/rooms/1/messages/5"}}})

# ---- the rest of the view-models -------------------------------------------------------------------------------------------
for _ in range(n(20)):
    users = [{"user": RNG.choice(USERS), "attachable_sgid": hostile()} for _ in range(RNG.randrange(0, 5))]
    case("autocompletable/users_index_json", {"users": users, "base_url": RNG.choice(["http://campfire.test", ""])})
    case("searches/search_path", {"query": hostile()})
    case("rooms/room_display_name", {"name": RNG.choice([None, "HQ", hostile()]), "direct": RNG.random() < 0.5, "others": [hostile() for _ in range(RNG.randrange(0, 4))], "for_user": RNG.choice([None, "David", hostile()])})
    case("rooms/button_to_delete_room", {"room_id": RNG.randrange(1, 10**6), "display_name": hostile()}, random_ctx())
    case("rooms/mention_prompt_src", {"room_id": RNG.randrange(1, 10**6)})

# ==== unit 4.2: the hot-path templates ============================================================================
# The cases below read messages the way the presenters do: from the seeds' rows (the 169 messages of each seed with their
# rich text, boosts and attachments, rooms, memberships), plus generated variations (hostile strings, every presentation).

for _ in range(n(60)):
    case("helpers/application", {"name": "to_lowercase", "text": RNG.choice(HOSTILE)})
SIGMAS = [
    "Σ", "ΑΣ", "ΑΣΑ", "ΣΑ", "ΑΣ ", "Α Σ", "Α.Σ", "Α'Σ", "ΑΣ'Α", "ΑΣ.Α", "ΑΣ.", "ΟΔΥΣΣΕΥΣ", "ΑΣΣ", "ΑΣΣΑ", "1ΣΑ", "Α1Σ", "ǅΣ", "ʰΣ", "Σʰ",
    "áΣ", "Σ́a", "ΑΣ­Α", "Α­Σ", "ΑΣ­", "ΑΣ​", "ΑΣ_", "İΣ", "ΑΣͅ", "ΑΣʹΑ", "ΑΣ:Α", "ΑΣ·Α", "ΑΣ’Α",
    "Σ\n", "ß ẞ Σ", "ΣΣΣ", "ΑΣ퟿", "𝒜Σ", "ΑΣ𝒜",
]
for text in SIGMAS:
    case("helpers/application", {"name": "to_lowercase", "text": text})
for start in range(0, len(chars), 4000):
    case("helpers/application", {"name": "to_lowercase", "text": "".join(chars[start : start + 4000]) + "Σ"})
    if start >= 0x30000 // SCALE and SCALE == 1 and start > 0x40000:
        break
# Σ after, between and before every code point
for start in range(0, 0x3000, 500):
    case("helpers/application", {"name": "to_lowercase", "text": "".join("Α" + c + "Σ" + c + "Α" for c in chars[start : start + 500])})
    case("helpers/application", {"name": "to_lowercase", "text": "".join("Σ" + c + "Σ" for c in chars[start : start + 500])})

EMOJI = ["👍", "🎉", "❤️", "👀", "💯", "🔥", "😂", "Hello", "ok", "a b", "<3", "&amp;"]
FRAME = lambda: RNG.random() < 0.3


def seed_worlds():
    worlds = []
    for seed in sorted(SEEDS.iterdir()):
        path = seed / "db/production.sqlite3"
        if not path.exists():
            continue
        db = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
        world = {"name": seed.name, "users": {}, "rooms": {}, "members": {}, "messages": [], "bodies": {}, "boosts": {}, "attachments": {}, "memberships": [], "searches": []}
        for id, name, bio, email, role, status, created in db.execute("select id, name, bio, email_address, role, status, created_at from users"):
            world["users"][id] = {
                "id": id, "name": name, "bio": bio, "email_address": email, "role": ["member", "administrator", "bot"][role],
                "status": ["active", "deactivated", "banned"][status],
                "avatar_path": f"/users/{id}/avatar?v={created.replace('-', '').replace(':', '').replace(' ', '')}",
            }
        for id, name, kind, updated in db.execute("select id, name, type, updated_at from rooms"):
            world["rooms"][id] = {"id": id, "name": name, "kind": kind.split("::")[1].lower(), "updated_at": rfc3339(updated)}
        for id, room_id, user_id, involvement, unread, updated in db.execute("select id, room_id, user_id, involvement, unread_at, updated_at from memberships"):
            world["memberships"].append({"id": id, "room_id": room_id, "user_id": user_id, "involvement": involvement, "unread": unread is not None, "updated_at": rfc3339(updated)})
            world["members"].setdefault(room_id, []).append(user_id)
        for id, client_id, created, updated, room_id, creator_id in db.execute("select id, client_message_id, created_at, updated_at, room_id, creator_id from messages order by id"):
            world["messages"].append({"id": id, "client_message_id": client_id, "created_at": rfc3339(created), "updated_at": rfc3339(updated), "room_id": room_id, "creator_id": creator_id})
        for record_id, body in db.execute("select record_id, body from action_text_rich_texts where record_type = 'Message'"):
            world["bodies"][record_id] = body or ""
        for id, booster_id, content, updated, message_id in db.execute("select id, booster_id, content, updated_at, message_id from boosts order by id"):
            world["boosts"].setdefault(message_id, []).append({"id": id, "booster_id": booster_id, "content": content, "updated_at": rfc3339(updated), "message_id": message_id})
        for record_id, blob_id, filename, content_type, metadata in db.execute(
            "select a.record_id, b.id, b.filename, b.content_type, b.metadata from active_storage_attachments a join active_storage_blobs b on b.id = a.blob_id where a.record_type = 'Message' and a.name = 'attachment'"
        ):
            world["attachments"][record_id] = {"blob_id": blob_id, "filename": filename, "content_type": content_type, "metadata": json.loads(metadata or "{}")}
        for user_id, query in db.execute("select user_id, query from searches order by id"):
            world["searches"].append({"user_id": user_id, "query": query})
        db.close()
        worlds.append(world)
    return worlds


WORLDS = seed_worlds()


def user_of(world, user_id):
    """A seed user, or a stand-in for one a row names that the seed doesn't have (a deleted account)."""
    return world["users"].get(user_id) or {"id": user_id, "name": "Deleted user", "bio": None, "email_address": None, "role": "member", "status": "deactivated", "avatar_path": f"/users/{user_id}/avatar"}


def title_of(user):
    return user["name"] + (" – " + user["bio"] if user["bio"] else "")


def uview(user):
    return {"id": user["id"], "name": user["name"], "title": title_of(user), "avatar_url": user["avatar_path"]}


def room_name_of(world, room_id):
    room = world["rooms"][room_id]
    if room["kind"] != "direct":
        return room["name"] or ""
    return " and ".join(world["users"][u]["name"] for u in world["members"].get(room_id, []) if u in world["users"]) or "Direct"


def attachment_content(world, message_id):
    blob = world["attachments"].get(message_id)
    if not blob:
        return None
    meta = blob["metadata"]
    kind = blob["content_type"] or ""
    base = f"/rails/active_storage/blobs/redirect/sgid-{blob['blob_id']}/{blob['filename']}"
    if kind.startswith("video/"):
        preview = {"type": "video", "poster_url": f"/rails/active_storage/representations/redirect/sgid-{blob['blob_id']}/{blob['filename']}.webp"}
    elif kind.startswith("image/") and kind != "image/bmp":
        preview = {"type": "image", "thumb_url": f"/rails/active_storage/representations/redirect/sgid-{blob['blob_id']}/thumb-{blob['filename']}"}
    else:
        preview = {"type": "file"}
    return {"type": "attachment", "filename": blob["filename"], "blob_path": base, "download_path": base + "?disposition=attachment", "preview": preview, "width": meta.get("width"), "height": meta.get("height")}


def seed_message_view(world, row):
    message_id = row["id"]
    content = attachment_content(world, message_id) or {"type": "text", "html": '<div class="trix-content">' + world["bodies"].get(message_id, "") + "</div>"}
    boosts = [
        {"id": b["id"], "updated_at": b["updated_at"], "message_id": message_id, "content": b["content"], "all_emoji": not b["content"].isalnum(), "booster": uview(user_of(world, b["booster_id"]))}
        for b in world["boosts"].get(message_id, [])
    ]
    return {
        "id": message_id, "client_message_id": row["client_message_id"], "room_id": row["room_id"], "room_name": room_name_of(world, row["room_id"]),
        "creator": uview(user_of(world, row["creator_id"])), "created_at": row["created_at"], "updated_at": row["updated_at"],
        "all_emoji": not world["bodies"].get(message_id, "x").isalnum() and "<" not in world["bodies"].get(message_id, "<"), "content": content, "boosts": boosts,
    }


def hostile_message_view(kind=None):
    """A generated message: every presentation, hostile text everywhere it is written, boosts, edited or not."""
    kind = kind or RNG.choice(["text", "text", "text", "mention", "embed", "sound", "image", "video", "file", "unrenderable"])
    mention = '<p>hi <span class="mention" sgid="BAh7CEkiCGdpZAY6BkVUSSI">@' + "JZ" + '</span> &amp; <em>you</em></p>'
    if kind == "text":
        content = {"type": "text", "html": RNG.choice(["<p>hi</p>", "<div class=\"trix-content\">" + hostile() + "</div>", "<p>a &amp; b</p>"])}
    elif kind == "mention":
        content = {"type": "text", "html": mention}
    elif kind == "embed":
        content = {"type": "text", "html": '<div class="og-embed"><a href="https://example.com/?a=1&amp;b=2"><strong>Title</strong></a><p>' + hostile() + "</p></div>"}
    elif kind == "sound":
        content = {"type": "sound", "url": RNG.choice(["/assets/a-d1g3st.mp3", hostile()]), "image": RNG.choice([None, {"src": RNG.choice(["/i.png", hostile()]), "width": RNG.randrange(0, 400), "height": RNG.randrange(0, 400)}]), "text": RNG.choice([None, hostile()])}
    elif kind in ("image", "video", "file"):
        preview = {"image": {"type": "image", "thumb_url": hostile()}, "video": {"type": "video", "poster_url": hostile()}, "file": {"type": "file"}}[kind]
        content = {
            "type": "attachment", "filename": RNG.choice(["a.png", "launch notes.txt", hostile()]), "blob_path": RNG.choice(["/rails/blobs/a?x=1&y=2", hostile()]),
            "download_path": RNG.choice(["/rails/blobs/a?disposition=attachment", hostile()]), "preview": preview, "width": random_dimension(), "height": random_dimension(),
        }
    else:
        content = {"type": "unrenderable"}
    base = RNG.choice(SEED["messages"]) if SEED["messages"] else {"id": 1, "client_message_id": "0001", "created_at": "2026-01-01T00:00:00Z", "updated_at": "2026-01-01T00:00:00Z", "room_id": 1}
    created = base["created_at"]
    updated = RNG.choice([created, base["updated_at"], RNG.choice(timestamps)])
    boosts = []
    for _ in range(RNG.choice([0, 0, 0, 1, 2, 5])):
        boosts.append({"id": RNG.randrange(1, 10**6), "updated_at": RNG.choice(timestamps), "message_id": base["id"], "content": RNG.choice(EMOJI + [hostile()]), "all_emoji": RNG.random() < 0.5, "booster": user_view()})
    creator = user_view()
    if RNG.random() < 0.4:
        creator = dict(creator, name=hostile(), title=hostile(), avatar_url=RNG.choice(["/a", "/a?v=1&w=2", hostile()]))
    return {
        "id": base["id"] if RNG.random() < 0.7 else RNG.randrange(1, 10**9), "client_message_id": RNG.choice([base["client_message_id"], hostile(), "ab12\"cd", "a b"]), "room_id": base["room_id"] if RNG.random() < 0.7 else RNG.randrange(1, 10**9),
        "room_name": RNG.choice([hostile(), "HQ", "Jason and JZ"]), "creator": creator, "created_at": created, "updated_at": updated, "all_emoji": RNG.random() < 0.2,
        "content": content, "boosts": boosts,
    }


def all_messages(world):
    return [seed_message_view(world, row) for row in world["messages"]]


def message_cases(view, ctx_fn=random_ctx):
    """Every template that renders one message, for one view."""
    second = dict(view, room_name=view["room_name"] + " (renamed)")
    case("messages/_message", {"message": view}, ctx_fn())
    case("messages/message_cached", {"message": view, "second": second}, ctx_fn())
    case("messages/_actions", {"message": view}, ctx_fn())
    case("messages/_presentation", {"message": view}, ctx_fn())
    case("messages/show", {"message": view}, ctx_fn())
    case("messages/edit", {"edit": {"message": view, "editable_body_html": RNG.choice(["<p>hi</p>", hostile(), "<p>a &amp; b \"q\"</p>"])}}, ctx_fn())
    case("messages/create_turbo_stream", {"message": view, "room_kind": RNG.choice(["open", "closed", "direct"]), "fragment": RNG.random() < 0.5}, ctx_fn())
    case("messages/destroy_turbo_stream", {"message": view})
    case("messages/boosts/_boosts", {"message": view}, ctx_fn())
    case("messages/boosts/index", {"message": view}, ctx_fn())
    case("messages/boosts/new", {"message": view, "user": user_view()}, ctx_fn())
    case("messages/_template", {"user": user_view()}, ctx_fn())
    for boost in view["boosts"]:
        case("messages/boosts/_boost", {"boost": boost}, ctx_fn())


def user_view():
    user = RNG.choice(USERS)
    return {"id": user["id"], "name": user["name"], "title": title_of(user), "avatar_url": user["avatar_path"]}


case("messages/_unrenderable", None)
case("messages/room_not_found", None)
for world in WORLDS:
    messages = all_messages(world)
    sample = messages if world["name"] == "default" else RNG.sample(messages, min(len(messages), n(25)))
    for view in sample:
        message_cases(view)
for kind in ["text", "mention", "embed", "sound", "image", "video", "file", "unrenderable"]:
    for _ in range(n(8)):
        message_cases(hostile_message_view(kind))
for _ in range(n(20)):
    message_cases(hostile_message_view())
if not WORLDS:
    for _ in range(n(20)):
        message_cases(hostile_message_view())

# ---- the room page -------------------------------------------------------------------------------------------------------
JOIN = ACCOUNTS[0]["join_code"]


def stream_name(*parts):
    return "ImJvb20iLWFiYw--" + "".join(str(p) for p in parts) + "e3f2"


def show_view(world, room_id, messages, user=None, invitation=None):
    room = world["rooms"][room_id]
    return {
        "room": {"id": room_id, "kind": room["kind"], "name": room["name"], "display_name": room_name_of(world, room_id)},
        "updated_at": room["updated_at"],
        "user": user or uview(RNG.choice(list(world["users"].values()))),
        "messages": messages, "invitation": RNG.random() < 0.3 if invitation is None else invitation,
        "join_code": RNG.choice([JOIN, hostile()]), "messages_stream_name": stream_name("room", room_id),
    }


for world in WORLDS:
    by_room = {}
    for view in all_messages(world):
        by_room.setdefault(view["room_id"], []).append(view)
    for room_id in world["rooms"]:
        messages = by_room.get(room_id, [])[-40:]
        for mode in (["view", "mixed"] if world["name"] == "default" else ["view"]):
            case("rooms/show", {"show": show_view(world, room_id, messages), "mode": mode, "frame": False}, random_ctx())
        if world["name"] == "default":
            case("rooms/show", {"show": show_view(world, room_id, messages), "mode": "view", "frame": True}, random_ctx())
            case("rooms/show", {"show": show_view(world, room_id, [], invitation=True), "mode": "view", "frame": False}, random_ctx())
            case("rooms/show", {"show": show_view(world, room_id, messages[:3], invitation=True), "mode": "mixed", "frame": True}, random_ctx())
# the page of messages a client fetches while scrolling (the last 40 of each room; an empty one; generated ones)
for world in WORLDS:
    by_room = {}
    for view in all_messages(world):
        by_room.setdefault(view["room_id"], []).append(view)
    for room_id in world["rooms"]:
        for mode in (["view", "mixed"] if world["name"] == "default" else ["view"]):
            case("messages/index", {"messages": by_room.get(room_id, [])[-40:], "mode": mode}, random_ctx())
for _ in range(n(15)):
    case("messages/index", {"messages": [hostile_message_view() for _ in range(RNG.randrange(0, 7))], "mode": RNG.choice(["view", "mixed"])}, random_ctx())

for _ in range(n(30)):
    messages = [hostile_message_view() for _ in range(RNG.randrange(0, 9))]
    show = {
        "room": {"id": RNG.randrange(1, 10**9), "kind": RNG.choice(["open", "closed", "direct"]), "name": RNG.choice([None, "HQ", hostile()]), "display_name": RNG.choice(["HQ", hostile()])},
        "updated_at": RNG.choice(timestamps), "user": user_view(), "messages": messages, "invitation": RNG.random() < 0.4,
        "join_code": RNG.choice([JOIN, hostile()]), "messages_stream_name": RNG.choice([stream_name("x"), hostile()]),
    }
    case("rooms/show", {"show": show, "mode": RNG.choice(["view", "mixed"]), "frame": FRAME()}, random_ctx(account=dict(name=hostile(), logo_url="/account/logo?v=1", has_logo=RNG.random() < 0.5)))

for kind in ["open", "closed", "direct"]:
    for involvement in ["mentions", "everything", "nothing", "invisible"]:
        case("rooms/involvements/show", {"involvement": {"room_id": RNG.randrange(1, 10**9), "kind": kind, "involvement": involvement}}, random_ctx())

for world in WORLDS[:2]:
    by_room = {}
    for view in all_messages(world):
        by_room.setdefault(view["room_id"], []).append(view)
    for room_id, room in world["rooms"].items():
        views = by_room.get(room_id, [])
        for new, updated in [(0, 0), (0, 2), (3, 0), (2, 2), (6, 4)]:
            if len(views) >= new + updated:
                case(
                    "rooms/refreshes/show",
                    {"refresh": {"room_id": room_id, "room_kind": room["kind"], "new_messages": views[:new], "updated_messages": views[new : new + updated]}, "mode": RNG.choice(["view", "mixed"])},
                    random_ctx(),
                )
for _ in range(n(15)):
    case(
        "rooms/refreshes/show",
        {"refresh": {"room_id": RNG.randrange(1, 10**9), "room_kind": RNG.choice(["open", "closed", "direct"]), "new_messages": [hostile_message_view() for _ in range(RNG.randrange(0, 4))], "updated_messages": [hostile_message_view() for _ in range(RNG.randrange(0, 4))]}, "mode": RNG.choice(["view", "mixed"])},
        random_ctx(),
    )

# ---- room forms ---------------------------------------------------------------------------------------------------------------
def user_views(count):
    base = [uview(u) for u in USERS]
    out = []
    for i in range(count):
        user = dict(base[i % len(base)])
        if i >= len(base) or RNG.random() < 0.2:
            user = dict(user, id=user["id"] + 1000 * (i // len(base) + 1), name=RNG.choice([user["name"] + str(i), hostile()]))
            user["title"] = user["name"]
        out.append(user)
    return out


def form_room(new):
    return {"id": None if new else RNG.randrange(1, 10**9), "name": RNG.choice([None, "HQ", hostile()]) if not new else RNG.choice([None, None, "x"])}


for _ in range(n(60)):
    count = RNG.choice([0, 1, 4, 19, 20, 21, 25, 40])
    new = RNG.random() < 0.4
    form = {"room": form_room(new), "can_administer": RNG.random() < 0.7, "users": user_views(count)}
    case("rooms/opens/new" if new else "rooms/opens/edit", {"form": form, "frame": FRAME()}, random_ctx())
    users = user_views(count)
    split = RNG.randrange(0, len(users) + 1)
    selected, unselected = users[:split], users[split:]
    me = users[0]["id"] if users else 1
    form = {"room": form_room(new), "can_administer": RNG.random() < 0.7, "current_user_id": RNG.choice([me, me, 1]), "selected_users": selected if not new else users[:1], "unselected_users": unselected if not new else users[1:]}
    case("rooms/closeds/new" if new else "rooms/closeds/edit", {"form": form, "frame": FRAME()}, random_ctx())
    case("rooms/directs/new", {"frame": FRAME()}, random_ctx())
    case("rooms/directs/edit", {"edit": {"room_id": RNG.randrange(1, 10**9), "display_name": RNG.choice(["Jason", hostile()]), "users": user_views(RNG.randrange(1, 5))}, "frame": FRAME()}, random_ctx())
    room = form_room(RNG.random() < 0.4)
    case("rooms/layouts/_form", {"room": room, "can_administer": RNG.random() < 0.5, "kind": RNG.choice(["open", "closed"]), "content": safe_html()}, random_ctx())

# ---- search -----------------------------------------------------------------------------------------------------------------------
QUERIES = ["pizza", "launch plan", "a & b", "<b>", "\"quoted\"", "日本語", "x" * 80, "100%", "a+b"]
for world in WORLDS[:2]:
    views = all_messages(world)
    for query in QUERIES:
        found = RNG.sample(views, min(len(views), RNG.choice([0, 1, 5, 20, 100])))
        recents = RNG.sample(QUERIES, RNG.randrange(0, 6))
        index = {"query": query, "q": RNG.choice([query, query.upper(), hostile()]), "messages": found, "recent_searches": recents, "return_to_room_id": RNG.choice(list(world["rooms"]))}
        case("searches/index", {"index": index, "mode": RNG.choice(["view", "mixed"]), "frame": FRAME()}, random_ctx())
for _ in range(n(15)):
    index = {"query": RNG.choice([None, hostile()]), "q": RNG.choice([None, hostile()]), "messages": [hostile_message_view() for _ in range(RNG.randrange(0, 4))], "recent_searches": [hostile() for _ in range(RNG.randrange(0, 4))], "return_to_room_id": RNG.randrange(1, 10**9)}
    case("searches/index", {"index": index, "mode": RNG.choice(["view", "mixed"]), "frame": FRAME()}, random_ctx())
case("searches/index", {"index": {"query": None, "q": None, "messages": [], "recent_searches": [], "return_to_room_id": 1}, "mode": "view", "frame": False}, random_ctx())

# ---- the mentions prompt -----------------------------------------------------------------------------------------------------------
for _ in range(n(20)):
    users = [{"user": RNG.choice(USERS), "attachable_sgid": RNG.choice([hostile(), "BAh7CEkiCGdpZAY6BkVU--abc"])} for _ in range(RNG.randrange(0, 6))]
    if RNG.random() < 0.4 and users:
        users[0] = {"user": dict(users[0]["user"], name=hostile(), bio=RNG.choice([None, hostile()])), "attachable_sgid": hostile()}
    case("autocompletable/users/index", {"users": users}, random_ctx())
    case("autocompletable/users/_prompt_item", {"user": RNG.choice(users) if users else {"user": RNG.choice(USERS), "attachable_sgid": "s"}}, random_ctx())
for user in USERS:
    case("autocompletable/users/_prompt_item", {"user": {"user": user, "attachable_sgid": user["attachable_sgid"]}}, random_ctx())

# ---- the sidebar ----------------------------------------------------------------------------------------------------------------------
def sidebar_for(world, user_id, mode):
    me = world["users"][user_id]
    memberships = [m for m in world["memberships"] if m["user_id"] == user_id]
    direct, shared = [], []
    for m in memberships:
        room = world["rooms"][m["room_id"]]
        if room["kind"] == "direct":
            others = [world["users"][u] for u in world["members"].get(m["room_id"], []) if u != user_id and u in world["users"]]
            direct.append({
                "room_id": m["room_id"], "unread": m["unread"], "updated_at_epoch": str(RNG.randrange(10**12, 2 * 10**12)),
                "members": others or [me], "membership_id": m["id"], "membership_updated_at": m["updated_at"],
            })
        elif m["involvement"] != "invisible":
            shared.append({"id": m["room_id"], "param_key": "rooms_" + room["kind"], "name": room["name"] or "", "unread": m["unread"]})
    shared.sort(key=lambda r: r["name"].lower())
    talked = {u["id"] for d in direct for u in d["members"]}
    placeholders = [u for u in world["users"].values() if u["status"] == "active" and u["role"] != "bot" and u["id"] not in talked and u["id"] != user_id]
    return {
        "current_user": me, "rooms_stream": stream_name("rooms"), "user_rooms_stream": stream_name("user", user_id), "direct": direct,
        "placeholders": placeholders, "shared": shared, "can_create_rooms": me["role"] == "administrator" or RNG.random() < 0.5, "mode": mode,
    }


def user_context(world, user_id):
    return random_ctx(current_user=user_ctx(world["users"][user_id]))


for world in WORLDS:
    for user_id in world["users"]:
        for mode, frame in [("view", False), ("mixed", False), ("view", True)]:
            if world["name"] != "default" and (mode, frame) != ("view", False):
                continue
            case("users/sidebars/show", dict(sidebar_for(world, user_id, mode), frame=frame), user_context(world, user_id))
for _ in range(n(30)):
    members = lambda: [dict(RNG.choice(USERS), name=hostile()) if RNG.random() < 0.3 else RNG.choice(USERS) for _ in range(RNG.randrange(1, 7))]
    direct = [
        {"room_id": RNG.randrange(1, 10**9), "unread": RNG.random() < 0.5, "updated_at_epoch": str(RNG.randrange(10**12, 2 * 10**12)), "members": members(), "membership_id": RNG.randrange(1, 10**6), "membership_updated_at": RNG.choice(timestamps_for_cache)}
        for _ in range(RNG.randrange(0, 7))
    ]
    sidebar = {
        "current_user": dict(RNG.choice(USERS), name=hostile()) if RNG.random() < 0.3 else RNG.choice(USERS), "rooms_stream": RNG.choice([stream_name("rooms"), hostile()]), "user_rooms_stream": RNG.choice([stream_name("u"), hostile()]),
        "direct": direct, "placeholders": [RNG.choice(USERS) for _ in range(RNG.randrange(0, 4))],
        "shared": [{"id": RNG.randrange(1, 10**9), "param_key": RNG.choice(["rooms_open", "rooms_closed"]), "name": RNG.choice(["HQ", hostile()]), "unread": RNG.random() < 0.5} for _ in range(RNG.randrange(0, 6))],
        "can_create_rooms": RNG.random() < 0.5, "mode": RNG.choice(["view", "mixed"]), "frame": FRAME(),
    }
    case("users/sidebars/show", sidebar, random_ctx())
