#!/usr/bin/env python3
"""Make the "same pixels, different engines" images from the parity harness's captures, and check them.

The project page shows the same signed-in room page as Rails, Rust and F# render it. The pictures are the
parity harness's own screenshots, not mock-ups: Rails is the reference side and Rust the candidate side of a
run with PARITY_CANDIDATE_APP=rust, and F# the candidate side of a run with the default (F#) candidate. Both
runs serve the default seed with the clock frozen and capture Chromium at 1440x900, light scheme:

    PARITY_NET_DIR=/tmp/parity-net PARITY_CANDIDATE_APP=rust parity/bin/candidate compare --seed default \\
      --matrix lean --engines chromium --viewports desktop --schemes light --breakpoints exclude --out out/p6-r-default
    PARITY_NET_DIR=/tmp/parity-net parity/bin/candidate compare --seed default \\
      --matrix lean --engines chromium --viewports desktop --schemes light --breakpoints exclude --out out/p6-f-default

This checks the three PNGs byte for byte and decoded pixel for pixel. When they match, the page shows one of them
at full size (room.webp, lossless, so it is still the same pixels) and the three, each from its own capture, as
thumbnails in the side panel ({rails,rust,fsharp}.webp); all go to site/assets/same-pixels/, and what it found goes
under "same_pixels" in site/data.json. parity/out/ is not committed, so run it in a checkout that has the
captures (or pass --captures). Needs Pillow with WebP.

    python3 site/same_pixels.py [--captures DIR] [--thumb 640]
"""
import argparse
import hashlib
import json
import os
import sys
from datetime import datetime, timezone

SITE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(SITE)
DATA = os.path.join(SITE, 'data.json')
ASSETS = 'site/assets/same-pixels'

STATE = 'rooms/show/busy/last_page'  # GET /rooms/{watercooler} as David: the request bench/run measures as room_show
CELL = 'chromium-desktop-light'
APPS = [
    # key, name, tag, the app's name in bench/results, parity run, side of the run
    ('rails', 'Rails', 'slow', 'reference', 'p6-r-default', 'reference'),
    ('rust', 'Rust', 'fast', 'rust', 'p6-r-default', 'candidate'),
    ('fsharp', 'F#', 'faster', 'fsharp', 'p6-f-default', 'candidate'),
]


def sha(b):
    return hashlib.sha256(b).hexdigest()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--captures', default=os.path.join(REPO, 'parity', 'out'))
    ap.add_argument('--thumb', type=int, default=640)
    a = ap.parse_args()
    from PIL import Image, features
    if not features.check('webp'):
        sys.exit('same_pixels: this Pillow has no WebP support')

    apps, pixels = [], set()
    os.makedirs(os.path.join(REPO, ASSETS), exist_ok=True)
    for key, name, tag, bench, run, side in APPS:
        src = os.path.join(a.captures, run, side, STATE, CELL + '.png')
        report = json.load(open(os.path.join(a.captures, run, 'report.json'), encoding='utf-8'))
        cell = next(r for r in report['results'] if r['state'] == STATE and r['cell'] == CELL)
        raw = open(src, 'rb').read()
        im = Image.open(src).convert('RGB')
        pixels.add(sha(im.tobytes()))
        th = round(im.height * a.thumb / im.width)
        out = os.path.join(ASSETS, key + '.webp')
        im.resize((a.thumb, th), Image.LANCZOS).save(os.path.join(REPO, out), format='WEBP', lossless=True, quality=100, method=6)
        if key == 'rails':
            first = im
        apps.append({'key': key, 'name': name, 'tag': tag, 'bench_app': bench, 'run': run, 'side': side,
                     'harness_verdict': cell['status'], 'capture_sha256': sha(raw), 'thumb': out,
                     'started': report['info']['startedAt']})
        print('%-6s %s/%s  %s  png sha256 %s…' % (name, run, side, cell['status'], sha(raw)[:16]))

    captures = {x['capture_sha256'] for x in apps}
    identical = len(captures) == 1 and len(pixels) == 1
    print('byte-identical PNGs: %s; identical decoded pixels: %s' % (len(captures) == 1, len(pixels) == 1))
    w, h = im.size
    image = None
    if identical:
        # One picture serves for all three: the Rails capture at its own size, lossless.
        image = os.path.join(ASSETS, 'room.webp')
        first.save(os.path.join(REPO, image), format='WEBP', lossless=True, quality=100, method=6)
    d = json.load(open(DATA, encoding='utf-8'))
    d['same_pixels'] = {
        'state': STATE, 'cell': CELL, 'viewport': '%d×%d' % (w, h), 'image': image, 'thumb_size': '%d×%d' % (a.thumb, round(h * a.thumb / w)),
        'page': 'All Talk, the busy room (131 messages), signed in as David: the page the room_show benchmark requests',
        'identical': identical, 'bytes_identical': len(captures) == 1, 'pixels_identical': len(pixels) == 1,
        'pixels_sha256': sorted(pixels)[0] if len(pixels) == 1 else None,
        'checked': datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
        'apps': apps,
        'bench': {'dir': 'bench/results/phase7-final', 'route': 'room_show', 'conc': 16},
    }
    with open(DATA, 'w', encoding='utf-8') as f:
        json.dump(d, f, indent=2, ensure_ascii=False)
        f.write('\n')
    if not identical:
        sys.exit('same_pixels: the captures differ; the page will say so instead of "same pixels"')


if __name__ == '__main__':
    main()
