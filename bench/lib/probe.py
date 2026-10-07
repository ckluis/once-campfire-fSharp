#!/usr/bin/env python3
"""One request to each GET route of bench/run, gzip and identity: what an app answers, in bytes.

  probe.py BASE COOKIE 'name|/path' ...      # prints a JSON list

Per route and encoding: the status, Content-Encoding, Content-Type, the bytes on the wire (the body as
sent) and the bytes decoded (what the client gets after gunzip). A smaller response is not a fair win,
so bench/report puts these side by side for every app. Routes whose path is POST are skipped.
"""
import gzip, http.client, json, sys
from urllib.parse import urlsplit

base, cookie, specs = sys.argv[1], sys.argv[2], sys.argv[3:]
u = urlsplit(base)
out = []
for spec in specs:
    name, path = spec.split("|", 1)
    if path == "POST":
        continue
    row = {"route": name, "path": path}
    for enc in ("gzip", "identity"):
        c = http.client.HTTPConnection(u.hostname, u.port, timeout=30)
        c.request("GET", path, headers={"Cookie": cookie, "Accept-Encoding": enc})
        r = c.getresponse()
        body = r.read()
        ce = r.getheader("Content-Encoding")
        decoded = gzip.decompress(body) if ce == "gzip" else body
        row[enc] = {"status": r.status, "content_encoding": ce, "content_type": r.getheader("Content-Type"),
                    "wire_bytes": len(body), "decoded_bytes": len(decoded), "etag": r.getheader("ETag") is not None}
        c.close()
    out.append(row)
print(json.dumps(out))
