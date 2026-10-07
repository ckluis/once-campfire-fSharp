#!/usr/bin/env python3
"""Writes inputs for bin/richtext-differential, one JSON string per line.

  generate.py KIND COUNT SEED CORPUS

KIND is what the inputs aim at:
  fragments  random markup built from every HTML tag, attribute and parser trap, and the corpus
             bodies cut, spliced and mutated (for the parse comparison: trees, EOF handling)
  depth      nesting around Gumbo's tree depth limit and tags around its attribute limit
  pipeline   corpus bodies with attachments, links, tables and mentions spliced in
  autolink   URLs, email addresses, punctuation, Unicode and tags in a row

CORPUS is the corpus file whose bodies and SGIDs seed them.
"""
import json
import random
import sys

kind, count, seed, corpus_path = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), sys.argv[4]
random.seed(seed)
corpus = json.load(open(corpus_path))
bodies = [c["body"] for c in corpus["cases"]]
sgids = [u["attachable_sgid"] for u in corpus["users"]] + [s["sgid"] for s in corpus["signed"]]

TAGS = ["a", "abbr", "address", "area", "article", "aside", "audio", "b", "base", "basefont", "bdi", "bdo", "bgsound", "big", "blockquote", "body", "br", "button", "canvas", "caption", "center", "cite", "code", "col", "colgroup", "data", "datalist", "dd", "del", "details", "dfn", "dialog", "dir", "div", "dl", "dt", "em", "embed", "fieldset", "figcaption", "figure", "font", "footer", "form", "frame", "frameset", "h1", "h2", "h3", "h4", "h5", "h6", "head", "header", "hgroup", "hr", "html", "i", "iframe", "image", "img", "input", "ins", "isindex", "kbd", "keygen", "label", "legend", "li", "link", "listing", "main", "map", "mark", "marquee", "math", "menu", "meta", "meter", "mglyph", "mi", "mn", "mo", "ms", "mtext", "malignmark", "annotation-xml", "nav", "nobr", "noembed", "noframes", "noscript", "object", "ol", "optgroup", "option", "output", "p", "param", "picture", "plaintext", "pre", "progress", "q", "rb", "rp", "rt", "rtc", "ruby", "s", "samp", "script", "search", "section", "select", "slot", "small", "source", "span", "strike", "strong", "style", "sub", "summary", "sup", "svg", "table", "tbody", "td", "template", "textarea", "tfoot", "th", "thead", "time", "title", "tr", "track", "tt", "u", "ul", "var", "video", "wbr", "xmp", "foreignObject", "desc", "clipPath", "linearGradient", "action-text-attachment", "figure", "circle", "path", "g", "A", "DIV", "Svg", "TaBlE"]
ATTRS = ["href", "src", "class", "id", "style", "title", "name", "type", "value", "onclick", "xlink:href", "xml:lang", "xmlns", "xmlns:xlink", "viewBox", "viewbox", "color", "face", "size", "encoding", "definitionURL", "definitionurl", "data-x", "a", "b", "c", "shadowrootmode", "form", "alt", "content-type", "sgid", "content", "tabindex", "HREF", "Class"]
VALS = ["", "x", "javascript:alert(1)", "a b", "a\"b", "a'b", "a&amp;b", "&lt;b&gt;", "&#106;avascript:", "text/html", "hidden", "open", "closed", "1", "0 0 1 1", "http://example.com/", "  ", "a\nb", "\u00a0", "&bogus;", "&notit;", "&amp", "&#0;", "&#x41;", "&#128;", "&copy", "&copy=1", "<b>", "<script>", "color: red"]
TEXTS = ["x", "hello world", " ", "\n", "\n\n", "\t", "a&amp;b", "&lt;", "&gt;", "&amp", "&copy", "&copy;", "&notin;", "&notit;", "&#65;", "&#x41", "&#0;", "&#xD800;", "&#x110000;", "&#128512;", "&bogus", "&nbsp;", "\u00a0", "\u00e9", "\U0001F600", "\ufeff", "\r\n", "\r", "\x00", "\x0b", "\x0c", "<", "<<", ">", "&", "&&", ";", "]]>", "<!--", "-->", "--", "<![CDATA[x]]>", "<?php x ?>", "<!DOCTYPE html>", "<!doctype html public \"x\" \"y\">", "</", "</>", "<>", "<3", "</3", "<a", "<a ", "<a b", "<a b=", "<a b='c", "<p/", "<br/>", "<img/>", "<script>", "</script>", "<style>", "</style>", "<title>", "</title>", "<textarea>", "</textarea>", "<plaintext>", "<xmp>", "</xmp>", "<noscript>", "<iframe>", "<noembed>", "<noframes>", "<!--[if IE]>", "<!-->", "<!--->", "<!---->", "<!-- a -- b -->", "<!x>", "<!>", "<!", "<!-", "<!--x", "<![", "<![CDATA[", "<script><!--", "<script><!--<script>", "<script><!--</script>", "</script x>"]


def rand_attrs():
    out = []
    for _ in range(random.choice([0, 0, 0, 1, 1, 2, 3])):
        a = random.choice(ATTRS)
        form = random.random()
        if form < 0.15:
            out.append(a)
        elif form < 0.5:
            out.append('%s="%s"' % (a, random.choice(VALS).replace('"', "&quot;")))
        elif form < 0.7:
            out.append("%s='%s'" % (a, random.choice(VALS).replace("'", "&#39;")))
        else:
            out.append("%s=%s" % (a, random.choice(["x", "1", "a/b", "a>b", "'", "\"", "`x`", "x=y", ""])))
    return (" " + " ".join(out)) if out else ""


def rand_frag(depth=0):
    parts = []
    for _ in range(random.randint(1, 8)):
        r = random.random()
        if r < 0.35:
            parts.append("<%s%s%s>" % (random.choice(TAGS), rand_attrs(), "/" if random.random() < 0.05 else ""))
        elif r < 0.6:
            parts.append("</%s>" % random.choice(TAGS))
        elif r < 0.9:
            parts.append(random.choice(TEXTS))
        else:
            parts.append(rand_frag(depth + 1) if depth < 2 else "x")
    return "".join(parts)


def fragments():
    out = []
    for b in bodies:
        out.append(b)
        if len(b) > 3:
            out += [b[: random.randint(0, len(b))] for _ in range(3)]
            s = list(b)
            for _ in range(2):
                s.insert(random.randint(0, len(s)), random.choice(TEXTS))
            out.append("".join(s))
    while len(out) < count:
        out.append(rand_frag())
    random.shuffle(out)
    return out[:count]


def depth():
    fmt = ["b", "i", "a", "span", "div", "p", "em", "font", "u", "s", "code", "big", "nobr"]
    blocks = ["div", "p", "table", "td", "tr", "ul", "li", "h1", "blockquote", "pre", "form", "select", "option", "svg", "math", "g", "mi", "template", "button", "table", "tbody"]

    def nested():
        out = []
        n = random.choice([random.randint(380, 420), random.randint(1, 40), random.randint(390, 402)])
        pool = random.choice([fmt, blocks, fmt + blocks])
        for _ in range(n):
            t = random.choice(pool)
            r = random.random()
            if r < 0.85:
                out.append("<%s>" % t)
            elif r < 0.93:
                out.append("</%s>" % random.choice(pool))
            elif r < 0.97:
                out.append("x")
            else:
                out.append("<%s id=%d>" % (t, random.randint(1, 9)))
        if random.random() < 0.3:
            out.append(random.choice(["</p>", "<table>x", "x", "<td>", "<!---->", "</b></b>"]))
        return "".join(out)

    def attributes():
        names = ["a%d=%d" % (i, i) for i in range(1, random.choice([399, 400, 401, 402, 10, 1000, 2000]) + 1)]
        if random.random() < 0.3:
            names.append("a1=dup")
        tag = random.choice(["p", "b", "textarea", "div", "svg", "table", "script", "style", "title"])
        form = random.choice(["element", "textarea", "open", "end"])
        if form == "element":
            return "<%s %s>x</%s>" % (tag, " ".join(names), tag)
        if form == "textarea":
            return "<textarea><p %s>" % " ".join(names)
        if form == "open":
            return "<%s %s" % (tag, " ".join(names))
        return "<p>x</p %s>" % " ".join(names)

    return [nested() if random.random() < 0.8 else attributes() for _ in range(count)]


URLS = ["http://example.com", "https://example.com/a?b=1&amp;c=2", "www.example.com", "www.a.com/x", "ftp://x.y/z", "mailto:a@b.com", "http://example.com.", "(http://example.com/a_(b))", "http://example.com/a,", "\"http://example.com\"", "http://example.com/&gt;", "me@example.com", "a.b@c.d.e", "x@y", "@z.com", "a@b.c.", "foo_bar@baz-qux.org", "HTTP://UPPER.COM", "javascript:alert(1)", "http://example.com/<b>", "https://twitter.com/dhh/status/1", "https://x.com/dhh/status/1?s=20", "http://[::1]/x", "http://ex ample.com", "\u017fsh://a.b", "www.\u00e9.com", "http://a.b/\u00e9", "ed2k://x"]
FRAGMENTS = ["<p>", "</p>", "<div>", "</div>", "<a href=\"%s\">", "</a>", "<b>", "</b>", "<pre>", "</pre>", "<table><tr><td>", "</td></tr></table>", "<ul><li>", "</li></ul>", "<ol><li>", "<blockquote>", "</blockquote>", "<br>", "<h1>", "</h1>", "<span style=\"color: red; x: y\">", "<img src=\"%s\">", "<script>x</script>", "<svg><a>x</a></svg>", "<!-- c -->", "<a name=\"a b\" href=\"x y\">", "<abbr title=\"x> http://evil.test/\">", "<p title=\"a>b me@x.com\">", "\n", "  ", "&amp;", "&lt;", "\u00a0", "\u00e9", "\U0001F600", "<mark style=\"color: var(--h)\">", "<s>", "<u>", "<figure>", "<figcaption>", "</figcaption></figure>", "<template>", "<select><option>", "<noscript>", "<action-text-attachment>", "</action-text-attachment>"]


def attachment():
    k = random.choice(["mention", "expired", "plain", "og", "content", "image", "video", "gallery", "trix", "missing"])
    s = random.choice(sgids)
    if k == "mention":
        return '<action-text-attachment sgid="%s" content-type="application/vnd.campfire.mention"></action-text-attachment>' % s
    if k == "plain":
        return '<action-text-attachment sgid="%s"></action-text-attachment>' % s
    if k == "og":
        return '<action-text-attachment content-type="application/vnd.actiontext.opengraph-embed" url="%s" href="%s" filename="T" caption="C"></action-text-attachment>' % (random.choice(URLS), random.choice(URLS))
    if k == "content":
        return '<action-text-attachment content-type="text/html" content="&lt;p&gt;%s&lt;/p&gt;"></action-text-attachment>' % random.choice(["x", "<b>y</b>", "http://a.b"])
    if k == "image":
        return '<action-text-attachment content-type="image/png" url="%s" caption="cap" width="1"></action-text-attachment>' % random.choice(["/a.png", "http://x/y.png", "bad", "", "cid:1"])
    if k == "video":
        return '<action-text-attachment content-type="video/mp4" url="/a.mp4" filename="f.mp4"></action-text-attachment>'
    if k == "gallery":
        return '<div><action-text-attachment presentation="gallery" content-type="image/png" url="/1.png"></action-text-attachment>' + random.choice(["", "\n", " "]) + '<action-text-attachment presentation="gallery" content-type="image/png" url="/2.png"></action-text-attachment></div>'
    if k == "trix":
        return '<figure data-trix-attachment=\'{"contentType":"image/png","url":"/x.png","width":%s}\' data-trix-attributes=\'{"presentation":"gallery"}\'></figure>' % random.choice(["1", "\"2\"", "[1]", "null"])
    return '<action-text-attachment content-type="application/pdf"></action-text-attachment>'


def piece():
    r = random.random()
    if r < 0.3:
        return random.choice(URLS)
    if r < 0.5:
        return attachment()
    f = random.choice(FRAGMENTS)
    return f % random.choice(URLS + ["/x", "x"]) if "%s" in f else f


def pipeline():
    out = list(bodies)
    for _ in range(count):
        r = random.random()
        if r < 0.4:
            b = random.choice(bodies)
            i = random.randint(0, len(b))
            out.append(b[:i] + piece() + b[i:])
        elif r < 0.6:
            a, b = random.choice(bodies), random.choice(bodies)
            out.append(a[: random.randint(0, len(a))] + b[random.randint(0, len(b)):])
        else:
            out.append("".join(piece() for _ in range(random.randint(1, 10))))
    random.shuffle(out)
    return out[:count]


def autolink():
    pieces = ["http://", "https://", "www.", "ftp://", "HTTP://", "\u017fsh://", "s\u017fh://", "Kelvin", "file://", "mailto:", "a@b.c", "x.y@z.org", "me+tag@sub.example.co.uk", "a.@b.c", "@", "<", ">", "</a>", "<a href=\"x\">", "<a>", "<A\n href=y>", "<p title=\"", "\">", "\n", "\r\n", " ", "\t", "\u00a0", "\u00e9", "\u65e5\u672c\u8a9e", "\U0001F600", "(", ")", "[", "]", "{", "}", ".", ",", ";", ":", "!", "?", "'", "\"", "`", "/", "-", "_", "=", "&amp;", "&gt;", "&lt;", "&quot;", "&#39;", "%40", "%3A", "a", "b", "1", "example", "com", "org", "path/to", "?q=1", "#frag", "x=y", "</", "<br>", "<b>", "</b>", "<script>", "\u2028", "\u0085", "\u200b", "\u0301", "\u01c5", "\u0130", "\u017f", "\u212a"]

    def text():
        return "".join(random.choice(pieces) for _ in range(random.randint(1, 25)))

    out = []
    for _ in range(count):
        r = random.random()
        if r < 0.5:
            out.append("<p>" + text() + "</p>")
        elif r < 0.8:
            out.append(text())
        else:
            out.append("<div>" + text() + "<p title='" + text() + "'>" + text() + "</p></div>")
    return out


for line in {"fragments": fragments, "depth": depth, "pipeline": pipeline, "autolink": autolink}[kind]():
    print(json.dumps(line))
