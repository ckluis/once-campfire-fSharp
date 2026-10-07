#!/usr/bin/env python3
"""Render index.html (at the repository root) from site/data.json and the Phase 7 log.

Python 3 standard library only. The page is a run of short beats, with the live climb chart from
bench/progress/climb.py at the top; every detail sits in a side panel a beat links to. Panels are
CSS :target drawers, so they work with JavaScript off; with it on they also close on Esc or a click
outside, keep focus inside while open, and give it back on close.

    python3 site/collect_tokens.py   # refresh token and commit figures from the transcripts
    python3 site/build.py            # write index.html

bench/progress/build.py runs this after every logged tuning run, so the chart stays current.
"""
import html
import json
import os
import re
import sys
from datetime import datetime, timedelta, timezone

SITE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(SITE)
sys.path.insert(0, os.path.join(REPO, 'bench', 'progress'))
import climb as climb_chart  # noqa: E402  (bench/progress/climb.py, shared with the dashboard)

EDT = timezone(timedelta(hours=-4))


def e(s):
    return html.escape(str(s), quote=True)


def n(x):
    return format(int(x), ',')


def mega(x, digits=1):
    x = float(x)
    if x >= 1e9:
        return ('%.' + str(digits) + 'f B') % (x / 1e9)
    if x >= 1e6:
        return ('%.' + str(digits) + 'f M') % (x / 1e6)
    if x >= 1e3:
        return '%.0f K' % (x / 1e3)
    return '%d' % x


def ts(iso):
    if not iso:
        return None
    if iso.endswith('Z'):
        iso = iso[:-1] + '+00:00'
    d = datetime.fromisoformat(iso)
    return d if d.tzinfo else d.replace(tzinfo=timezone.utc)


def local(iso, fmt='%-d %b %H:%M'):
    d = ts(iso)
    return d.astimezone(EDT).strftime(fmt) if d else ''


def duration(a, b):
    if not a or not b:
        return ''
    s = int((ts(b) - ts(a)).total_seconds())
    h, m = s // 3600, (s % 3600) // 60
    return '%dh %02dm' % (h, m) if h else '%d min' % m


def para(text):
    return ''.join('<p>%s</p>' % inline(p).replace('\n', '<br>') for p in str(text).split('\n\n'))


def inline(text):
    """Escape, then render `code` spans."""
    return re.sub(r'`([^`]+)`', r'<code>\1</code>', e(text))


def lead_int(s):
    m = re.match(r'\s*([\d,]+)', str(s or ''))
    return int(m.group(1).replace(',', '')) if m else None


def pills(items):
    return '<ul class="pills">%s</ul>' % ''.join('<li>%s</li>' % e(i) for i in items)


def tip(label, text, cls=''):
    """A small fact with a hover/focus card (CSS only)."""
    return ('<span class="has-tip %s" tabindex="0">%s<span class="tip" role="tooltip">%s</span></span>'
            % (cls, label, e(text)))


def status_badge(status):
    label = {'done': 'Done', 'in-progress': 'In progress', 'pending': 'Pending', 'running': 'Running'}.get(status, status)
    return '<span class="badge badge-%s"><span class="bdot" aria-hidden="true"></span>%s</span>' % (e(status), e(label))


def table(columns, rows, caption=None, num_from=1, cls='', ratio_col=None):
    head = ''.join('<th scope="col"%s>%s</th>' % (' class="num"' if i >= num_from else '', e(c)) for i, c in enumerate(columns))
    body = []
    for r in rows:
        cells = []
        for i, c in enumerate(r):
            if i == 0:
                cells.append('<th scope="row">%s</th>' % e(c))
            elif ratio_col is not None and i == ratio_col:
                cells.append(ratio_cell(c))
            else:
                cells.append('<td class="num">%s</td>' % e(c) if i >= num_from else '<td>%s</td>' % e(c))
        body.append('<tr>%s</tr>' % ''.join(cells))
    cap = '<caption>%s</caption>' % e(caption) if caption else ''
    return ('<div class="tw %s"><table>%s<thead><tr>%s</tr></thead><tbody>%s</tbody></table></div>'
            % (cls, cap, head, ''.join(body)))


def ratio_cell(text):
    m = re.match(r'([0-9.]+)', text)
    v = float(m.group(1)) if m else 0
    pct = max(0.0, min(v / 1.25, 1.0)) * 100
    tone = 'ahead' if v >= 1.0 else 'behind'
    return ('<td class="num ratio"><span class="rbar" aria-hidden="true"><span class="rfill %s" style="width:%.1f%%"></span>'
            '<span class="rone" style="left:80%%"></span></span><span class="rval">%s</span></td>' % (tone, pct, e(text)))


# ---------------------------------------------------------------- token charts (panel only)

def bar_chart(rows, title, desc, chart_id, series=None):
    """Horizontal bars. rows: [(label, sublabel, {series_key: value})]. series: [(key, label, css_var)]."""
    series = series or [('v', 'Tokens', '--s1')]
    W, label_w, row_h, gap, pad_r = 760, 210, 30, 16, 92
    plot_w = W - label_w - pad_r
    H = len(rows) * (row_h + gap) + 28
    vmax = max(sum(v.get(k, 0) for k, _, _ in series) for _, _, v in rows) or 1
    mag = 10 ** (len(str(int(vmax))) - 1)
    nice = next(m * mag for m in (1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10) if m * mag >= vmax)
    out = ['<svg class="bars" id="%s" viewBox="0 0 %d %d" role="img" aria-labelledby="%s-t %s-d">' % (chart_id, W, H, chart_id, chart_id),
           '<title id="%s-t">%s</title><desc id="%s-d">%s</desc>' % (chart_id, e(title), chart_id, e(desc))]
    for i in range(5):
        x = label_w + plot_w * i / 4
        out.append('<line class="bg" x1="%.1f" x2="%.1f" y1="0" y2="%d"/>' % (x, x, H - 22))
        out.append('<text class="bt" x="%.1f" y="%d" text-anchor="middle">%s</text>' % (x, H - 6, e(mega(nice * i / 4) if i else '0')))
    for idx, (label, sub, vals) in enumerate(rows):
        y = idx * (row_h + gap) + 4
        out.append('<text class="bl" x="%d" y="%.1f" text-anchor="end">%s</text>' % (label_w - 12, y + row_h / 2 - 2, e(label)))
        if sub:
            out.append('<text class="bs" x="%d" y="%.1f" text-anchor="end">%s</text>' % (label_w - 12, y + row_h / 2 + 12, e(sub)))
        x = label_w
        total = sum(vals.get(k, 0) for k, _, _ in series)
        segs = [(k, lab, var, vals.get(k, 0)) for k, lab, var in series if vals.get(k, 0) > 0]
        for j, (k, lab, var, v) in enumerate(segs):
            w = plot_w * v / nice
            last = j == len(segs) - 1
            out.append('<rect style="fill:var(%s)" x="%.1f" y="%.1f" width="%.1f" height="%d" rx="%d"><title>%s: %s %s</title></rect>'
                       % (var, x, y + 4, max(w - (0 if last else 2), 0.8), row_h - 8, 3 if last else 0, e(label), e(lab), n(v)))
            x += w
        out.append('<text class="bv" x="%.1f" y="%.1f">%s</text>' % (x + 8, y + row_h / 2 + 4, e(mega(total))))
    out.append('</svg>')
    legend = ''
    if len(series) > 1:
        legend = '<ul class="legend">%s</ul>' % ''.join(
            '<li><span class="sw" style="background:var(%s)"></span>%s</li>' % (var, e(lab)) for _, lab, var in series)
    return legend + '<div class="bars-box">%s</div>' % ''.join(out)


# ---------------------------------------------------------------- facts the page derives

def facts(d, cl):
    tok = d['tokens']
    g = tok['grand']
    b = d['chapters']['build']
    phases = b['phases']
    gate5 = next((p for p in phases if p['key'] == 'phase5'), {}).get('gate', '')
    renders = next((x['value'] for x in b['differentials'] if 'render' in x['label']), '')
    words = 0
    msgs = [m for m in d['prompts']['chris'] if m.get('kind') != 'attachment']
    for m in msgs:
        words += len(m['text'].split())
    words += d['prompts']['status_pings']['count']
    start, end = ts(tok['main']['first']), ts(tok['as_of'])
    vs_rails = []
    for row in d['chapters']['tuning']['baseline']['rows']:
        if 'c=16' in row[0]:
            ra, fs = float(row[1].replace(',', '')), float(row[3].replace(',', ''))
            vs_rails.append(fs / ra)
    findings = {'critical': 0, 'major': 0, 'minor': 0}
    for w in tok['workflows']:
        for a in w['agents']:
            if a['role'] == 'verify' and a.get('findings'):
                for k in findings:
                    findings[k] += a['findings'].get(k, 0)
    return {
        'processed': g['processed'], 'fresh': g['fresh'], 'output': g['output'], 'calls': g['calls'],
        'agents': tok['workflow_agents'] + 1 + len(tok['others']), 'wf_agents': tok['workflow_agents'], 'others': len(tok['others']),
        'commits': d['commits']['total'],
        'tests': lead_int(gate5), 'tests_gate': gate5,
        'renders': lead_int(renders), 'renders_label': renders,
        'hours': (end - start).total_seconds() / 3600, 'as_of': tok['as_of'], 'first': tok['main']['first'],
        'words': words, 'messages': len(msgs) + d['prompts']['status_pings']['count'],
        'rails_lo': min(vs_rails) if vs_rails else None, 'rails_hi': max(vs_rails) if vs_rails else None,
        'findings': findings,
    }


# ---------------------------------------------------------------- beats

def details(pid, label='The details'):
    return '<a class="more" href="#%s">%s <span aria-hidden="true">&rarr;</span></a>' % (pid, e(label))


def beat(bid, kicker, visual, headline, lines, links, cls=''):
    return ('<section class="beat %s" id="b-%s" aria-labelledby="b-%s-h"><div class="wrap beat-grid">'
            '<div class="beat-visual reveal">%s</div>'
            '<div class="beat-copy reveal"><p class="kicker">%s</p><h2 id="b-%s-h">%s</h2>%s<p class="links">%s</p></div>'
            '</div></section>' % (cls, bid, bid, visual, kicker, bid, headline, ''.join(l if l.startswith('<div') else '<p class="line">%s</p>' % l for l in lines), links))


def counter(value, dec=0, unit='', prefix=''):
    shown = ('{:,.%df}' % dec).format(value)
    return ('<span class="num-big">%s<b class="count" data-to="%s" data-dec="%d">%s</b>%s</span>'
            % (('<i>%s</i>' % e(prefix)) if prefix else '', ('%.' + str(dec) + 'f') % value, dec, shown,
               ('<i>%s</i>' % e(unit)) if unit else ''))


def hero(d, cl, f):
    latest = cl.get('latest')
    if latest is None:
        live = 'Tuning against Rust has begun; the first measured runs will appear on the chart below.'
        head = 'Campfire, in F#.'
    else:
        if latest >= 1.005:
            head = 'F# just passed Rust.'
        elif latest >= 0.995:
            head = 'F# caught Rust.'
        else:
            head = 'F# is hunting Rust.'
        live = ('Basecamp&#39;s chat app, ported to F# so it can&#39;t be told apart from the Rails original or the Rust port. '
                'Latest run: <strong>%.2f&times;</strong> Rust&#39;s throughput, <strong>%+.1f%%</strong> since tuning began.' % (latest, cl['gain']))
    pts = cl.get('points') or []
    last_t = pts[-1]['time'].astimezone(EDT).strftime('%a %-d %b, %H:%M EDT') if pts else ''
    tiles = ''
    if latest is not None:
        tiles = ''.join('<div class="ht">%s<span>%s</span></div>' % (a, b) for a, b in [
            (counter(latest, 2, '×'), 'of Rust, latest run'),
            (counter(cl['gain'], 1, '%', '+' if cl['gain'] >= 0 else ''), 'since tuning began'),
            (counter(cl['changes'], 0, ''), 'code change%s to get here' % ('' if cl['changes'] == 1 else 's')),
            (counter(abs(cl['to_go']), 0, '%'), 'to go to match Rust' if cl['to_go'] > 0 else 'ahead of Rust')
            if abs(cl['to_go']) >= 0.5 else ('<span class="num-big"><b>Level</b></span>', 'with Rust, latest run'),
        ])
    return ('<section class="hero" id="top" aria-labelledby="hero-h"><div class="glow" aria-hidden="true"></div><div class="wrap">'
            '<p class="kicker hero-in">Basecamp&#39;s ONCE Campfire &middot; ported to F# &middot; Phase 7, live</p>'
            '<h1 id="hero-h" class="hero-in">%s</h1><p class="lede hero-in">%s</p>'
            '<div class="htiles hero-in">%s</div>'
            '<figure class="climb hero-in" id="climb" aria-labelledby="climb-cap">%s'
            '<figcaption id="climb-cap"><span><b>The climb.</b> F#&#39;s throughput as a share of Rust&#39;s, one dot per measured run '
            '(geometric mean of the five workloads, 16 connections). Hover, tap or tab to a dot for that run&#39;s numbers and what changed. '
            'Hollow grey dots were tried and reverted. Latest: %s.</span>%s</figcaption></figure>'
            '<p class="btw" id="btw"><a href="#p-muse">(btw, Muse sucks)</a> <span>How this started, and why none of its numbers are on the chart.</span></p>'
            '</div></section>' % (head, live, tiles, cl['chart'], e(last_t), details('p-climb', 'How to read it')))


def numbers(d, f):
    hrs = f['hours']
    items = [
        ('p-tokens', counter(f['processed'] / 1e9, 2, 'B'), 'tokens processed',
         '%s tokens read or written across %s API calls, summed from the transcripts.' % (n(f['processed']), n(f['calls']))),
        ('p-tokens', counter(f['fresh'] / 1e6, 1, 'M'), 'fresh tokens',
         '%s: uncached input, cache writes and output. Cache reads left out.' % n(f['fresh'])),
        ('p-tokens', counter(f['agents']), 'Claude agents',
         '%d in workflows, the orchestrator, and %d that built this page.' % (f['wf_agents'], f['others'])),
        ('p-routine', counter(f['commits']), 'commits',
         'On the port branch from Phase 0 to now, this page&#39;s own commits left out.'),
        ('p-phase5', counter(f['tests'] or 0), 'tests at the last gate', 'Phase 5 gate: %s.' % e(f['tests_gate'])),
        ('p-proof', counter(f['renders'] or 0), 'renders, byte for byte', '%s view renders identical to the Rust port&#39;s.' % e(f['renders_label'])),
        ('p-tokens', counter(hrs, 1), 'hours so far', 'From the hand-off (%s) to the last token counted (%s), EDT.' % (local(f['first']), local(f['as_of']))),
    ]
    tiles = ''.join('<a class="nt reveal" href="#%s" style="--i:%d">%s<span class="nt-l">%s</span><span class="tip" role="tooltip">%s</span></a>'
                    % (pid, i, big, e(label), t) for i, (pid, big, label, t) in enumerate(items))
    return ('<section class="numbers" id="numbers" aria-label="By the numbers"><div class="wrap">'
            '<p class="kicker reveal">By the numbers &middot; counted from the transcripts, git and the logs, not estimated</p>'
            '<div class="ntiles">%s</div></div></section>' % tiles)


def beat_port(d, f):
    b = d['chapters']['build']
    wfs = {w['key']: w for w in d['tokens']['workflows']}
    commits = d['commits']['by_phase_window']
    tests = [lead_int(p.get('gate')) or 0 for p in b['phases']]
    top = max(tests) or 1
    tiles = []
    for i, p in enumerate(b['phases']):
        wf = wfs.get(p['key'])
        t = tests[i]
        if wf:
            dur = duration(wf['first'], wf['last'])
            meta = '%s &middot; %d agents &middot; %s commits' % (e(dur), len(wf['agents']), commits.get(p['key'], 0))
        else:
            mm = re.search(r'(\d\d):(\d\d) to (\d\d):(\d\d)', p.get('when', ''))
            dur = '%d min' % ((int(mm.group(3)) * 60 + int(mm.group(4))) - (int(mm.group(1)) * 60 + int(mm.group(2)))) if mm else ''
            meta = 'orchestrator &middot; %s commit' % commits.get(p['key'], 0)
        big = ('<b class="count" data-to="%d" data-dec="0">%s</b><span>tests at the gate</span>' % (t, n(t))) if t else '<b>%s</b><span>to a green build</span>' % e(dur)
        tiles.append('<li class="ph" style="--i:%d;--h:%.3f"><a href="#p-%s"><span class="ph-n">%s</span><span class="ph-t">%s</span>'
                     '<span class="ph-bar" aria-hidden="true"><span></span></span><span class="ph-big">%s</span><span class="ph-m">%s</span></a></li>'
                     % (i, max(t / top, 0.04), e(p['key']), e(p['n']), e(p['title']), big, meta))
    m = re.search(r'about ([\d,]+) lines', d['chapters']['handover']['decision']['after'])
    rust_lines = m.group(1) if m else ''
    return beat('port', 'The call &middot; 5 Oct %s' % e(d['chapters']['handover']['decision']['time']),
                '<ol class="phases-strip">%s</ol>' % ''.join(tiles), 'So we ported the whole thing.',
                ['All %s lines of the Rust port, translated module by module in six gated phases: %s tests at the last gate, none failing.'
                 % (tip('~' + e(rust_lines), '177 routes, Action Cable, Web Push, bots and attachments; the Muse app was 713 lines.'), n(f['tests'] or 0))],
                details('p-decision', 'The call') + details('p-routine', 'The routine'), 'wide')


def beat_proof(d, f):
    visual = ('<div class="proof"><span class="num-big">%s</span><span class="of">of %s</span></div>'
              % ('<b class="count" data-to="%d" data-dec="0">%s</b>' % (f['renders'], n(f['renders'])), n(f['renders'])))
    return beat('proof', 'Checked against the originals', visual, 'Byte for byte.',
                ['Every view render identical to the Rust port&#39;s. Rich text, cookies, the HTTP kit and thumbnails matched too.'],
                details('p-proof'), 'flip')


def beat_process(d):
    pc = d['chapters']['tuning']['process_change']
    q = pc['quotes'][0]
    tiers = ''.join('<li style="--i:%d">%s</li>' % (i, tip('<b>%s</b>' % e(t[1]), 'Tier %s: %s. Decides: %s.' % (t[0], t[2], t[3].lower())))
                    for i, t in enumerate(pc['tiers']))
    return beat('process', 'A change of process &middot; %s' % e(q.get('time', pc['when'])), '<ol class="tiers">%s</ol>' % tiers,
                'Rust became a fixed target.',
                ['F# measured alone against stored Rust numbers, in loops of seconds, minutes and an hour. That&#39;s why the dots come fast.'],
                details('p-process'))


def more_row():
    links = [('p-climb', 'Every run'), ('p-baseline', 'The 5b baseline'), ('p-delivered', 'What&#39;s left'),
             ('p-prompts', 'Every prompt'), ('p-tokens', 'The token ledger'), ('p-muse', 'How it started')]
    return ('<section class="more-row" id="more" aria-label="Everything else"><div class="wrap reveal"><p class="kicker">Everything else</p>'
            '<p class="links">%s</p></div></section>' % ''.join('<a class="more" href="#%s">%s <span aria-hidden="true">&rarr;</span></a>' % (p, l) for p, l in links))


# ---------------------------------------------------------------- panels

def panel(pid, kicker, title, body, back):
    return ('<div class="panel" id="%s" role="dialog" aria-modal="true" aria-labelledby="%s-t">'
            '<a class="scrim" href="#%s" data-close tabindex="-1" aria-hidden="true"></a>'
            '<div class="sheet" tabindex="-1"><header class="sheet-head"><div><p class="kicker">%s</p><h2 id="%s-t">%s</h2></div>'
            '<a class="x" href="#%s" data-close aria-label="Close panel"><span aria-hidden="true">&times;</span></a></header>'
            '<div class="sheet-body">%s<p class="back"><a href="#%s" data-close>&larr; Back to the page</a></p></div></div></div>'
            % (pid, pid, back, kicker, pid, title, back, body, back))


def p_climb(cl):
    pts = cl.get('points') or []
    rows = []
    for p in pts:
        rows.append('<tr%s><th scope="row">%s</th><td>%s</td><td>%s</td><td class="num">%.2f&times;</td><td>%s</td></tr>'
                    % ('' if p['kept'] else ' class="dropped"', e(p['time'].astimezone(EDT).strftime('%-d %b %H:%M')), e(p['unit'] or ''),
                       e(p['change']), p['ratio'], 'kept' if p['kept'] else 'reverted'))
    tbl = ('<div class="tw"><table><caption>Every dot, oldest first</caption><thead><tr><th scope="col">EDT</th><th scope="col">Unit</th>'
           '<th scope="col">Change</th><th scope="col" class="num">&times; Rust</th><th scope="col">Kept</th></tr></thead><tbody>%s</tbody></table></div>' % ''.join(rows))
    body = ''.join([
        '<p class="lead">The line is the geometric mean of F#&#39;s throughput over Rust&#39;s across the five benchmarked workloads '
        '(room page, messages page, sidebar, search, post a message) at 16 connections. Dots are spaced evenly in the order they ran, '
        'not by the clock. A dot that measured only some workloads carries the rest from the last kept run; its card greys them.</p>',
        '<h3>Read with care</h3><ul class="caveats">'
        '<li><b>Most tuning dots are provisional.</b> Tier 2 runs are one rep of only the workloads a change touches, on a published build mounted '
        'into the existing container. The number that gets reported is tier 3: a rebuilt image, three reps, every workload.</li>'
        '<li><b>Rust is a stored reference.</b> From the Phase 7 start on, Rust&#39;s numbers are its medians from that run '
        '(<code>bench/results/phase7-start/</code>), not re-measured each time; Rust and Rails are re-run at the re-baseline that closes the phase.</li>'
        '<li><b>The first two dots are different methods.</b> The Phase 5 dot is a rough wrk check through Docker&#39;s published port; '
        'the 5b baseline is <code>bench/run</code> with Rust in the same run, before the longer warm-up the Phase 7 harness added.</li>'
        '<li><b>Rails is in the cards only.</b> Its numbers are the 5b baseline; at 1 to 2%% of Rust&#39;s throughput it would sit flat on the floor of the chart.</li>'
        '</ul>',
        '<p>The Muse-era ratios are off the chart on purpose: those pages were not the same as Rust&#39;s. <a href="#p-muse">(btw, Muse sucks) &rarr;</a></p>',
        tbl,
        '<p class="src">Source: <code>bench/results/phase7-log.jsonl</code>, <code>bench/progress/history.json</code>, drawn by <code>bench/progress/climb.py</code>.</p>',
    ])
    return panel('p-climb', 'The climb', 'How to read the chart', body, 'climb')


def p_muse(d, cl):
    """The whole Muse and hand-over story, in one panel: what was claimed, what we found, the M4 rebaseline."""
    c = d['chapters']['muse']
    ho = d['chapters']['handover']
    pre = (cl['hist'].get('prelude') or [{}])[0]
    rows = c['claimed_table']['rows']
    chips = []
    for i, (k, v) in enumerate((pre.get('ratios') or {}).items()):
        r = rows[i] if i < len(rows) else None
        t = ('Ren: F# %s req/s vs official Rust %s, on the Linux box.' % (r[1], r[2])) if r else k
        chips.append('<li>%s<small>%s</small></li>' % (tip('<s class="strike">%s</s>' % e(v), t, 'ratio'), e(k)))
    st = next((f['stat'] for f in ho['findings'] if f.get('stat')), None)
    cards = []
    for f in ho['findings']:
        stat = ''
        if f.get('stat'):
            s = f['stat']
            stat = ('<div class="versus"><div class="v bad"><b>%s</b><span>%s</span></div><div class="v good"><b>%s</b><span>%s</span></div></div>'
                    % (e(s['bad']), e(s['bad_note']), e(s['good']), e(s['good_note'])))
        cards.append('<article class="finding"><p class="tag">%s</p><h3>%s</h3><p>%s</p>%s<p class="evidence">%s</p></article>'
                     % (e(f['tag']), e(f['title']), e(f['body']), stat, inline(f['evidence'])))
    fs = ''.join('<div class="fact"><b>%s</b><span>%s</span><p>%s</p></div>' % (e(x['value']), e(x['label']), e(x['detail'])) for x in c['facts'])
    instr = ''.join('<li><time>%s</time><span>%s</span></li>' % (e(i['time']), inline(i['text'])) for i in c['instructions'])
    log = ''.join('<li><time>%s</time><span>%s</span></li>' % (e(i['time']), inline(i['text'])) for i in c['ren_log'])
    ct = c['claimed_table']
    rb = ho['rebaseline']
    h = c['handoff']
    body = ''.join([
        '<p class="lead">%s</p>' % e(c['standfirst']),
        '<div class="claim"><p class="claim-tag"><s class="strike">F# wins 5/5</s></p><ul class="struck">%s</ul>'
        '<p class="cap">Ren&#39;s final scoreboard, F# over Rust. None of it measured the same pages.</p></div>' % ''.join(chips),
        ('<div class="vs"><div class="vs-bad"><span class="num-big small"><s class="strike">%s</s></span><span>req/s for posting a message, with %s</span></div>'
         '<div class="vs-good"><span class="num-big small"><b>%s</b></span><span>once the post script kept its session cookie: %s</span></div></div>'
         % (e(st['bad'].replace(' req/s', '')), e(st['bad_note']), e(st['good'].replace(' req/s', '')), e(st['good_note']))) if st else '',
        '<h3>What we found on the M4</h3><p>%s</p>' % e(ho['standfirst']),
        ''.join(cards),
        table(rb['columns'], rb['rows'], rb['caption']), '<p class="src">Source: %s</p>' % e(rb['source']),
        '<h3>Why it is off the chart</h3>', cl['prelude'],
        '<h3>The overnight run</h3><div class="facts">%s</div>' % fs,
        '<div class="row"><span class="lbl">Ren&#39;s stack</span>%s</div>' % pills(c['stack']),
        '<h4>What Chris asked Ren for</h4><p class="muted">%s</p><ol class="log">%s</ol>' % (e(c['instructions_intro']), instr),
        '<h4>What Ren reported</h4><p class="muted">%s</p><ol class="log">%s</ol>' % (e(c['ren_log_intro']), log),
        table(ct['columns'], ct['rows'], ct['caption']),
        '<h3>The hand-off</h3><figure class="handoff"><figcaption>%s</figcaption><div class="bubble"><div class="bubble-head"><span class="avatar" aria-hidden="true">CK</span>'
        '<b>chris kluis</b><time>10:45 AM</time></div><div class="bubble-body">%s</div></div></figure>' % (e(h['intro']), para(h['text'])),
        '<p>Then Chris made the call: an identical-in-use port of the whole app. <a href="#p-decision">The call and the six decisions &rarr;</a></p>',
        '<div class="unknown"><span aria-hidden="true">?</span><p><b>Tokens in Muse:</b> %s</p></div>' % e(c['tokens_note']),
    ])
    return panel('p-muse', '(btw, Muse sucks)', 'How it started', body, 'btw')


def p_decision(d):
    c = d['chapters']['handover']
    dec = c['decision']
    ds = ''.join('<div class="decision"><b>%s</b><p class="q">%s</p><p>%s</p></div>' % (e(x['id']), e(x['q']), e(x['a'])) for x in c['decisions'])
    body = ''.join([
        '<p class="muted">%s</p><blockquote class="pull"><p>%s</p><footer>Chris, 5 Oct %s</footer></blockquote>' % (e(dec['intro']), e(dec['quote']), e(dec['time'])),
        '<p>%s</p>' % e(dec['after']),
        '<div class="decisions">%s</div>' % ds,
        '<h3>Chris&#39;s reply, 11:37</h3><pre class="reply">%s</pre>' % e(c['decision_reply']),
    ])
    return panel('p-decision', 'The call', 'Identical in use, then faster', body, 'b-port')


def findings_line(agents):
    parts = []
    for role, name in (('verify', 'Verifier'), ('reverify', 'Re-verify')):
        for a in agents:
            if a['role'] == role and 'findings' in a:
                f = a['findings']
                chips = ''.join('<span class="sev sev-%s">%d %s</span>' % (s, f[s], s) for s in ('critical', 'major', 'minor') if f.get(s))
                parts.append('<span class="fl"><b>%s</b>%s</span>' % (name, chips or '<span class="sev sev-none">none</span>'))
    return '<div class="findings-line">%s</div>' % ''.join(parts) if parts else ''


def p_phase(d, i, p):
    wfs = {w['key']: w for w in d['tokens']['workflows']}
    commits = d['commits']['by_phase_window']
    wf = wfs.get(p['key'])
    meta = []
    if wf:
        meta += [('Ran', '%s &ndash; %s' % (e(local(wf['first'])), e(local(wf['last'], '%H:%M')))),
                 ('Duration', e(duration(wf['first'], wf['last']))), ('Agents', len(wf['agents'])),
                 ('Processed', e(mega(wf['totals']['processed']))), ('Fresh', e(mega(wf['totals']['fresh'])))]
    else:
        meta += [('Ran', e(p.get('when', ''))), ('By', 'orchestrator')]
    meta.append(('Commits', commits.get(p['key'], 0)))
    dl = ''.join('<div><dt>%s</dt><dd>%s</dd></div>' % (k, v) for k, v in meta)
    units = ''.join('<li>%s</li>' % e(u) for u in p['units'])
    hl = ''.join('<li>%s</li>' % inline(h) for h in p['highlights'])
    gate = '<p class="gate"><span>Gate</span>%s</p>' % e(p['gate']) if p.get('gate') else ''
    phases = d['chapters']['build']['phases']
    nav = []
    if i > 0:
        nav.append('<a href="#p-%s">&larr; Phase %s</a>' % (e(phases[i - 1]['key']), e(phases[i - 1]['n'])))
    if i + 1 < len(phases):
        nav.append('<a href="#p-%s">Phase %s &rarr;</a>' % (e(phases[i + 1]['key']), e(phases[i + 1]['n'])))
    script = ''
    unit = next((u for u in d['prompts']['units'] if u['phase'] == p['key']), None)
    if unit:
        script = ('<h3>What the builders were told</h3><ul>%s</ul><p class="src">Script: <a href="site/workflows/%s">site/workflows/%s</a></p>'
                  % (''.join('<li>%s</li>' % inline(x) for x in unit['items']), e(unit['script']), e(unit['script'])))
    body = ''.join(['<dl class="pmeta">%s</dl>' % dl, findings_line(wf['agents']) if wf else '', gate,
                    '<h3>Units</h3><ul class="units">%s</ul>' % units, '<h3>What happened</h3><ul class="hl">%s</ul>' % hl,
                    pills(p['pills']), script, '<p class="pnav">%s</p>' % ' '.join(nav)])
    return panel('p-' + p['key'], 'Phase %s' % e(p['n']), e(p['title']), body, 'b-port')


def p_routine(d, f):
    b = d['chapters']['build']
    steps = ''.join('<li><span class="step-n">%d</span><b>%s</b><span class="who">%s &middot; %s</span><p>%s</p></li>'
                    % (i + 1, e(s['step']), e(s['who']), e(s['count']), e(s['what'])) for i, s in enumerate(b['routine']))
    commits = d['commits']['by_phase_window']
    titles = d['token_config']['phase_titles']
    crow = [[titles.get(k, {'phase0': 'Phase 0: Scaffold', 'after': 'After'}.get(k, k)), str(v)] for k, v in commits.items() if v]
    fd = f['findings']
    body = ''.join([
        '<p class="lead">%s</p>' % e(b['standfirst']),
        '<ol class="routine">%s</ol>' % steps,
        '<p>Verifier findings, Phases 1 to 5: <b>%d critical, %d major, %d minor</b>. The per-phase counts, and what the re-verifiers still found, are in each phase&#39;s panel.</p>' % (fd['critical'], fd['major'], fd['minor']),
        '<p class="pnav">%s</p>' % ' '.join('<a href="#p-%s">Phase %s</a>' % (e(p['key']), e(p['n'])) for p in b['phases']),
        table(['Phase window', 'Commits'], crow, 'Commits on the port branch, by the phase running when they were made (site/ excluded; %d in all)' % f['commits']),
    ])
    return panel('p-routine', 'Built for real', 'The routine, every phase', body, 'b-port')


def p_proof(d):
    b = d['chapters']['build']
    diffs = ''.join('<div class="diff"><b>%s</b><span>%s</span></div>' % (e(x['value']), e(x['label'])) for x in b['differentials'])
    inc = ''.join('<article class="incident"><h4>%s</h4><p>%s</p></article>' % (e(x['title']), e(x['body'])) for x in b['incidents'])
    body = ''.join([
        '<p class="lead">Each layer was compared with the port it was translated from, or with Rails itself, on generated inputs, not samples. '
        'The differential tools are in <code>bin/</code>: <code>views-differential</code>, <code>richtext-differential</code>, <code>kit-differential</code>, <code>db-differential</code>.</p>',
        '<div class="diffs">%s</div>' % diffs,
        '<h3>What went wrong along the way</h3><div class="incidents">%s</div>' % inc,
    ])
    return panel('p-proof', 'Checked against the originals', 'Byte for byte, and how we know', body, 'b-proof')


def p_baseline(d):
    c = d['chapters']['tuning']
    b = c['baseline']
    q = c['chris_quote']
    other = table(['Measure', 'Rails', 'Rust', 'F#'], [[o['label'], o['rails'], o['rust'], o['fsharp']] for o in b['other']], 'Beyond throughput (same run)')
    body = ''.join([
        '<blockquote class="pull"><p>%s</p><footer>Chris, 5 Oct %s, during Phase 3</footer></blockquote>' % (e(q['text']), e(q['time'])),
        '<p class="lead">%s</p>' % e(b['summary']),
        table(b['columns'], b['rows'], 'Throughput, %s. The bar is F# as a share of Rust; the tick marks parity.' % b['unit'], ratio_col=b.get('ratio_col')),
        '<p class="src">%s Source: <code>%s</code>.</p>' % (e(b['method']), e(b['source'])),
        other,
        '<div class="note"><b>The skeptical review.</b> %s</div>' % e(b['note']) if b.get('note') else '',
    ])
    return panel('p-baseline', 'Phase 5b', 'The whole-app baseline', body, 'more')


def p_process(d):
    pc = d['chapters']['tuning']['process_change']
    quotes = ''.join('<blockquote class="pull"><p>%s</p><footer>Chris, %s</footer></blockquote>' % (e(q['text']), e(q.get('time', pc['when']))) for q in pc['quotes'])
    tiers = table(['Tier', 'Loop time', 'What runs', 'What it decides'], pc['tiers'], 'Iteration tiers from here on')
    rules = ''
    path = os.path.join(SITE, 'workflows', 'campfire-fs-phase7b.js')
    if os.path.exists(path):
        m = re.search(r'const RULES = `(.*?)`', open(path, encoding='utf-8').read(), re.S)
        if m:
            rules = ('<h3>As the relaunched Phase 7 workflow tells every agent</h3><pre class="code">%s</pre>'
                     '<p class="src">From <a href="site/workflows/campfire-fs-phase7b.js">site/workflows/campfire-fs-phase7b.js</a>; '
                     'the first launch, before the change, is <a href="site/workflows/campfire-fs-phase7.js">campfire-fs-phase7.js</a>.</p>' % e(m.group(1).strip()))
    body = ''.join([quotes, '<p class="lead">%s</p>' % e(pc['summary']), tiers, rules])
    return panel('p-process', 'A change of process', e(pc['title']), body, 'b-process')


def p_delivered(d):
    t = d['chapters']['tuning']
    dl = d['chapters']['delivered']
    pend = ''.join('<article class="pcard"><div class="ptop"><span class="pn">%s</span><h3>%s</h3>%s</div><p>%s</p>%s</article>'
                   % (e(p['n']), e(p['title']), status_badge(p['status']), e(p['target']),
                      '<p class="result">%s</p>' % e(p['result']) if p.get('result') else '')
                   for p in t['pending'])
    steps = ''.join('<article class="pcard">%s<h3>%s</h3><p>%s</p>%s</article>'
                    % (status_badge(s['status']), e(s['title']), e(s['detail']), '<a href="%s">%s</a>' % (e(s['link']), e(s['link'])) if s.get('link') else '')
                    for s in dl['steps'])
    body = '<p class="lead">%s</p><h3>Still running</h3><div class="pgrid">%s</div><h3>Then</h3><div class="pgrid">%s</div>' % (e(dl['standfirst']), pend, steps)
    return panel('p-delivered', 'Delivered, not yet', 'What&#39;s left', body, 'more')


def p_prompts(d, f):
    p = d['prompts']
    msgs = []
    for m in p['chris']:
        if m.get('kind') == 'attachment':
            msgs.append('<li class="msg attach"><time>%s</time><p>%s</p></li>' % (e(m['time']), e(m['text'])))
        else:
            msgs.append('<li class="msg"><time>%s</time><p>%s</p></li>' % (e(m['time']), e(m['text']).replace('\n', '<br>')))
    roles = ''.join('<div class="prole"><p class="kicker">%s</p><pre>%s</pre></div>' % (e(r['role']), e(r['text'])) for r in p['roles'])
    scripts = sorted(x for x in os.listdir(os.path.join(SITE, 'workflows')) if x.endswith('.js'))
    body = ''.join([
        '<div class="pstats"><div><b>%d</b><span>messages from Chris</span></div><div><b>%s</b><span>%s</span></div><div><b>%d</b><span>workflow scripts</span></div></div>'
        % (f['messages'], n(f['words']), e(p['word_count_note']), len(scripts)),
        '<p class="muted">%s</p>' % e(p['intro']),
        '<ol class="chat">%s</ol>' % ''.join(msgs),
        '<p class="muted">%s</p>' % e(p['status_pings']['note']),
        '<h3>The routine, as code</h3><p>%s</p>' % e(p['routine_intro']),
        '<div class="prole common"><p class="kicker">%s</p><pre>%s</pre></div>' % (e(p['common_label']), e(p['common'])),
        roles,
        '<h3>The workflow scripts</h3><ul class="scripts">%s</ul>' % ''.join('<li><a href="site/workflows/%s">%s</a></li>' % (e(s), e(s)) for s in scripts),
    ])
    return panel('p-prompts', 'The prompts', 'What was actually said', body, 'more')


def p_tokens(d):
    tok = d['tokens']
    wfs, main, others, g = tok['workflows'], tok['main'], tok['others'], tok['grand']
    rows = [(w['title'].replace(': ', ' · ', 1), '%d agent%s' % (len(w['agents']), '' if len(w['agents']) == 1 else 's'), {'v': w['totals']['processed']}) for w in wfs]
    rows.append(('Orchestrator session', '%s calls' % n(main['totals']['calls']), {'v': main['totals']['processed']}))
    for o in others:
        rows.append(('Page agent', o['label'][:32], {'v': o['totals']['processed']}))
    chart1 = bar_chart(rows, 'Tokens processed per phase', 'Horizontal bars: total tokens processed by each workflow, the orchestrating session and the page agents.', 'chart-processed')
    series = [('cache_write', 'Cache writes', '--s1'), ('output', 'Output', '--s2'), ('input', 'Uncached input', '--s3')]
    rows2 = [(w['title'].replace(': ', ' · ', 1), '', {k: w['totals'][k] for k, _, _ in series}) for w in wfs]
    rows2.append(('Orchestrator session', '', {k: main['totals'][k] for k, _, _ in series}))
    chart2 = bar_chart(rows2, 'Fresh tokens per phase', 'Stacked bars: cache writes, output and uncached input by workflow and the orchestrating session.', 'chart-fresh', series)
    rec = []
    for w in wfs:
        h = w.get('harness')
        delta = (h['tokens'] - w['final_context_sum']) if h else None
        rec.append([w['title'], str(len(w['agents'])), duration(w['first'], w['last']), n(h['tokens']) if h else '–', n(w['final_context_sum']),
                    ('%+d' % delta) if delta is not None else '–', n(w['totals']['fresh']), n(w['totals']['processed'])])
    role_names = {'build': 'Builders', 'verify': 'Verifiers', 'fix': 'Fixers', 'reverify': 'Re-verifiers', 'bench': 'Bench', 'review': 'Reviewers',
                  'harness': 'Harness (7.0)', 'tune': 'Tuner'}
    cols = ['', 'Agents', 'API calls', 'Processed', 'Fresh', 'Output']
    role_rows = [[role_names.get(k, k), str(v['agents']), n(v['totals']['calls']), mega(v['totals']['processed']), mega(v['totals']['fresh']), n(v['totals']['output'])]
                 for k, v in tok['by_role'].items()]
    model_rows = [[k, str(v['agents']) + (' + orchestrator' if k in main['models'] else ''), n(v['totals']['calls']), mega(v['totals']['processed']), mega(v['totals']['fresh']), n(v['totals']['output'])]
                  for k, v in tok['by_model'].items()]
    ag_rows = [[w['title'].split(':')[0], a['label'], a['model'], duration(a['first'], a['last']), n(a['totals']['calls']),
                n(a['totals']['processed']), n(a['totals']['fresh']), n(a['final_context'])] for w in wfs for a in w['agents']]
    win_rows = [[w['label'], local(w['from']), n(w['totals']['calls']), n(w['totals']['processed']), n(w['totals']['fresh'])] for w in main['windows']]
    share = 100.0 * g['cache_read'] / g['processed'] if g['processed'] else 0
    body = ''.join([
        '<p class="lead">Summed from the transcripts Claude Code wrote, once per API call, for the orchestrating session, all %d workflow agents and the page agents. '
        'Nothing here is estimated. As of %s EDT.</p>' % (tok['workflow_agents'], e(local(tok['as_of']))),
        '<div class="bign"><div><b>%s</b><span>tokens processed</span><p>%s. Cache reads are %.1f%% of it: each turn re-reads the agent&#39;s context from the prompt cache.</p></div>'
        '<div><b>%s</b><span>fresh tokens</span><p>%s: uncached input, cache writes and output. What was new to the model on each call.</p></div>'
        '<div><b>%s</b><span>written by models</span><p>Output across %s API calls: code, tests, reports, commit messages.</p></div></div>'
        % (e(mega(g['processed'], 2)), n(g['processed']), share, e(mega(g['fresh'])), n(g['fresh']), e(mega(g['output'])), n(g['calls'])),
        '<dl class="measures"><div><dt>Processed</dt><dd>input + cache writes + cache reads + output, over every API call. The fullest measure of work done.</dd></div>'
        '<div><dt>Fresh</dt><dd>the same without cache reads, which are cheap and repetitive. Closer to new work.</dd></div>'
        '<div><dt>Final context</dt><dd>each agent&#39;s context on its last call. The workflow harness&#39;s per-phase figure is this, summed; it lands within a few dozen tokens of ours. It measures how big the contexts grew, not how much was processed.</dd></div></dl>',
        '<figure class="fig"><figcaption><b>Tokens processed</b><span>Linear scale. The orchestrator ran the whole time.</span></figcaption>%s</figure>' % chart1,
        '<figure class="fig"><figcaption><b>Fresh tokens</b><span>Cache reads left out, so the new work shows.</span></figcaption>%s</figure>' % chart2,
        table(['Workflow', 'Agents', 'Ran', 'Harness figure', 'Final contexts', 'Gap', 'Fresh', 'Processed'], rec, 'Reconciliation with the workflow harness (gap = harness minus our sum of final contexts)', cls='dense'),
        table(cols, role_rows, 'By role (workflow agents)'),
        table(cols, model_rows, 'By model (everything, the orchestrator and page agents included)'),
        table(['Orchestrator window', 'From', 'Calls', 'Processed', 'Fresh'], win_rows, 'The orchestrating session, split where its job changed', num_from=2),
        '<details><summary>All %d workflow agents</summary>%s</details>' % (len(ag_rows), table(['Phase', 'Agent', 'Model', 'Ran', 'Calls', 'Processed', 'Fresh', 'Final context'], ag_rows, num_from=4, cls='dense')),
        '<div class="unknown"><span aria-hidden="true">?</span><p><b>Not counted:</b> Ren&#39;s run in Muse. Its token use isn&#39;t in the hand-off and no transcript was shared, so it is unknown, not zero.</p></div>',
        '<p class="src">Method: <code>site/collect_tokens.py</code> reads the session transcript and every <code>agent-*.jsonl</code> under its <code>subagents/</code> folder, '
        'keeps the last line of each API message id (Claude Code repeats a message&#39;s usage on each content block), and maps workflows to phases by their script names. Rule: %s.</p>' % e(tok['rule']),
    ])
    return panel('p-tokens', 'Every token, counted', 'The token ledger', body, 'numbers')


# ---------------------------------------------------------------- page

CSS = r"""
:root{
  --bg:#f7f4ee;--surface:#fffdf9;--surface-2:#efe9df;--ink:#16130f;--ink-2:#4b453d;--muted:#7a7368;
  --line:rgba(22,19,15,.13);--line-2:rgba(22,19,15,.07);--accent:#c2410c;--accent-2:#6d4aff;--crit:#c0262d;--good:#0b7a35;--warn:#a15c00;--minor:#6b665d;
  --s1:#2a78d6;--s2:#eb6834;--s3:#1baf7a;
  --glow-a:rgba(234,88,12,.22);--glow-b:rgba(109,74,255,.15);--shadow:0 1px 2px rgba(0,0,0,.04),0 18px 40px -18px rgba(30,20,10,.18);
  --display:"Instrument Serif",ui-serif,Georgia,serif;--sans:system-ui,-apple-system,"SF Pro Text","Segoe UI",Roboto,sans-serif;--mono:ui-monospace,"SF Mono",Menlo,Consolas,monospace;
  color-scheme:light}
@media (prefers-color-scheme:dark){:root:not([data-theme="light"]){
  --bg:#0c0a09;--surface:#161311;--surface-2:#1f1b18;--ink:#f6f1ea;--ink-2:#cbc3b7;--muted:#91897d;
  --line:rgba(255,255,255,.12);--line-2:rgba(255,255,255,.06);--accent:#ff8a4c;--accent-2:#a594ff;--crit:#ff6b6b;--good:#34c46a;--warn:#e8a33a;--minor:#a39d92;
  --s1:#3987e5;--s2:#d95926;--s3:#199e70;--glow-a:rgba(255,122,61,.22);--glow-b:rgba(140,110,255,.2);--shadow:0 1px 2px rgba(0,0,0,.4),0 18px 44px -18px rgba(0,0,0,.7);color-scheme:dark}}
:root[data-theme="dark"]{
  --bg:#0c0a09;--surface:#161311;--surface-2:#1f1b18;--ink:#f6f1ea;--ink-2:#cbc3b7;--muted:#91897d;
  --line:rgba(255,255,255,.12);--line-2:rgba(255,255,255,.06);--accent:#ff8a4c;--accent-2:#a594ff;--crit:#ff6b6b;--good:#34c46a;--warn:#e8a33a;--minor:#a39d92;
  --s1:#3987e5;--s2:#d95926;--s3:#199e70;--glow-a:rgba(255,122,61,.22);--glow-b:rgba(140,110,255,.2);--shadow:0 1px 2px rgba(0,0,0,.4),0 18px 44px -18px rgba(0,0,0,.7);color-scheme:dark}
*{box-sizing:border-box}
html{scroll-behavior:smooth;-webkit-text-size-adjust:100%;overflow-x:clip}
body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.55 var(--sans);overflow-x:clip;-webkit-font-smoothing:antialiased}
html.locked,html.locked body{overflow:hidden}
a{color:var(--accent);text-underline-offset:3px}
:focus-visible{outline:2px solid var(--accent);outline-offset:3px;border-radius:4px}
code{overflow-wrap:anywhere;font:.88em var(--mono);background:var(--surface-2);padding:.1em .35em;border-radius:5px}
.wrap{max-width:1200px;margin:0 auto;padding:0 16px}
@media (min-width:720px){.wrap{padding:0 32px}}
.skip{position:absolute;left:-999px}.skip:focus{left:16px;top:8px;z-index:60;background:var(--surface);padding:8px 12px;border-radius:8px}
.kicker{font-size:12px;letter-spacing:.14em;text-transform:uppercase;color:var(--accent);font-weight:650;margin:0 0 12px}
/* top bar */
.bar{position:sticky;top:0;z-index:20;backdrop-filter:saturate(1.6) blur(18px);-webkit-backdrop-filter:saturate(1.6) blur(18px);background:color-mix(in srgb,var(--bg) 80%,transparent);border-bottom:1px solid var(--line-2)}
.bar .wrap{display:flex;align-items:center;gap:12px;height:52px}
.brand{font-weight:650;letter-spacing:-.01em;color:var(--ink);text-decoration:none;white-space:nowrap}.brand span{color:var(--accent)}
.bar nav{flex:1;min-width:0;overflow-x:auto;scrollbar-width:none}.bar nav::-webkit-scrollbar{display:none}
.bar nav ol{display:flex;gap:2px;list-style:none;margin:0;padding:0;justify-content:flex-end}
.bar nav a{display:block;padding:6px 10px;border-radius:999px;color:var(--ink-2);text-decoration:none;font-size:13px;white-space:nowrap}
.bar nav a:hover,.bar nav a:focus-visible{background:var(--surface-2);color:var(--ink)}
.theme{border:1px solid var(--line);background:var(--surface);color:var(--ink);border-radius:999px;width:34px;height:34px;cursor:pointer;flex:none;display:none}
.js .theme{display:inline-grid;place-items:center}
/* hero */
.hero{position:relative;z-index:2;padding:56px 0 24px;isolation:isolate}
.glow{position:absolute;inset:-140px 0 auto;height:700px;z-index:-1;pointer-events:none;
  background:radial-gradient(38% 46% at 22% 35%,var(--glow-a),transparent 70%),radial-gradient(34% 44% at 78% 22%,var(--glow-b),transparent 70%);filter:blur(12px)}
h1{font:400 clamp(52px,10.5vw,138px)/.9 var(--display);letter-spacing:-.025em;margin:0 0 22px;text-wrap:balance}
.lede{font-size:clamp(17px,2.1vw,22px);line-height:1.5;color:var(--ink-2);max-width:780px;margin:0 0 28px}
.lede strong{color:var(--ink)}
.htiles{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:12px;margin:0 0 18px}
.ht{background:var(--surface);border:1px solid var(--line);border-radius:18px;padding:16px 18px;box-shadow:var(--shadow);min-width:0}
.ht .num-big{font-size:clamp(34px,4.6vw,54px)}.ht>span:last-child{display:block;color:var(--muted);font-size:13.5px;margin-top:4px}
@media (max-width:720px){.htiles{grid-template-columns:repeat(2,minmax(0,1fr));gap:10px}}
.climb{margin:0;position:relative;z-index:3}
.climb figcaption{display:flex;flex-wrap:wrap;gap:8px 20px;align-items:baseline;justify-content:space-between;color:var(--muted);font-size:13.5px;margin-top:12px}
.climb figcaption>span{flex:1 1 420px;min-width:0}.climb figcaption b{color:var(--ink)}
.cc{--cc-card:var(--surface);--cc-ink:var(--ink);--cc-mute:var(--muted);--cc-line:var(--line);--cc-acc:var(--accent);font-family:var(--sans)}
.cc-chart{border-radius:22px;box-shadow:var(--shadow)}
/* big numbers */
.num-big{display:inline-flex;align-items:baseline;font:400 clamp(60px,12vw,168px)/.9 var(--display);letter-spacing:-.03em;font-variant-numeric:tabular-nums;white-space:nowrap}
.num-big i{font-style:normal;font-size:.5em;margin-left:.04em;color:var(--accent)}
.num-big i:first-child{margin:0 .03em 0 0}
.num-big.small{font-size:clamp(44px,7vw,96px)}
s.strike{text-decoration:none;position:relative;display:inline-block}
s.strike::after{content:"";position:absolute;left:-4%;right:-4%;top:47%;height:.07em;min-height:2px;background:var(--crit);border-radius:2px;transform-origin:left;transition:transform .7s cubic-bezier(.6,0,.2,1) .35s}
/* numbers band */
.numbers{padding:72px 0 24px}
.sec-head h2,.beat h2{font:400 clamp(38px,6.4vw,80px)/.98 var(--display);letter-spacing:-.02em;margin:0 0 16px;text-wrap:balance}
.line{font-size:clamp(17px,1.8vw,20px);line-height:1.55;color:var(--ink-2);margin:0 0 14px;max-width:640px}
.ntiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:10px;margin:28px 0 8px}
.nt{position:relative;display:flex;flex-direction:column;justify-content:space-between;gap:10px;min-height:150px;padding:18px;border-radius:20px;background:var(--surface);border:1px solid var(--line);
  color:var(--ink);text-decoration:none;box-shadow:var(--shadow);transition:transform .25s,border-color .25s}
.nt:hover,.nt:focus-visible{transform:translateY(-3px);border-color:color-mix(in srgb,var(--accent) 50%,var(--line))}
.nt .num-big{font-size:clamp(40px,5vw,60px)}.nt-l{font-size:13.5px;font-weight:600;color:var(--ink-2)}
@media (min-width:1100px){.ntiles{grid-template-columns:repeat(7,1fr)}.nt:first-child{grid-column:span 1}}
/* tips (CSS only): a card above the fact; a sheet at the bottom of a phone screen */
.has-tip{position:relative;cursor:help;border-bottom:1px dashed color-mix(in srgb,currentColor 45%,transparent);outline-offset:2px}
.tip{position:absolute;z-index:30;left:50%;bottom:calc(100% + 10px);transform:translate(-50%,4px);width:max-content;max-width:min(300px,80vw);
  background:var(--ink);color:var(--bg);font:500 13px/1.45 var(--sans);letter-spacing:0;text-transform:none;text-align:left;padding:9px 12px;border-radius:10px;
  box-shadow:0 10px 30px rgba(0,0,0,.25);opacity:0;visibility:hidden;pointer-events:none;transition:opacity .15s,transform .15s;white-space:normal}
.has-tip:hover>.tip,.has-tip:focus>.tip,.nt:hover>.tip,.nt:focus-visible>.tip{opacity:1;visibility:visible;transform:translate(-50%,0)}
.nt>.tip{bottom:auto;top:calc(100% + 8px)}
@media (max-width:640px),(hover:none){.tip{position:fixed;left:12px;right:12px;bottom:12px;top:auto!important;width:auto;max-width:none;transform:none!important;font-size:14px;padding:12px 14px}}
/* beats */
.beat{padding:clamp(56px,8vw,104px) 0;border-top:1px solid var(--line-2);scroll-margin-top:52px;position:relative}
.beat-grid{display:grid;gap:clamp(28px,5vw,72px);align-items:center;grid-template-columns:minmax(0,1fr)}
@media (min-width:900px){.beat-grid{grid-template-columns:minmax(0,1.1fr) minmax(0,1fr)}.beat.flip .beat-visual{order:2}.beat.wide .beat-grid{grid-template-columns:minmax(0,1fr)}}
.beat-visual,.beat-copy{min-width:0}
.beat.wide .beat-visual{order:2}
.beat q{font:400 clamp(22px,2.6vw,30px)/1.25 var(--display);color:var(--ink);quotes:"\201C" "\201D"}
.who{display:block;margin-top:6px;font-size:14px;color:var(--muted)}
.links{display:flex;flex-wrap:wrap;gap:10px;margin:22px 0 0}
.more{display:inline-flex;align-items:center;gap:8px;padding:10px 16px;border-radius:999px;border:1px solid var(--line);background:var(--surface);color:var(--ink);
  font-weight:600;font-size:14.5px;text-decoration:none;box-shadow:var(--shadow);transition:transform .2s,border-color .2s,background .2s}
.more:hover,.more:focus-visible{transform:translateY(-2px);border-color:var(--accent)}
.more span{color:var(--accent);transition:transform .2s}.more:hover span{transform:translateX(3px)}
.cap{display:block;color:var(--muted);font-size:14px;margin-top:10px}
/* the struck claims (Muse panel) */
.claim-tag{font:400 clamp(30px,4vw,48px)/1 var(--display);margin:0 0 18px;color:var(--ink-2)}
.struck{list-style:none;margin:0;padding:0;display:flex;flex-wrap:wrap;gap:6px 26px}
.struck li{display:flex;flex-direction:column}
.struck .has-tip{border:0}
.struck s{font:400 clamp(48px,8vw,104px)/1 var(--display);color:var(--ink);letter-spacing:-.02em}
.struck small{font-size:12.5px;color:var(--muted);margin-top:4px}
.struck li:nth-child(2) s::after{transition-delay:.45s}.struck li:nth-child(3) s::after{transition-delay:.55s}.struck li:nth-child(4) s::after{transition-delay:.65s}.struck li:nth-child(5) s::after{transition-delay:.75s}
/* before and after (Muse panel) */
.vs{display:grid;gap:18px}
.vs>div>span:last-child{display:block;color:var(--muted);font-size:14px;margin-top:6px}
.vs-bad .num-big{color:var(--muted)}
.vs-good .num-big b{background:linear-gradient(160deg,var(--good),color-mix(in srgb,var(--good) 55%,var(--accent-2)));-webkit-background-clip:text;background-clip:text;color:transparent}
.tags ul,.chips ul,.loop ol{list-style:none;margin:8px 0 0;padding:0;display:flex;flex-wrap:wrap;gap:8px}
.tags li .has-tip,.chips li,.loop li .has-tip{display:inline-block;border:1px solid var(--line);background:var(--surface);border-radius:999px;padding:5px 12px;font-size:14px;color:var(--ink)}
.tags li .has-tip{border-color:color-mix(in srgb,var(--crit) 45%,var(--line));color:var(--crit)}
.tags,.loop,.chips{margin:0 0 16px}.loop ol{counter-reset:l}.loop li{display:flex;align-items:center;gap:8px}.loop li:not(:last-child)::after{content:"\2192";color:var(--accent);font-weight:700}
.chips b{font-variant-numeric:tabular-nums}
/* the phases strip */
.phases-strip{list-style:none;margin:0;padding:0;display:grid;gap:10px;grid-template-columns:repeat(6,minmax(0,1fr))}
@media (max-width:899px){.phases-strip{grid-template-columns:repeat(3,minmax(0,1fr))}}
@media (max-width:520px){.phases-strip{gap:8px}.ph a{padding:12px;border-radius:16px}.ph-n{font-size:34px}.ph-t{font-size:13px}.ph-m{display:none!important}.ph-big b{font-size:26px}}
.ph a{display:flex;flex-direction:column;gap:6px;height:100%;padding:16px;border-radius:20px;background:var(--surface);border:1px solid var(--line);color:var(--ink);text-decoration:none;box-shadow:var(--shadow);transition:transform .25s,border-color .25s}
.ph a:hover,.ph a:focus-visible{transform:translateY(-4px);border-color:var(--accent)}
.ph-n{font:400 44px/.85 var(--display);background:linear-gradient(160deg,var(--accent),var(--accent-2));-webkit-background-clip:text;background-clip:text;color:transparent}
.ph-t{font-weight:650;font-size:15px;line-height:1.25;min-height:2.5em}
.ph-bar{display:block;height:84px;border-radius:10px;background:var(--surface-2);position:relative;overflow:hidden}
.ph-bar span{position:absolute;left:0;right:0;bottom:0;height:calc(var(--h)*100%);background:linear-gradient(0deg,var(--accent),var(--accent-2));border-radius:10px;transform-origin:bottom;transition:transform 1.1s cubic-bezier(.2,.7,.2,1) calc(var(--i)*90ms)}
.ph-big b{display:block;font:400 clamp(30px,3.2vw,40px)/1 var(--display);font-variant-numeric:tabular-nums}.ph-big span,.ph-m{display:block;font-size:12.5px;color:var(--muted)}
.beat-build .beat-copy{max-width:820px}
/* byte for byte */
.proof .of{display:block;font:400 clamp(24px,3vw,36px)/1 var(--display);color:var(--muted);margin-top:6px}
.proof .num-big b{background:linear-gradient(160deg,var(--accent),var(--accent-2));-webkit-background-clip:text;background-clip:text;color:transparent}
/* tiers: three big words, details on hover */
.tiers{list-style:none;margin:0;padding:0;display:flex;flex-wrap:wrap;gap:6px 0;align-items:baseline}
.tiers li{display:flex;align-items:baseline}
.tiers li:not(:last-child)::after{content:"\2192";font:400 clamp(28px,4vw,48px)/1 var(--display);color:var(--muted);margin:0 .35em}
.tiers .has-tip{border:0}
.tiers b{font:400 clamp(44px,7vw,96px)/1 var(--display);letter-spacing:-.02em;color:var(--ink)}
.tiers li:last-child b{background:linear-gradient(160deg,var(--accent),var(--accent-2));-webkit-background-clip:text;background-clip:text;color:transparent}
@media (max-width:720px){.bar nav ol{justify-content:flex-start}.nt{min-height:112px}.ph-bar{height:48px}.ph-t{min-height:0}}
.btw{margin:18px 0 0;font-size:15px;color:var(--muted)}
.btw a{font-weight:650;text-decoration-thickness:1px}
.btw span{margin-left:4px}
.numbers{padding:48px 0 8px}.numbers .ntiles{margin-top:14px}
.more-row{padding:40px 0 56px;border-top:1px solid var(--line-2)}.more-row .links{margin-top:0}
.sheet-body h4{margin:22px 0 8px;font-size:16px}
.sheet .vs{margin:22px 0}
/* footer */
footer.site{border-top:1px solid var(--line-2);padding:40px 0 64px;color:var(--muted);font-size:14px}
footer.site p{max-width:860px}
/* panels: :target drawers without JS, .open with it */
.panel{position:fixed;inset:0;z-index:50;visibility:hidden;pointer-events:none}
html:not(.js) .panel:target,.panel.open{visibility:visible;pointer-events:auto}
.scrim{position:absolute;inset:0;background:rgba(10,8,6,.45);opacity:0;transition:opacity .3s;cursor:default}
html:not(.js) .panel:target .scrim,.panel.open .scrim{opacity:1}
.sheet{position:absolute;top:0;right:0;bottom:0;width:min(780px,100%);background:var(--bg);color:var(--ink);box-shadow:-24px 0 60px rgba(0,0,0,.25);
  overflow-y:auto;overscroll-behavior:contain;transform:translateX(102%);transition:transform .38s cubic-bezier(.2,.8,.2,1);outline:none}
html:not(.js) .panel:target .sheet,.panel.open .sheet{transform:none}
.sheet-head{position:sticky;top:0;z-index:2;display:flex;gap:16px;align-items:flex-start;justify-content:space-between;padding:22px 24px 16px;background:color-mix(in srgb,var(--bg) 88%,transparent);backdrop-filter:blur(14px);-webkit-backdrop-filter:blur(14px);border-bottom:1px solid var(--line-2)}
.sheet-head h2{font:400 clamp(30px,4.4vw,46px)/1 var(--display);margin:0;letter-spacing:-.015em}
.sheet-head .kicker{margin-bottom:8px}
.x{flex:none;display:grid;place-items:center;width:40px;height:40px;border-radius:50%;border:1px solid var(--line);background:var(--surface);color:var(--ink);text-decoration:none;font-size:24px;line-height:1}
.x:hover{border-color:var(--accent)}
.sheet-body{padding:8px 24px 48px;font-size:15.5px}
.sheet-body h3{font:400 28px/1.1 var(--display);margin:34px 0 12px}
.sheet-body .lead{font-size:17.5px;color:var(--ink-2)}
.back{margin-top:36px}
@media (max-width:720px){.sheet{width:100%;box-shadow:none}.sheet-head{padding:16px 16px 12px}.sheet-body{padding:4px 16px 40px}}
.muted{color:var(--muted)}.src{font-size:13px;color:var(--muted)}
.pills{display:flex;flex-wrap:wrap;gap:6px;list-style:none;margin:12px 0;padding:0}
.pills li{font-size:12.5px;padding:4px 10px;border-radius:999px;border:1px solid var(--line);background:var(--surface);color:var(--ink-2)}
.row{display:flex;flex-wrap:wrap;gap:10px;align-items:center;margin:18px 0}.lbl{font-size:12px;text-transform:uppercase;letter-spacing:.1em;color:var(--muted)}
.tw{overflow-x:auto;margin:18px 0;border:1px solid var(--line);border-radius:16px;background:var(--surface)}
.tw table{border-collapse:collapse;width:100%;font-size:14px}
.tw caption{text-align:left;padding:14px 16px 4px;font-size:13px;color:var(--muted);caption-side:top}
.tw th,.tw td{padding:9px 14px;border-bottom:1px solid var(--line-2);text-align:left;vertical-align:top}
.tw thead th{font-size:11.5px;text-transform:uppercase;letter-spacing:.06em;color:var(--muted);font-weight:600;white-space:nowrap}
.tw tbody th{font-weight:600}.tw tbody tr:last-child>*{border-bottom:0}.tw tr.dropped{color:var(--muted)}
.tw .num{text-align:right;font-variant-numeric:tabular-nums;white-space:nowrap}
.tw.dense th,.tw.dense td{padding:6px 10px;font-size:13px}
.ratio{min-width:160px}.rbar{display:inline-block;position:relative;width:90px;height:8px;border-radius:4px;background:var(--surface-2);vertical-align:middle;margin-right:10px}
.rfill{position:absolute;left:0;top:0;bottom:0;border-radius:4px;background:var(--s1)}.rfill.ahead{background:var(--good)}
.rone{position:absolute;top:-3px;bottom:-3px;width:2px;background:var(--ink);opacity:.55}
.facts{display:grid;gap:10px;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));margin:18px 0}
.fact{background:var(--surface);border:1px solid var(--line);border-radius:16px;padding:16px}
.fact b{font:400 38px/1 var(--display)}.fact span{display:block;font-weight:600;margin:6px 0}.fact p{margin:0;color:var(--ink-2);font-size:13.5px}
.log{list-style:none;margin:0;padding:0;display:grid;gap:8px}
.log li{display:grid;grid-template-columns:52px minmax(0,1fr);gap:10px;font-size:14.5px;overflow-wrap:anywhere}
.log time{font:12.5px var(--mono);color:var(--accent);padding-top:2px}
.handoff{margin:0}.handoff figcaption{color:var(--muted);font-size:14px;margin-bottom:10px}
.bubble{background:#1a1d21;color:#e8e8e8;border-radius:18px;padding:16px 18px;font-size:15px}
.bubble-head{display:flex;align-items:center;gap:10px;margin-bottom:6px}.bubble-head b{color:#fff}.bubble-head time{color:#9aa0a6;font-size:12.5px}
.avatar{width:32px;height:32px;border-radius:8px;display:grid;place-items:center;background:linear-gradient(135deg,#c2410c,#6d4aff);color:#fff;font-size:12px;font-weight:700}
.bubble-body p{margin:.5em 0;overflow-wrap:anywhere}
.unknown{display:flex;gap:16px;align-items:center;margin:24px 0;padding:16px 18px;border:1px dashed var(--line);border-radius:16px}
.unknown>span{font:400 64px/1 var(--display);color:var(--muted);opacity:.6}.unknown p{margin:0}
.finding{background:var(--surface);border:1px solid var(--line);border-radius:18px;padding:20px;margin:14px 0}
.finding h3{font:650 18px/1.3 var(--sans);margin:0 0 8px}.finding p{margin:0 0 10px;color:var(--ink-2)}
.tag{font-size:12px;text-transform:uppercase;letter-spacing:.1em;color:var(--crit)!important;font-weight:650}
.evidence{font:13px/1.5 var(--mono);color:var(--muted)!important;border-top:1px dashed var(--line);padding-top:10px;margin:0!important;overflow-wrap:anywhere}
.versus{display:grid;grid-template-columns:1fr 1fr;gap:10px;margin:4px 0 12px}
.v{border-radius:14px;padding:12px;background:var(--surface-2)}.v b{display:block;font:400 26px/1.05 var(--display)}.v span{font-size:13px;color:var(--muted)}
.v.bad b{color:var(--crit);text-decoration:line-through}.v.good b{color:var(--good)}
.pull{margin:18px 0;padding:0 0 0 20px;border-left:3px solid var(--accent)}
.pull p{font:400 clamp(21px,2.6vw,27px)/1.3 var(--display);margin:0 0 8px}.pull footer{color:var(--muted);font-size:14px}
.decisions{display:grid;gap:10px;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));margin:18px 0}
.decision{background:var(--surface);border:1px solid var(--line);border-radius:16px;padding:14px 16px}
.decision b{font:400 26px var(--display);color:var(--accent)}.decision .q{font-weight:600;margin:2px 0 6px}.decision p{margin:0;font-size:14px;color:var(--ink-2)}
pre{font:13px/1.55 var(--mono);white-space:pre-wrap;word-break:break-word;margin:0}
.reply,.code{background:var(--surface-2);border-radius:14px;padding:14px 16px}
.pmeta{display:grid;grid-template-columns:repeat(auto-fit,minmax(110px,1fr));gap:8px;margin:12px 0}
.pmeta div{background:var(--surface);border:1px solid var(--line-2);border-radius:12px;padding:8px 10px}
.pmeta dt{font-size:11px;text-transform:uppercase;letter-spacing:.08em;color:var(--muted)}.pmeta dd{margin:0;font-weight:600;font-variant-numeric:tabular-nums;font-size:14px}
.units{display:flex;flex-wrap:wrap;gap:6px;list-style:none;margin:0;padding:0}
.units li{font-size:13.5px;padding:5px 10px;border-radius:8px;background:color-mix(in srgb,var(--accent-2) 11%,transparent)}
.hl{padding-left:18px;display:grid;gap:8px;color:var(--ink-2)}
.gate{font-weight:600;margin:12px 0}.gate span{font-size:11px;text-transform:uppercase;letter-spacing:.1em;color:var(--good);margin-right:8px}
.findings-line{display:flex;flex-wrap:wrap;gap:8px 16px;font-size:13px;margin:10px 0}
.fl{display:inline-flex;gap:6px;align-items:center;flex-wrap:wrap}.fl b{color:var(--muted);font-weight:600}
.sev{padding:2px 8px;border-radius:999px;font-weight:600;font-size:12px;border:1px solid currentColor}
.sev-critical{color:var(--crit)}.sev-major{color:var(--warn)}.sev-minor{color:var(--minor)}.sev-none{color:var(--good)}
.pnav{display:flex;flex-wrap:wrap;gap:10px;margin-top:24px}.pnav a{padding:6px 12px;border:1px solid var(--line);border-radius:999px;text-decoration:none;font-size:14px;background:var(--surface)}
.routine{list-style:none;margin:16px 0;padding:0;display:grid;gap:10px}
.routine li{display:grid;grid-template-columns:40px minmax(0,1fr);gap:0 12px;background:var(--surface);border:1px solid var(--line);border-radius:16px;padding:14px 16px}
.routine .step-n{font:400 34px/1 var(--display);color:var(--accent);grid-row:span 3}.routine b{font-size:16px}.routine .who{margin:0;font-size:13px;color:var(--accent-2);font-weight:600}.routine p{margin:6px 0 0;font-size:14px;color:var(--ink-2)}
.diffs{display:grid;gap:10px;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));margin:18px 0}
.diff{padding:18px;border-radius:16px;background:linear-gradient(160deg,color-mix(in srgb,var(--accent) 9%,var(--surface)),var(--surface));border:1px solid var(--line)}
.diff b{display:block;font:400 30px/1.05 var(--display)}.diff span{font-size:14px;color:var(--ink-2)}
.incidents{display:grid;gap:10px;grid-template-columns:repeat(auto-fit,minmax(240px,1fr))}
.incident{border:1px solid var(--line);border-radius:16px;padding:16px;background:var(--surface)}.incident h4{margin:0 0 6px}.incident p{margin:0;font-size:14px;color:var(--ink-2)}
.note{border-left:3px solid var(--accent-2);padding:4px 0 4px 14px;color:var(--ink-2);font-size:14.5px}
.caveats{padding-left:18px;display:grid;gap:10px;color:var(--ink-2)}.caveats b{color:var(--ink)}
.sheet .cc-prelude h2{display:none}.sheet .cc-sub{display:none}
.pgrid{display:grid;gap:10px;grid-template-columns:repeat(auto-fit,minmax(220px,1fr))}
.pcard{border:1px dashed var(--line);border-radius:16px;padding:16px}.pcard h3{font:650 17px/1.3 var(--sans);margin:10px 0 6px}.pcard p{margin:0;font-size:14px;color:var(--ink-2)}
.ptop{display:flex;flex-wrap:wrap;gap:10px;align-items:center}.ptop h3{margin:0}.pn{font:400 34px/1 var(--display);color:var(--accent)}
.badge{display:inline-flex;align-items:center;gap:6px;font-size:11.5px;font-weight:600;padding:3px 9px;border-radius:999px;border:1px solid var(--line);white-space:nowrap}
.bdot{width:7px;height:7px;border-radius:50%;background:var(--muted)}.badge-done .bdot{background:var(--good)}.badge-in-progress .bdot{background:var(--accent-2)}
.badge-pending{color:var(--muted)}.badge-pending .bdot{background:transparent;border:1.5px dashed var(--muted)}
.pstats{display:grid;gap:10px;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));margin:12px 0 18px}
.pstats div{border:1px solid var(--line);border-radius:16px;padding:14px;background:var(--surface)}.pstats b{display:block;font:400 36px/1 var(--display)}.pstats span{font-size:13px;color:var(--ink-2)}
.chat{list-style:none;margin:0;padding:0;display:grid;gap:10px}
.msg{display:grid;gap:4px}.msg time{font:12px var(--mono);color:var(--muted)}
.msg p{margin:0;background:linear-gradient(160deg,color-mix(in srgb,var(--accent) 13%,var(--surface)),color-mix(in srgb,var(--accent-2) 9%,var(--surface)));
  border:1px solid var(--line);border-radius:18px 18px 18px 6px;padding:11px 15px;font-size:15px;justify-self:start;max-width:100%;overflow-wrap:anywhere}
.msg.attach p{background:var(--surface-2);font-style:italic;color:var(--muted)}
.prole{background:var(--surface);border:1px solid var(--line);border-radius:16px;padding:16px;margin:10px 0}.prole pre{color:var(--ink-2)}
.prole.common{border-color:color-mix(in srgb,var(--accent) 40%,var(--line))}
.scripts{columns:2 220px;padding-left:18px}.scripts a{font:13px var(--mono)}
.bign{display:grid;gap:10px;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));margin:16px 0}
.bign div{border-radius:18px;padding:18px;background:var(--surface);border:1px solid var(--line)}
.bign b{display:block;font:400 clamp(40px,6vw,56px)/1 var(--display)}.bign span{display:block;font-weight:600;margin:6px 0}.bign p{margin:0;color:var(--ink-2);font-size:13.5px}
.measures{display:grid;gap:10px;margin:16px 0}.measures dt{font-weight:650;color:var(--accent)}.measures dd{margin:2px 0 0;font-size:14px;color:var(--ink-2)}
.fig{margin:22px 0;background:var(--surface);border:1px solid var(--line);border-radius:18px;padding:16px}
.fig figcaption{display:flex;flex-direction:column;margin-bottom:8px}.fig figcaption span{color:var(--muted);font-size:13px}
.bars-box{overflow-x:auto}.bars{display:block;width:100%;min-width:560px;height:auto;font-family:var(--sans)}
.bars .bg{stroke:var(--line);stroke-width:1}.bars .bt{fill:var(--muted);font-size:11px}.bars .bl{fill:var(--ink);font-size:13px;font-weight:600}
.bars .bs{fill:var(--muted);font-size:11px}.bars .bv{fill:var(--ink-2);font-size:12px;font-weight:600}
.legend{display:flex;flex-wrap:wrap;gap:14px;list-style:none;margin:0 0 8px;padding:0;font-size:13px;color:var(--ink-2)}
.sw{display:inline-block;width:12px;height:12px;border-radius:3px;margin-right:6px;vertical-align:-1px}
details{margin:18px 0;border:1px solid var(--line);border-radius:16px;background:var(--surface);padding:4px 16px}
details summary{cursor:pointer;padding:12px 0;font-weight:600}details .tw{border:0;margin:0 -16px 8px}
/* motion: everything is visible without JS; with it, things wait for their reveal */
.js .reveal{opacity:0;transform:translateY(26px);transition:opacity .8s cubic-bezier(.2,.7,.2,1),transform .8s cubic-bezier(.2,.7,.2,1)}
.js .reveal.in{opacity:1;transform:none}
.js .nt.reveal{transition-delay:calc(var(--i)*70ms)}
.js .reveal:not(.in) s.strike::after{transform:scaleX(0)}
.js .reveal:not(.in) .ph-bar span{transform:scaleY(0)}
.js .reveal:not(.in) .struck li,.js .reveal:not(.in) .tiers li{opacity:0;transform:translateY(14px)}
.struck li,.tiers li{transition:opacity .6s,transform .6s;transition-delay:calc(var(--i,0)*80ms)}
.js .hero-in{animation:rise .9s cubic-bezier(.2,.7,.2,1) both}
.js h1.hero-in{animation-delay:.05s}.js .lede.hero-in{animation-delay:.12s}.js .htiles.hero-in{animation-delay:.2s}.js .climb.hero-in{animation-delay:.28s}
@keyframes rise{from{opacity:0;transform:translateY(24px)}to{opacity:1;transform:none}}
.js .climb.hero-in{animation-name:fade}
@keyframes fade{from{opacity:0}to{opacity:1}}
@media (prefers-reduced-motion:reduce){*,*::before,*::after{animation:none!important;transition:none!important}.js .reveal,.js .reveal *{opacity:1!important;transform:none!important}html{scroll-behavior:auto}}
@media print{.bar,.theme,.panel{display:none}.js .reveal{opacity:1;transform:none}}
"""

JS = r"""
(function(){
  var root=document.documentElement;
  var btn=document.querySelector('.theme');
  function current(){var t=root.getAttribute('data-theme');if(t)return t;return matchMedia('(prefers-color-scheme: dark)').matches?'dark':'light';}
  function label(){if(btn){var c=current();btn.textContent=c==='dark'?'☀':'☾';btn.setAttribute('aria-label','Switch to '+(c==='dark'?'light':'dark')+' theme');}}
  if(btn){btn.addEventListener('click',function(){var next=current()==='dark'?'light':'dark';root.setAttribute('data-theme',next);try{localStorage.setItem('theme',next);}catch(e){}label();});label();}
  var reduce=matchMedia('(prefers-reduced-motion: reduce)').matches;
  // counters: the HTML holds the final value; count up to it when it scrolls into view
  function fmt(v,dec){return v.toLocaleString('en-US',{minimumFractionDigits:dec,maximumFractionDigits:dec});}
  function count(el){var to=parseFloat(el.getAttribute('data-to')),dec=+el.getAttribute('data-dec')||0;if(reduce||!isFinite(to))return;
    var t0=null,d=1400;function step(t){if(!t0)t0=t;var k=Math.min((t-t0)/d,1),q=1-Math.pow(1-k,3);el.textContent=fmt(to*q,dec);if(k<1)requestAnimationFrame(step);else el.textContent=fmt(to,dec);}
    el.textContent=fmt(0,dec);requestAnimationFrame(step);}
  var els=[].slice.call(document.querySelectorAll('.reveal,.hero-in'));
  function show(el){el.classList.add('in');[].forEach.call(el.querySelectorAll('.count'),count);}
  if('IntersectionObserver' in window){
    var io=new IntersectionObserver(function(es){es.forEach(function(en){if(en.isIntersecting){show(en.target);io.unobserve(en.target);}});},{rootMargin:'0px 0px -10% 0px',threshold:0.08});
    els.forEach(function(el){io.observe(el);});
  }else{els.forEach(show);}
  // on a phone the chart scrolls sideways: start at the latest run
  var sc=document.querySelector('.climb .cc-scroll');if(sc&&sc.scrollWidth>sc.clientWidth)sc.scrollLeft=sc.scrollWidth;
  // draw the climb line once
  var pl=document.querySelector('.climb .cc-climb');
  if(pl&&pl.getTotalLength&&!reduce){try{var L=pl.getTotalLength();pl.style.strokeDasharray=L;pl.style.strokeDashoffset=L;pl.getBoundingClientRect();
    pl.style.transition='stroke-dashoffset 1.8s cubic-bezier(.6,0,.2,1) .5s';pl.style.strokeDashoffset=0;}catch(e){}}
  // side panels: :target drawers without JS; with it, opened by class so Esc, click-outside and focus all work
  var mainEl=document.getElementById('main'),barEl=document.querySelector('.bar'),footEl=document.querySelector('footer.site');
  var openP=null,opener=null;
  function isPanel(el){return el&&el.classList&&el.classList.contains('panel');}
  function setInert(on){[mainEl,barEl,footEl].forEach(function(x){if(x){if(on)x.setAttribute('inert','');else x.removeAttribute('inert');}});}
  function openPanel(p,from,push){
    if(openP===p)return;if(openP)closePanel(true,true);
    openP=p;opener=from||null;p.classList.add('open');root.classList.add('locked');setInert(true);
    if(push)history.pushState({panel:p.id},'','#'+p.id);
    var s=p.querySelector('.sheet');s.scrollTop=0;setTimeout(function(){s.focus({preventScroll:true});},40);
  }
  function closePanel(silent,keepFocus){
    if(!openP)return;var p=openP;openP=null;p.classList.remove('open');root.classList.remove('locked');setInert(false);
    if(!silent){if(history.state&&history.state.panel===p.id){history.back();}else if(location.hash==='#'+p.id){history.replaceState(null,'',location.pathname+location.search);}}
    if(!keepFocus){var back=opener;if(!back){var c=p.querySelector('[data-close]');back=c&&document.getElementById(c.getAttribute('href').slice(1));}
      if(back){if(!back.matches('a[href],button,input,[tabindex]'))back.setAttribute('tabindex','-1');back.focus({preventScroll:!!opener});}}
  }
  document.addEventListener('click',function(ev){
    var a=ev.target.closest&&ev.target.closest('a');if(!a)return;
    if(a.hasAttribute('data-close')){ev.preventDefault();closePanel(false);return;}
    var h=a.getAttribute('href')||'';
    if(h.charAt(0)==='#'){var t=document.getElementById(h.slice(1));
      if(isPanel(t)){ev.preventDefault();openPanel(t,openP?opener:a,true);}
      else if(openP&&t){ev.preventDefault();closePanel(false,true);setTimeout(function(){t.scrollIntoView({behavior:reduce?'auto':'smooth'});},60);}}
  });
  document.addEventListener('keydown',function(ev){
    if(!openP)return;
    if(ev.key==='Escape'){ev.preventDefault();closePanel(false);return;}
    if(ev.key==='Tab'){var f=[].filter.call(openP.querySelectorAll('a[href],button,summary,[tabindex]:not([tabindex="-1"])'),function(x){return x.offsetParent!==null||x===document.activeElement;});
      if(!f.length)return;var first=f[0],last=f[f.length-1];
      if(ev.shiftKey&&(document.activeElement===first||document.activeElement===openP.querySelector('.sheet'))){ev.preventDefault();last.focus();}
      else if(!ev.shiftKey&&document.activeElement===last){ev.preventDefault();first.focus();}}
  });
  window.addEventListener('popstate',function(){var id=location.hash.slice(1),t=id&&document.getElementById(id);
    if(isPanel(t))openPanel(t,null,false);else if(openP)closePanel(true);});
  var h0=location.hash.slice(1),t0=h0&&document.getElementById(h0);if(isPanel(t0))openPanel(t0,null,false);
})();
"""


def build():
    d = json.load(open(os.path.join(SITE, 'data.json'), encoding='utf-8'))
    m = d['meta']
    cl = climb_chart.climb()
    f = facts(d, cl)
    phases = d['chapters']['build']['phases']
    nav = ''.join('<li><a href="%s">%s</a></li>' % h for h in
                  [('#climb', 'Climb'), ('#numbers', 'Numbers'), ('#b-port', 'Story'), ('#p-prompts', 'Prompts'), ('#p-tokens', 'Tokens')])
    beats = [beat_port(d, f), beat_proof(d, f), beat_process(d), more_row()]
    panels = [p_climb(cl), p_tokens(d), p_muse(d, cl), p_decision(d), p_routine(d, f)] + \
             [p_phase(d, i, p) for i, p in enumerate(phases)] + \
             [p_proof(d), p_baseline(d), p_process(d), p_delivered(d), p_prompts(d, f)]
    body = ''.join([
        '<a class="skip" href="#main">Skip to content</a>',
        '<header class="bar"><div class="wrap"><a class="brand" href="#top">Campfire <span>in F#</span></a>'
        '<nav aria-label="Sections"><ol>%s</ol></nav><button class="theme" type="button" aria-label="Switch theme"></button></div></header>' % nav,
        '<main id="main">', hero(d, cl, f), numbers(d, f), ''.join(beats), '</main>',
        '<footer class="site"><div class="wrap"><p><b>Sources.</b> Muse: Ren&#39;s <code>STATUS.md</code>, <code>AI-ORIENTATION.md</code>, '
        '<code>campfire-fs/Program.fs</code> and <code>bench_official.sh</code> from the hand-off zip. Hand-over: the M4 logs '
        '<code>bench-mac-rebaseline.log</code> and <code>bench-mac-rebaseline-run1-badpost.log</code>. The build and tuning: '
        '<a href="plans/fsharp-port.md">plans/fsharp-port.md</a>, <a href="AGENTS.md">AGENTS.md</a>, <a href="README.md">README.md</a> (Known differences), '
        '<code>bench/results/</code> (the climb is <code>phase7-log.jsonl</code>), the git history of <code>port</code>, and the workflow scripts in '
        '<a href="site/workflows/">site/workflows/</a>. Tokens and prompts: the session transcripts, via <code>site/collect_tokens.py</code>. %s</p>'
        '<p>Written by %s with Claude. Campfire, the Rust port, its parity harness and vectors are &copy; 37signals, LLC, MIT. '
        'Generated from <code>site/data.json</code> and the Phase 7 log by <code>site/build.py</code>.</p></div></footer>' % (e(m['timezone_note']), e(m['author'])),
        '<div class="panels">%s</div>' % ''.join(panels),
    ])
    page = ('<!doctype html>\n<html lang="en"><head><meta charset="utf-8">'
            '<meta name="viewport" content="width=device-width, initial-scale=1">'
            '<title>%s</title><meta name="description" content="%s">'
            '<meta name="color-scheme" content="light dark">'
            '<script>document.documentElement.className+=" js";try{var t=localStorage.getItem("theme");if(t==="light"||t==="dark")document.documentElement.setAttribute("data-theme",t)}catch(e){}</script>'
            '<link rel="preconnect" href="https://fonts.googleapis.com"><link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>'
            '<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Instrument+Serif:ital@0;1&display=swap">'
            '<style>%s%s</style></head><body>%s<script>%s</script></body></html>\n'
            % (e(m['title']), e(m['description']), climb_chart.CSS.strip(), CSS.strip(), body, JS.strip()))
    out = os.path.join(REPO, 'index.html')
    with open(out, 'w', encoding='utf-8') as fh:
        fh.write(page)
    print('wrote %s (%s bytes)' % (out, format(len(page.encode('utf-8')), ',')))


if __name__ == '__main__':
    build()
