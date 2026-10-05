#!/usr/bin/env python3
"""Sends random requests to the Rust kit's http.rs app and to the same app on Campfire.Kit, and compares
what comes back: status, headers and body.

  fuzz.py RUST_BINARY FSHARP_COMMAND SEED COUNT plain|ssl

The requests draw on every route of the app and on what Rails and browsers send to them: methods,
Accept headers, conditional headers, cookies the servers themselves set earlier, Sec-Fetch-Site and Origin,
forms, JSON, multipart bodies, method overrides, odd request ids. Both servers are started here, on
ports of their own, with the same secret and a frozen clock, so a cookie one set the other reads.

What is left out of the comparison, because it is not the kit's:
  - Date, Server, X-Runtime and the connection and framing headers;
  - the compressed bytes of a gzipped body (each side deflates its own way): what they decode to is compared;
  - the Allow header axum adds to its 404 for a method a route lacks;
  - Content-Length: 0 on a HEAD answered 204 or 304, which hyper sends and Kestrel (RFC 9110) doesn't;
  - the value of an encrypted session cookie and the session ids in bodies (random per server).
`_method=head` is not sent: a POST answered as a HEAD has no framing of its own to compare (AdapterTests
checks what the F# kit does with it).
Exits 1 if anything else differs.
"""
import collections
import gzip
import os
import random
import re
import shlex
import socket
import subprocess
import sys
import time

rust_binary, fsharp_command, seed, count, mode = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4]), sys.argv[5]
rng = random.Random(seed)
rust_port, fsharp_port = (4001, 4002) if mode == "plain" else (4003, 4004)
extra = ["ssl"] if mode == "ssl" else []
env = dict(os.environ, DOTNET_ROOT=os.path.expanduser("~/.dotnet"))
servers = [
    subprocess.Popen([rust_binary, str(rust_port)] + extra, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL),
    subprocess.Popen(shlex.split(fsharp_command) + [str(fsharp_port)] + extra, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=env),
]
for port in (rust_port, fsharp_port):
    for _ in range(100):
        try:
            socket.create_connection(("127.0.0.1", port), timeout=1).close()
            break
        except OSError:
            time.sleep(0.1)


def send(port, raw):
    connection = socket.create_connection(("127.0.0.1", port), timeout=10)
    connection.sendall(raw)
    data = b""
    while True:
        try:
            chunk = connection.recv(65536)
        except socket.timeout:
            break
        if not chunk:
            break
        data += chunk
    connection.close()
    return data


def decode_chunked(body):
    out, i = b"", 0
    while i < len(body):
        eol = body.find(b"\r\n", i)
        if eol < 0:
            break
        size = int(body[i:eol].split(b";")[0].strip() or b"0", 16)
        i = eol + 2
        if size == 0:
            break
        out += body[i:i + size]
        i += size + 2
    return out


def parse(data):
    if not data:
        return None
    head, _, body = data.partition(b"\r\n\r\n")
    lines = head.decode("latin-1").split("\r\n")
    headers = []
    for line in lines[1:]:
        name, _, value = line.partition(":")
        headers.append((name.strip().lower(), value.strip().encode("latin-1").decode("utf-8", "replace")))
    if any(k == "transfer-encoding" and "chunked" in v for k, v in headers):
        body = decode_chunked(body)
    return int(lines[0].split(" ")[1]), headers, body


SKIPPED = {"date", "server", "x-runtime", "connection", "keep-alive", "transfer-encoding", "allow"}
ENCODINGS = ["gzip", "gzip, deflate, br", "identity", "gzip;q=0", "*", "identity;q=0, *;q=0", "br", "gzip;q=0.5, identity;q=0.9", "deflate", ""]


def normalize(response, method, path):
    status, headers, body = response
    if any(k == "content-encoding" and v == "gzip" for k, v in headers):
        # Each side's deflate is its own: the bytes differ and what they decode to is the body. The
        # header holds the OS code and the modification time, which are the same.
        header = body[:10]
        body = header[3:4] + header[4:8] + header[9:10] + gzip.decompress(body) if body else body
    kept = []
    for name, value in headers:
        if name in SKIPPED:
            continue
        if name == "etag" and path.split("?")[0] == "/session":
            continue  # its body holds a session id, random per server
        if name == "x-request-id" and re.fullmatch(r"[0-9a-f-]{36}", value):
            value = "<uuid>"
        if name == "set-cookie":
            m = re.match(r"([^=]+)=([^;]*)(.*)", value)
            if m and m.group(1) == "_campfire_session" and m.group(2) != "":
                value = m.group(1) + "=<encrypted>" + m.group(3)
        if name == "content-length" and value == "0" and method == "HEAD" and status in (204, 304):
            continue
        kept.append((name, value))
    if not any(k == "content-length" for k, v in kept) and status not in (204, 304) and status >= 200 and body == b"":
        kept.append(("content-length", "0"))
    kept.sort()
    text = re.sub(r'"id":"[0-9a-f]{32}"', '"id":"<sid>"', body.decode("utf-8", "replace"))
    return status, kept, text


ACCEPTS = ["text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", "text/vnd.turbo-stream.html, text/html, application/xhtml+xml",
           "application/json", "*/*", "image/png", "garbage", "text/html", "text/javascript", "application/xml", "text/*"]
PATHS = ["/rooms/5", "/rooms/abc", "/form", "/echo/9", "/echo/9?a=1&b[]=2&b[]=3", "/echo/1?a=1&a[b]=2", "/echo/1?x=%", "/echo/1?x=%FF",
         "/upload", "/session", "/noop", "/notice", "/flash", "/sign_in", "/live_sign_in", "/whoami", "/sign_out", "/admin", "/messages",
         "/messages.json", "/messages.xml", "/messages?format=json", "/autocomplete", "/redirect", "/redirect?to=relative",
         "/redirect?to=other", "/redirect?to=other_allowed", "/redirect?to=see_other", "/redirect?to=back", "/created", "/logo", "/fresh",
         "/nope", "/nope.json", "/file?path=%2Fnonexistent", "/rooms/5?x=1"]
METHODS = ["GET"] * 10 + ["POST"] * 6 + ["HEAD"] * 2 + ["PUT", "PATCH", "DELETE", "OPTIONS"]
ETAGS = ['"x"', 'W/"x"', "*", '"a", "b"', ""]
DATES = ["Wed, 01 May 2024 00:00:00 GMT", "Sat, 01 Jun 2024 12:00:00 GMT", "garbage", "Thu, 01 Jan 1970 00:00:00 GMT"]
cookies = []
etags = collections.defaultdict(list)


def request_body(method):
    if method not in ("POST", "PUT", "PATCH") or rng.random() < 0.2:
        return None, None
    kind = rng.choice(["form", "form", "json", "multipart", "text", "override"])
    if kind == "form":
        return "application/x-www-form-urlencoded", rng.choice(
            ["value=%2Frooms%2F1", "a[b][]=1&a[b][]=2&b=body&id=body", "user[name]=Jo", "x=%", "", "message[body]=hi&message[client_message_id]=1",
             "authenticity_token=abc&x=1"])
    if kind == "override":
        return "application/x-www-form-urlencoded", "_method=" + rng.choice(["patch", "delete", "put", "bogus", "get", "PATCH"]) + "&user[name]=Jo"
    if kind == "json":
        return "application/json", rng.choice(['{"a":1}', '{"user":{"name":"Jo"}}', "{nope", "[1,2]", '{"_method":"patch"}', ""])
    if kind == "text":
        return "text/plain", "Hello!"
    b = "----fz"
    body = rng.choice([
        f'--{b}\r\nContent-Disposition: form-data; name="_method"\r\n\r\npatch\r\n--{b}\r\nContent-Disposition: form-data; name="user[name]"\r\n\r\nJo\r\n'
        f'--{b}\r\nContent-Disposition: form-data; name="user[avatar]"; filename="me.png"\r\nContent-Type: image/png\r\n\r\nPNGDATA\r\n--{b}--\r\n',
        f'--{b}\r\nContent-Disposition: form-data; name="user[name]"\r\n\r\nJo\r\n--{b}--\r\n',
        f'--{b}\r\nContent-Disposition: form-data; name="x"\r\n\r\ntruncated',
    ])
    return f"multipart/form-data; boundary={b}", body


def make_request():
    method, path = rng.choice(METHODS), rng.choice(PATHS)
    headers = [("host", rng.choice(["chat.example.com"] * 6 + ["chat.example.com:3000", "localhost", "other.example"]))]
    if rng.random() < 0.6:
        headers.append(("accept", rng.choice(ACCEPTS)))
    if rng.random() < 0.3:
        headers.append(("if-none-match", rng.choice((etags.get(path.split("?")[0]) or []) + ETAGS)))
    if rng.random() < 0.15:
        headers.append(("if-modified-since", rng.choice(DATES)))
    if rng.random() < 0.6:
        headers.append(("accept-encoding", rng.choice(ENCODINGS)))
    if cookies and rng.random() < 0.5:
        headers.append(("cookie", "; ".join(rng.sample(cookies, min(len(cookies), rng.randint(1, 3))))))
    if rng.random() < 0.5:
        headers.append(("sec-fetch-site", rng.choice(["same-origin", "same-site", "cross-site", "none", "bogus"])))
    if rng.random() < 0.25:
        headers.append(("origin", rng.choice(["http://chat.example.com", "https://chat.example.com", "null", "https://evil.example", "http://localhost"])))
    if rng.random() < 0.25:
        headers.append(("referer", rng.choice(["http://chat.example.com/rooms/3", "https://evil.example/x", "http://chat.example.com@evil.example/x", "/rooms/1"])))
    if rng.random() < 0.25:
        headers.append(("x-user", rng.choice(["jo", "admin"])))
    if rng.random() < 0.1:
        headers.append(("turbo-frame", "x"))
    if rng.random() < 0.1:
        headers.append(("x-requested-with", "XMLHttpRequest"))
    if rng.random() < 0.1:
        headers.append(("x-forwarded-proto", rng.choice(["https", "http"])))
    if rng.random() < 0.05:
        headers.append(("x-http-method-override", rng.choice(["patch", "delete", "bogus"])))
    if rng.random() < 0.05:
        headers.append(("x-request-id", rng.choice(["abc-123<script>", "ok_id", "  ", "café-1"])))
    content_type, body = request_body(method)
    if content_type:
        headers.append(("content-type", content_type))
    raw = f"{method} {path} HTTP/1.1\r\n".encode()
    for name, value in headers:
        raw += f"{name}: {value}\r\n".encode()
    payload = body.encode() if body is not None else None
    if payload is not None:
        raw += f"content-length: {len(payload)}\r\n".encode()
    raw += b"connection: close\r\n\r\n"
    if payload is not None:
        raw += payload
    return method, path, raw


# The mode took effect on both: a form posted without Sec-Fetch-Site is refused behind TLS, and not without.
probe = b"POST /form HTTP/1.1\r\nhost: chat.example.com\r\ncontent-type: application/x-www-form-urlencoded\r\ncontent-length: 3\r\nconnection: close\r\n\r\nx=1"
for port in (rust_port, fsharp_port):
    status = parse(send(port, probe))[0]
    if status != (422 if mode == "ssl" else 200):
        sys.exit(f"{mode} mode: the probe got {status} from port {port}")

differences = []
statuses = collections.Counter()
for _ in range(count):
    method, path, raw = make_request()
    rust, fsharp = parse(send(rust_port, raw)), parse(send(fsharp_port, raw))
    if rust is None or fsharp is None:
        differences.append((raw, None, None))
        continue
    statuses[rust[0]] += 1
    for name, value in rust[1]:
        if name == "set-cookie":
            cookie = value.split(";")[0]
            if cookie.split("=")[0] in ("session_token", "last_room", "_campfire_session") and "=;" not in value and len(cookies) < 30 and rng.random() < 0.5:
                cookies.append(cookie)
        if name == "etag":
            etags[path.split("?")[0]].append(value)
    a, b = normalize(rust, method, path), normalize(fsharp, method, path)
    if a != b:
        differences.append((raw, a, b))
for server in servers:
    server.terminate()

print(f"{mode}: {count} requests, {len(differences)} differ; statuses {dict(sorted(statuses.items()))}")
for raw, a, b in differences[:5]:
    print("====")
    print(raw.decode("latin-1")[:600])
    if a is None or b is None:
        print("  no response:", "rust" if a is None else "f#")
        continue
    print("  rust status", a[0], " f# status", b[0])
    for header in a[1]:
        if header not in b[1]:
            print("   only rust:", header)
    for header in b[1]:
        if header not in a[1]:
            print("   only f#:  ", header)
    if a[2] != b[2]:
        print("   body rust:", repr(a[2][:300]), "\n   body f#:  ", repr(b[2][:300]))
sys.exit(1 if differences else 0)
