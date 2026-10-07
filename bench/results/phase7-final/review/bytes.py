#!/usr/bin/env python3
# Phase 7 final review: capture decoded bodies of the benchmarked routes from one app (frozen clock), for a byte diff.
#   bytes.py BASE OUTDIR
import gzip, http.client, json, re, sys, urllib.parse, os
base, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
host = urllib.parse.urlparse(base).netloc
labels = json.load(open("parity/.seed/default/labels.json"))
room, write_room, before = labels["rooms.watercooler"], labels["rooms.hq"], labels["messages.busy_060"]
jar = {}
def req(method, path, body=None, headers=None, gz=True):
    c = http.client.HTTPConnection(host, timeout=30)
    h = {"accept-encoding": "gzip" if gz else "identity"}
    if jar: h["cookie"] = "; ".join(f"{k}={v}" for k, v in jar.items())
    h.update(headers or {})
    c.request(method, path, body=body, headers=h)
    r = c.getresponse(); raw = r.read()
    for k, v in r.getheaders():
        if k.lower() == "set-cookie":
            kv = v.split(";", 1)[0]; n, _, val = kv.partition("="); jar[n] = val
    dec = gzip.decompress(raw) if r.getheader("content-encoding") == "gzip" else raw
    return r.status, r.getheader("content-encoding"), raw, dec
csrf_re = re.compile(rb'name="csrf-token" content="([^"]+)"')
s, _, _, page = req("GET", "/session/new")
m = csrf_re.search(page); tok = m.group(1).decode() if m else ""
form = urllib.parse.urlencode({"email_address": labels["emails.david"], "password": labels["passwords.all"], "authenticity_token": tok})
s, _, _, _ = req("POST", "/session", form, {"content-type": "application/x-www-form-urlencoded", "sec-fetch-site": "same-origin"})
assert s == 302, s
summary = {}
def grab(name, path, **kw):
    s, enc, raw, dec = req("GET", path, **kw)
    open(f"{out}/{name}", "wb").write(dec)
    summary[name] = {"status": s, "encoding": enc, "wire": len(raw), "decoded": len(dec)}
    return dec
page = grab("room_show.html", f"/rooms/{room}")
grab("room_show.identity.html", f"/rooms/{room}", gz=False)
grab("messages_page.html", f"/rooms/{room}/messages?before={before}")
grab("sidebar.html", "/users/me/sidebar")
grab("search.html", "/searches?q=coffee")
m = csrf_re.search(page); csrf = m.group(1).decode() if m else ""
body = urllib.parse.urlencode({"message[body]": "final review post", "message[client_message_id]": "review-0001", "authenticity_token": csrf})
s, enc, raw, dec = req("POST", f"/rooms/{write_room}/messages", body, {"content-type": "application/x-www-form-urlencoded",
    "accept": "text/vnd.turbo-stream.html, text/html, application/xhtml+xml", "x-csrf-token": csrf, "sec-fetch-site": "same-origin"})
open(f"{out}/post_message.txt", "wb").write(dec)
summary["post_message.txt"] = {"status": s, "encoding": enc, "wire": len(raw), "decoded": len(dec)}
grab("write_room_after_post.html", f"/rooms/{write_room}")
json.dump(summary, open(f"{out}/summary.json", "w"), indent=1)
print(json.dumps(summary))
