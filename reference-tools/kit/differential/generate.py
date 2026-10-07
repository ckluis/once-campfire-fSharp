#!/usr/bin/env python3
"""Generates the inputs bin/kit-differential feeds to both kits, one JSON object per line.

  generate.py COUNT SEED     COUNT inputs of each kind (the kinds are listed in src/main.rs), from SEED

The generators draw from the shapes Rails and browsers produce and from the edges where parsers
disagree: Accept headers with every kind of q-value, cookie headers with bad escapes, proxy headers with
odd addresses, JSON with numbers at the limits of an i64, query strings that conflict with themselves, and
multipart bodies cut short, with unquoted or escaped names, `filename*`, a missing final boundary.
"""
import json
import random
import sys

count = int(sys.argv[1]) if len(sys.argv) > 1 else 1000
seed = int(sys.argv[2]) if len(sys.argv) > 2 else 1
rng = random.Random(seed)


def pick(*choices):
    return rng.choice(choices)


# --- Accept headers and formats ---------------------------------------------------------------------------

MIMES = ["text/html", "application/json", "*/*", "text/*", "application/*", "image/png", "text/xml", "application/xml",
         "application/rss+xml", "text/vnd.turbo-stream.html", "garbage", "", "application/x-unknown", "image/svg+xml",
         "text/javascript", "application/javascript", "application/xhtml+xml", "text/plain", "audio/mpeg", "video/mp4",
         "application/atom+xml", "x/y", "a/b+xml", "text/yaml", "application/problem+json"]
Q_PARAMS = ["", ";q=0.5", ";q=", ";q=abc", ";q=1e2", ";Q=0.3", " ; q=0.9", ";charset=utf-8", ';q="0.7"', ";q=0", ";q=1",
            ";q=0.0001", ";q=-1", ";q=+0.2", ";x=1;q=0.4", ";q=0.4;x=1", ";q=1e400", ";q=0x1", ";q= 0.5", ";q=.5"]


def accept_header():
    items = [rng.choice(MIMES) + rng.choice(Q_PARAMS) for _ in range(rng.randint(1, 6))]
    header = rng.choice([", ", ",", " , ", ",  "]).join(items)
    if rng.random() < 0.1:
        header = " " + header
    if rng.random() < 0.05:
        header += ","
    if rng.random() < 0.05:
        header = ",".join(['"' + rng.choice(MIMES) + '"', rng.choice(MIMES)])
    return header


def accept_input():
    return {"op": "accept", "input": accept_header()}


def formats_input():
    case = {"op": "formats", "xhr": rng.random() < 0.3,
            "path": pick("/", "/a.json", "/x.unknown", "/a/b.svg", "/x.", "/m.html", "/q.JSON", "/a.b.c.png", "/x.md")}
    if rng.random() < 0.8:
        case["accept"] = accept_header() if rng.random() < 0.8 else pick("", "  ", "*/*", "application/json")
    if rng.random() < 0.3:
        case["content_type"] = pick("application/json", "text/html; charset=utf-8", "garbage", "", "application/x-www-form-urlencoded")
    if rng.random() < 0.25:
        case["format_param"] = pick("json", "html", "nope", "", "JSON", "md")
    return case


# --- cookies -------------------------------------------------------------------------------------------------

COOKIE_PARTS = ["a=1", "b=x%20y", "c", "d=%zz", "e=%FF", "a=2", " f=1", "g=+", "", "h=%41%42", "i=é", "j=%", "k=a=b",
                "l=%e2%9c%93", "m=%2", "=x", "n= v ", "o=%00"]


def cookie_input():
    parts = [rng.choice(COOKIE_PARTS) for _ in range(rng.randint(1, 6))]
    return {"op": "cookie", "input": rng.choice(["; ", ";", "; ;", ";  ", " ; "]).join(parts)}


# --- requests: host, protocol and client address from proxy headers -------------------------------------------

HOSTS = ["chat.example.com", "chat.example.com:3000", "localhost", "[::1]:3000", "[2001:db8::1]", "10.0.0.1:8080",
         "example.com:", "example.com:80", "EXAMPLE.com:443", "a:b:c", "chat.example.com:99999", "x:0"]
ADDRESSES = ["203.0.113.9", "10.0.0.2", "127.0.0.1", "198.51.100.7", "::1", "2001:db8::1", "[2001:db8::2]",
             "[2001:db8::3]:4711", "203.0.113.9:1234", "bogus", "", "01.2.3.4", "1.2.3", "fe80::1", "fc00::5",
             "192.168.1.1", "172.16.5.4", "172.32.0.1", "::ffff:1.2.3.4", "  8.8.8.8  ", "8.8.8.8,"]


def request_headers():
    headers = []
    if rng.random() < 0.8:
        headers.append(["host", rng.choice(HOSTS)])
    if rng.random() < 0.25:
        headers.append(["x-forwarded-host", rng.choice(HOSTS[:8]) + rng.choice(["", ", " + rng.choice(HOSTS[:5])])])
    if rng.random() < 0.3:
        headers.append(["x-forwarded-proto", pick("https", "http", "https, http", "http,https", "wss", "ftp", "", "HTTPS")])
    if rng.random() < 0.1:
        headers.append(["x-forwarded-ssl", pick("on", "off")])
    if rng.random() < 0.1:
        headers.append(["x-forwarded-scheme", pick("https", "http")])
    if rng.random() < 0.2:
        headers.append(["forwarded", pick('for=203.0.113.9;proto=https', 'for="[2001:db8::1]:4711"', 'proto=http, proto=https',
                                          'for=10.0.0.1, for=203.0.113.9;host=a.example', 'for=bogus', 'proto="https"',
                                          "For=198.51.100.7")])
    if rng.random() < 0.5:
        headers.append(["x-forwarded-for", ", ".join(rng.choice(ADDRESSES) for _ in range(rng.randint(1, 4)))])
    if rng.random() < 0.15:
        headers.append(["client-ip", rng.choice(ADDRESSES)])
    if rng.random() < 0.3:
        headers.append(["content-type", pick("application/json; charset=utf-8", "TEXT/HTML", "multipart/form-data; boundary=x",
                                             "text/plain, application/json")])
    # An empty header value is not a header to ASP.NET's dictionary, so none is sent.
    return [h for h in headers if h[1] != ""]


def request_input():
    return {"op": "request", "headers": request_headers(), "assume_ssl": rng.random() < 0.2,
            "peer": pick("127.0.0.1", "198.51.100.4", "::1", "10.1.2.3", "2001:db8::9", "192.168.0.9"),
            "uri": pick("/rooms/1?x=1", "/", "/a?", "/a/b?c=d&e=f")}


# --- JSON bodies, forms, strong parameters -----------------------------------------------------------------------

SCALARS = [None, True, False, 0, 1, -1, 42, 2**63 - 1, -2**63, 2**63, 2**64 - 1, 2**64, 1.0, 0.5, -0.0, 1e21, 1.5e-7, 1e16,
           1e15, 123456.789, 0.1, 1e-5, 0.0001, 100.0, "", "x", "héllo ✓", "a b", "<>&\"\\\n", "\u0000", "𝄞"]


def random_json(depth=0):
    r = rng.random()
    if depth > 3 or r < 0.35:
        return rng.choice(SCALARS)
    if r < 0.6:
        return [random_json(depth + 1) for _ in range(rng.randint(0, 4))]
    return {rng.choice(["a", "b", "c", "name", "user", "k1", "é", "x y", ""]): random_json(depth + 1) for _ in range(rng.randint(0, 4))}


NUMBERS = ["1e400", "-1e400", "1E5", "1e-400", "00", "01", "-", "+1", "1.", "1.e5", ".5", "0x10", "123456789012345678901234567890",
           "-9223372036854775809", "18446744073709551616", "1.7976931348623157e308", "4.9e-324", "[1,2,]", '{"a":1,}',
           '"\\ud800"', '"\\udc00\\ud800"', '"\\u0041"', '"\\x41"', "nan", "NaN", "true", "null", '"s"', "[]", "{}"]


def json_body_input():
    text = json.dumps(random_json(), ensure_ascii=rng.random() < 0.5)
    r = rng.random()
    if r < 0.1:
        text = text[:rng.randint(0, len(text))]
    elif r < 0.15:
        text += " x"
    elif r < 0.2:
        text = "\ufeff" + text
    elif r < 0.25:
        text = text.replace("}", ",}", 1)
    elif r < 0.3:
        text = "/*c*/" + text
    elif r < 0.35 and '"a"' in text:
        text = text.replace('"a"', '"a":1,"a"', 1)
    elif r < 0.4:
        text = text.replace("{", '{"dup":1,"dup":null,', 1)
    elif r < 0.5:
        number = rng.choice(NUMBERS)
        text = number if rng.random() < 0.5 else '{"v":' + number + "}"
    elif r < 0.55:
        depth = rng.choice([10, 60, 126, 127, 128, 129, 200])
        text = "[" * depth + "]" * depth if rng.random() < 0.5 else '{"a":' * depth + "1" + "}" * depth
    return {"op": "json_body", "input": text}


FORM_PARTS = ["a=1", "b[]=2", "b[]=3", "c[d]=4", "c[e][f]=5", "x", "y=", "=z", "a=%FF", "%FF=1", "k=%", "e[]=%41", "m[0][n]=1",
              "m[1][n]=2", "u=%E2%9C%93", "u+v=w+x", "q[]", "q[][r]=1", "s[t][]=1", "s[t]=2", "dup=1", "dup=2", " sp=1"]


def form_input():
    parts = [rng.choice(FORM_PARTS) for _ in range(rng.randint(0, 6))]
    body = "&".join(parts)
    r = rng.random()
    if r < 0.1:
        body += "\x00"
    elif r > 0.9:
        body += "&  " + rng.choice(FORM_PARTS)
    raw = body.encode("utf-8")
    if rng.random() < 0.05:
        raw += bytes([0xFF, 0xFE])
    return {"op": "form", "hex": raw.hex()}


KEYS = ["name", "avatar", "tags", "settings", "user", "a", "b"]
QUERIES = ["user[name]=Jo&user[admin]=1&user[tags][]=a&user[settings][x][y]=1&user[bad][]=1&blank=+&user[date(1i)]=2024",
           "a[b][c]=1&a[b][d]=2&list[][c]=1&list[][d]=2&ff[0][c]=1&ff[1][c]=2&name=x&tags[]=1&tags[]=2&tags[][x]=1",
           "name=1&name[]=2", "avatar=&b(2f)=3&b(1i)=1&b(i)=2&b(x)=3",
           "settings[a]=1&settings[b][]=2&settings[b][]=3&settings[c][][d]=1&settings[c][][e]=2"]


def random_filter(depth=0):
    kind = rng.choice(["key", "key", "array", "hash", "nested"])
    name = rng.choice(KEYS)
    if kind == "nested" and depth < 2:
        return {"k": "nested", "n": name, "c": [random_filter(depth + 1) for _ in range(rng.randint(0, 3))]}
    return {"k": "key" if kind == "nested" else kind, "n": name}


def permit_input():
    return {"op": "permit", "input": rng.choice(QUERIES), "filters": [random_filter() for _ in range(rng.randint(1, 5))],
            "require": rng.choice(KEYS + ["blank", "missing"])}


# --- multipart ---------------------------------------------------------------------------------------------------

FIELD_NAMES = ["x", "user[name]", "user[avatar]", "tags[]", "a b", "é", "", "file", "m[0][n]", "a[]b", "_method",
               "message[attachment]", "dup", "x;y", "q=r"]
FILE_NAMES = ["f.txt", "me.png", "a/b.png", "C:\\dir\\cat.png", "", "100%.txt", "%41.txt", "%zz", "résumé.pdf",
              'with "quotes".txt', "semi;colon.txt", "back\\slash", "日本.txt", "  spaced  .txt", "..", "x.tar.gz",
              "a%2Fb.png", "%"]
CONTENTS = [b"", b"x", b"hello world", b"PNGDATA", "héllo ✓".encode(), b"\xff\xfe", b"line1\r\nline2", b"--B", b"--Bx", b"a" * 5000]


def quote(value):
    style = rng.random()
    if style < 0.7:
        return '"' + value.replace("\\", "\\\\" if rng.random() < 0.3 else "\\").replace('"', '\\"') + '"'
    return value if style < 0.85 else "'" + value + "'"


def disposition():
    parts = []
    if rng.random() < 0.9:
        parts.append(pick("name", "NAME", "Name") + pick("=", " = ") + quote(rng.choice(FIELD_NAMES)))
    if rng.random() < 0.5:
        name = rng.choice(FILE_NAMES)
        kind = rng.random()
        if kind < 0.7:
            parts.append("filename=" + quote(name))
        elif kind < 0.85:
            parts.append("filename*=" + pick("UTF-8''", "utf-8''", "iso-8859-1''", "''", "UTF-8'en'") +
                         pick("r%C3%A9sum%C3%A9.pdf", "%41", "f.txt", "%zz", "a%2Fb", "%E2%9C%93.txt", ""))
        else:
            parts.append("filename=" + quote(name))
            parts.append("filename*=UTF-8''" + pick("star%2Eexe", "x.txt"))
    separator = pick("; ", "; ", ";", " ; ")
    value = pick("form-data", "form-data", "form-data", "FORM-DATA", "attachment", "file")
    if parts:
        value += separator + separator.join(parts)
    return value + (";" if rng.random() < 0.05 else "")


def multipart_part():
    headers = []
    if rng.random() < 0.95:
        headers.append((pick("Content-Disposition", "content-disposition", "CONTENT-DISPOSITION"), disposition()))
    if rng.random() < 0.5:
        headers.append((pick("Content-Type", "content-type"), pick("image/png", "text/plain; charset=utf-8", "application/octet-stream", "", "IMAGE/PNG")))
    if rng.random() < 0.15:
        headers.append(("Content-Transfer-Encoding", pick("binary", "base64")))
    if rng.random() < 0.1:
        headers.append(("X-Custom", "v"))
    if rng.random() < 0.05:
        headers.append(("Content-ID", "<abc>"))
    rng.shuffle(headers)
    return headers, rng.choice(CONTENTS)


def multipart_input():
    boundary = pick("B", "XyZ", "----campfire", "abc123", "B" * 70, "x-y_z", "a b")
    body = b""
    if rng.random() < 0.1:
        body += b"preamble text\r\n"
    for _ in range(rng.randint(0, 5)):
        headers, content = multipart_part()
        body += b"--" + boundary.encode() + b"\r\n"
        for name, value in headers:
            body += (name + ": " + value).encode() + b"\r\n"
        body += b"\r\n" + content + b"\r\n"
    if rng.random() < 0.9:
        body += b"--" + boundary.encode() + b"--\r\n"
    if rng.random() < 0.1:
        body += b"epilogue\r\n"
    r = rng.random()
    if r < 0.06:
        body = body[:rng.randint(0, max(1, len(body)))]
    elif r < 0.09:
        body = body.replace(b"\r\n", b"\n")
    elif r < 0.11:
        body = b""
    content_type = rng.choice(["multipart/form-data; boundary=" + boundary] * 6 +
                              ['multipart/form-data; boundary="' + boundary + '"', "multipart/form-data; charset=utf-8; boundary=" + boundary,
                               "MULTIPART/FORM-DATA; BOUNDARY=" + boundary, "multipart/form-data", "multipart/mixed; boundary=" + boundary,
                               "multipart/form-data; boundary="])
    case = {"op": "multipart", "hex": body.hex(), "content_type": content_type}
    if rng.random() < 0.15:
        case["limit"] = rng.choice([10, 100, 300, 1000, 100000])
    return case


TYPES = ["multipart", "form-data", "Multipart", "FORM-DATA", "text", "html", "x", "*", "a+b", "form-data+x", "json"]
BOUNDARY_PARAMS = ["boundary=B", 'boundary="B"', "BOUNDARY=abc", "boundary=", 'boundary=""', 'boundary="a b"', "boundary=a b",
                   'boundary="a;b"', "charset=utf-8", "charset=UTF-8", 'charset="utf-8"', "x=1", "boundary=B=C", 'boundary="B\\"C"',
                   'boundary="B', "boundary='B'", "boundary=B;", "boundary = B", " boundary=B", "boundary=a/b", 'boundary="\\""',
                   'q=""', 'boundary="x" ;', 'boundary="x" y']


def boundary_input():
    content_type = rng.choice(TYPES) + "/" + rng.choice(TYPES)
    if rng.random() < 0.05:
        content_type = pick("", "multipart", "/", "multipart/", "/form-data", "multipart/form-data/x", "multipart/form data")
    separator = pick("; ", ";", "  ; ", " ;", ";  ")
    content_type += "".join(separator + rng.choice(BOUNDARY_PARAMS) for _ in range(rng.randint(0, 3)))
    if rng.random() < 0.05:
        content_type += pick(";", "; ", ",", " ")
    return {"op": "boundary", "input": content_type}


for make in (accept_input, formats_input, cookie_input, request_input, json_body_input, form_input, permit_input,
             multipart_input, boundary_input):
    for _ in range(count):
        print(json.dumps(make(), ensure_ascii=False))
