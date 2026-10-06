#!/usr/bin/env python3
"""Render index.html (at the repository root) from site/data.json.

Python 3 standard library only. Deterministic: the same data.json gives the same bytes,
so later phases only update data.json (by hand, or through collect_tokens.py) and re-run:

    python3 site/collect_tokens.py   # refresh token and commit figures from the transcripts
    python3 site/build.py            # write index.html
"""
import html
import json
import os
import re
from datetime import datetime, timedelta, timezone

SITE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(SITE)
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
    out = e(text)
    return re.sub(r'`([^`]+)`', r'<code>\1</code>', out)


def pills(items, cls='pill'):
    return '<ul class="pills">%s</ul>' % ''.join('<li class="%s">%s</li>' % (cls, e(i)) for i in items)


def status_badge(status):
    label = {'done': 'Done', 'in-progress': 'In progress', 'pending': 'Pending', 'running': 'Running'}.get(status, status)
    return '<span class="badge badge-%s"><span class="dot" aria-hidden="true"></span>%s</span>' % (e(status), e(label))


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
    return ('<div class="table-wrap %s"><table>%s<thead><tr>%s</tr></thead><tbody>%s</tbody></table></div>'
            % (cls, cap, head, ''.join(body)))


def ratio_cell(text):
    m = re.match(r'([0-9.]+)', text)
    v = float(m.group(1)) if m else 0
    pct = max(0.0, min(v / 1.25, 1.0)) * 100
    one = 100 / 1.25
    tone = 'ahead' if v >= 1.0 else 'behind'
    return ('<td class="num ratio"><span class="ratio-bar" aria-hidden="true"><span class="ratio-fill %s" style="width:%.1f%%"></span>'
            '<span class="ratio-one" style="left:%.1f%%"></span></span><span class="ratio-val">%s</span></td>' % (tone, pct, one, e(text)))


# ---------------------------------------------------------------- charts

def bar_chart(rows, title, desc, unit_fmt, chart_id, series=None):
    """Horizontal bars. rows: [(label, sublabel, {series_key: value})]. series: [(key, label, css_var)]."""
    series = series or [('v', 'Tokens', '--s1')]
    W, label_w, row_h, gap, pad_r = 760, 210, 30, 16, 92
    plot_w = W - label_w - pad_r
    H = len(rows) * (row_h + gap) + 28
    vmax = max(sum(v.get(k, 0) for k, _, _ in series) for _, _, v in rows) or 1
    # nice max
    mag = 10 ** (len(str(int(vmax))) - 1)
    nice = next(m * mag for m in (1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10) if m * mag >= vmax)
    out = ['<svg class="chart" id="%s" viewBox="0 0 %d %d" role="img" aria-labelledby="%s-t %s-d" preserveAspectRatio="xMinYMin meet">'
           % (chart_id, W, H, chart_id, chart_id),
           '<title id="%s-t">%s</title><desc id="%s-d">%s</desc>' % (chart_id, e(title), chart_id, e(desc))]
    # grid
    for i in range(5):
        x = label_w + plot_w * i / 4
        out.append('<line class="grid" x1="%.1f" x2="%.1f" y1="0" y2="%d"/>' % (x, x, H - 22))
        out.append('<text class="tick" x="%.1f" y="%d" text-anchor="middle">%s</text>' % (x, H - 6, e(unit_fmt(nice * i / 4))))
    for idx, (label, sub, vals) in enumerate(rows):
        y = idx * (row_h + gap) + 4
        out.append('<text class="rlabel" x="%d" y="%.1f" text-anchor="end">%s</text>' % (label_w - 12, y + row_h / 2 - 2, e(label)))
        if sub:
            out.append('<text class="rsub" x="%d" y="%.1f" text-anchor="end">%s</text>' % (label_w - 12, y + row_h / 2 + 12, e(sub)))
        x = label_w
        total = sum(vals.get(k, 0) for k, _, _ in series)
        segs = [(k, lab, var, vals.get(k, 0)) for k, lab, var in series if vals.get(k, 0) > 0]
        for j, (k, lab, var, v) in enumerate(segs):
            w = plot_w * v / nice
            last = j == len(segs) - 1
            draw_w = max(w - (0 if last else 2), 0.8)
            tip = '%s: %s %s' % (label, lab, n(v))
            out.append('<g class="mark" tabindex="0" data-tip="%s"><title>%s</title>'
                       '<rect class="bar" style="fill:var(%s);--d:%dms" x="%.1f" y="%.1f" width="%.1f" height="%d" rx="%d"/></g>'
                       % (e(tip), e(tip), var, idx * 60, x, y + 4, draw_w, row_h - 8, 3 if last else 0))
            x += w
        out.append('<text class="vlabel" x="%.1f" y="%.1f">%s</text>' % (x + 8, y + row_h / 2 + 4, e(unit_fmt(total, True))))
    out.append('</svg>')
    legend = ''
    if len(series) > 1:
        legend = '<ul class="legend">%s</ul>' % ''.join(
            '<li><span class="swatch" style="background:var(%s)"></span>%s</li>' % (var, e(lab)) for _, lab, var in series)
    return legend + '<div class="chart-box">%s</div>' % ''.join(out)


def fmt_tokens(v, final=False):
    if v == 0:
        return '0'
    return mega(v)


# ---------------------------------------------------------------- sections

def section_head(ch, kicker=None):
    return ('<header class="chapter-head reveal"><span class="chapter-n" aria-hidden="true">%s</span>'
            '<div><p class="kicker">%s</p><h2 id="%s-title">%s</h2><p class="standfirst">%s</p></div></header>'
            % (e(ch['n']), e(kicker or 'Chapter %s' % ch['n']), e(ch.get('id', '')), e(ch['title']), e(ch['standfirst'])))


def window_tokens(tok, chapter):
    ws = [w for w in tok['main']['windows'] if w['chapter'] == chapter]
    if not ws:
        return ''
    items = ''.join('<li><span>%s</span><b>%s processed</b><i>%s fresh &middot; %s calls</i></li>'
                    % (e(w['label']), e(mega(w['totals']['processed'])), e(mega(w['totals']['fresh'])), n(w['totals']['calls']))
                    for w in ws)
    return ('<aside class="token-aside reveal" aria-label="Orchestrator tokens in this chapter"><h4>Orchestrator tokens, this chapter</h4>'
            '<ul>%s</ul></aside>' % items)


def ch_muse(d, tok):
    c = d['chapters']['muse']
    c['id'] = 'muse'
    facts = ''.join('<div class="fact reveal"><b>%s</b><span>%s</span><p>%s</p></div>' % (e(f['value']), e(f['label']), e(f['detail']))
                    for f in c['facts'])
    instr = ''.join('<li><time>%s</time><span>%s</span></li>' % (e(i['time']), inline(i['text'])) for i in c['instructions'])
    log = ''.join('<li><time>%s</time><span>%s</span></li>' % (e(i['time']), inline(i['text'])) for i in c['ren_log'])
    ct = c['claimed_table']
    h = c['handoff']
    return ''.join([
        '<section class="chapter" id="muse" aria-labelledby="muse-title">', section_head(c),
        '<div class="facts">%s</div>' % facts,
        '<div class="pill-row reveal"><span class="pill-label">Ren\'s stack</span>%s</div>' % pills(c['stack']),
        '<div class="two-col">',
        '<div class="card reveal"><h3>What Chris asked for</h3><p class="muted">%s</p><ol class="log">%s</ol></div>' % (e(c['instructions_intro']), instr),
        '<div class="card reveal"><h3>What Ren reported</h3><p class="muted">%s</p><ol class="log">%s</ol></div>' % (e(c['ren_log_intro']), log),
        '</div>',
        '<div class="reveal">%s</div>' % table(ct['columns'], ct['rows'], ct['caption']),
        '<div class="two-col">',
        '<figure class="handoff reveal"><figcaption>%s</figcaption><div class="bubble"><div class="bubble-head"><span class="avatar" aria-hidden="true">CK</span>'
        '<b>chris kluis</b><time>10:45 AM</time></div><div class="bubble-body">%s</div></div></figure>' % (e(h['intro']), para(h['text'])),
        '<div class="card unknown reveal"><p class="kicker">Tokens in Muse</p><p class="unknown-mark" aria-hidden="true">?</p><p>%s</p></div>' % e(c['tokens_note']),
        '</div>',
        '</section>'])


def ch_handover(d, tok):
    c = d['chapters']['handover']
    c['id'] = 'handover'
    cards = []
    for f in c['findings']:
        stat = ''
        if f.get('stat'):
            s = f['stat']
            stat = ('<div class="versus"><div class="v bad"><b>%s</b><span>%s</span></div><div class="v good"><b>%s</b><span>%s</span></div></div>'
                    % (e(s['bad']), e(s['bad_note']), e(s['good']), e(s['good_note'])))
        cards.append('<article class="finding reveal"><p class="tag">%s</p><h3>%s</h3><p>%s</p>%s<p class="evidence">%s</p></article>'
                     % (e(f['tag']), e(f['title']), e(f['body']), stat, inline(f['evidence'])))
    rb = c['rebaseline']
    dec = c['decision']
    decisions = ''.join('<div class="decision reveal"><b>%s</b><p class="q">%s</p><p>%s</p></div>' % (e(x['id']), e(x['q']), e(x['a'])) for x in c['decisions'])
    return ''.join([
        '<section class="chapter" id="handover" aria-labelledby="handover-title">', section_head(c),
        '<div class="findings">%s</div>' % ''.join(cards),
        '<div class="reveal">%s<p class="source">Source: %s</p></div>' % (table(rb['columns'], rb['rows'], rb['caption']), e(rb['source'])),
        '<blockquote class="pull reveal"><p class="muted">%s</p><p class="pull-q">%s</p><footer>Chris, %s</footer></blockquote>' % (e(dec['intro']), e(dec['quote']), e(dec['time'])),
        '<p class="prose reveal">%s</p>' % e(dec['after']),
        '<div class="decisions">%s</div>' % decisions,
        '<div class="reply reveal"><p class="kicker">Chris\'s reply, 11:37</p><pre>%s</pre></div>' % e(c['decision_reply']),
        window_tokens(tok, 'handover'),
        '</section>'])


def findings_line(agents):
    parts = []
    for role, name in (('verify', 'Verifier'), ('reverify', 'Re-verify')):
        for a in agents:
            if a['role'] == role and 'findings' in a:
                f = a['findings']
                chips = ''.join('<span class="sev sev-%s">%d %s</span>' % (s, f[s], s) for s in ('critical', 'major', 'minor') if f.get(s))
                parts.append('<span class="fl"><b>%s</b>%s</span>' % (name, chips or '<span class="sev sev-none">none</span>'))
    return '<div class="findings-line">%s</div>' % ''.join(parts) if parts else ''


def phase_card(p, wf, commits):
    meta = []
    if wf:
        meta.append('<div><dt>Duration</dt><dd>%s</dd></div>' % e(duration(wf['first'], wf['last'])))
        meta.append('<div><dt>Agents</dt><dd>%d</dd></div>' % len(wf['agents']))
        meta.append('<div><dt>Processed</dt><dd>%s</dd></div>' % e(mega(wf['totals']['processed'])))
        meta.append('<div><dt>Fresh</dt><dd>%s</dd></div>' % e(mega(wf['totals']['fresh'])))
    else:
        meta.append('<div><dt>By</dt><dd>orchestrator</dd></div>')
    if commits is not None:
        meta.append('<div><dt>Commits</dt><dd>%d</dd></div>' % commits)
    units = ''.join('<li>%s</li>' % e(u) for u in p['units'])
    hl = ''.join('<li>%s</li>' % inline(h) for h in p['highlights'])
    gate = '<p class="gate"><span>Gate</span>%s</p>' % e(p['gate']) if p.get('gate') else ''
    when = ('%s &ndash; %s' % (e(local(wf['first'])), e(local(wf['last'], '%-d %b %H:%M' if local(wf['first'], '%d') != local(wf['last'], '%d') else '%H:%M')))) if wf else e(p.get('when', ''))
    return ('<article class="phase reveal" id="%s"><div class="phase-top"><span class="phase-n">%s</span><h3>%s</h3><span class="phase-when">%s</span></div>'
            '<dl class="phase-meta">%s</dl>%s<ul class="units">%s</ul><ul class="hl">%s</ul>%s%s</article>'
            % (e(p['key']), e(p['n']), e(p['title']), when, ''.join(meta), findings_line(wf['agents']) if wf else '',
               units, hl, pills(p['pills']), gate))


def ch_build(d, tok):
    c = d['chapters']['build']
    c['id'] = 'build'
    wfs = {w['key']: w for w in tok['workflows']}
    commits = d.get('commits', {}).get('by_phase_window', {})
    steps = ''.join('<li class="step reveal"><span class="step-n">%d</span><b>%s</b><span class="who">%s</span><span class="count">%s</span><p>%s</p></li>'
                    % (i + 1, e(s['step']), e(s['who']), e(s['count']), e(s['what'])) for i, s in enumerate(c['routine']))
    phases = ''.join(phase_card(p, wfs.get(p['key']), commits.get(p['key'])) for p in c['phases'])
    diffs = ''.join('<div class="diff reveal"><b>%s</b><span>%s</span></div>' % (e(x['value']), e(x['label'])) for x in c['differentials'])
    inc = ''.join('<article class="incident reveal"><h4>%s</h4><p>%s</p></article>' % (e(x['title']), e(x['body'])) for x in c['incidents'])
    return ''.join([
        '<section class="chapter" id="build" aria-labelledby="build-title">', section_head(c),
        '<h3 class="sub reveal">The routine, every phase</h3>',
        '<ol class="routine" aria-label="Phase routine">%s</ol>' % steps,
        '<h3 class="sub reveal">The phases</h3>',
        '<div class="phases">%s</div>' % phases,
        '<h3 class="sub reveal">Checked against the originals</h3>',
        '<div class="diffs">%s</div>' % diffs,
        '<h3 class="sub reveal">What went wrong along the way</h3>',
        '<div class="incidents">%s</div>' % inc,
        window_tokens(tok, 'build'),
        '</section>'])


def process_change(pc):
    """The process change Chris made once tuning began: his words, then the three iteration tiers."""
    if not pc:
        return ''
    quotes = ''.join('<blockquote class="pull reveal"><p class="pull-q">%s</p><footer>Chris, %s</footer></blockquote>'
                     % (e(q['text']), e(pc['when'])) for q in pc['quotes'])
    tiers = table(['Tier', 'Loop time', 'What runs', 'What it decides'], pc['tiers'], 'Iteration tiers from here on')
    return ''.join(['<div class="baseline-head reveal"><h3>%s</h3><span class="muted">%s</span></div>' % (e(pc['title']), e(pc['when'])),
                    quotes, '<p class="prose reveal">%s</p>' % e(pc['summary']), '<div class="reveal">%s</div>' % tiers])


def ch_tuning(d, tok):
    c = d['chapters']['tuning']
    c['id'] = 'tuning'
    b = c['baseline']
    q = c['chris_quote']
    other = table(['Measure', 'Rails', 'Rust', 'F#'], [[o['label'], o['rails'], o['rust'], o['fsharp']] for o in b['other']],
                  'Beyond throughput (same run)')
    pend = ''.join(
        '<article class="pending-card reveal"><div class="phase-top"><span class="phase-n">%s</span><h3>%s</h3>%s</div><p>%s</p>%s</article>'
        % (e(p['n']), e(p['title']), status_badge(p['status']), e(p['target']),
           '<p class="result">%s</p>' % e(p['result']) if p.get('result') else '<p class="result empty">Results will appear here when the phase gates.</p>')
        for p in c['pending'])
    wf = next((w for w in tok['workflows'] if w['key'] == 'phase5b'), None)
    wfline = ''
    if wf:
        wfline = ('<p class="muted reveal">Workflow: %d agent(s) so far, %s processed, %s fresh; state: %s as of %s.</p>'
                  % (len(wf['agents']), e(mega(wf['totals']['processed'])), e(mega(wf['totals']['fresh'])), e(wf['state']), e(local(tok['as_of']))))
    return ''.join([
        '<section class="chapter" id="tuning" aria-labelledby="tuning-title">', section_head(c),
        '<blockquote class="pull reveal"><p class="pull-q">%s</p><footer>Chris, %s, during Phase 3</footer></blockquote>' % (e(q['text']), e(q['time'])),
        '<div class="baseline-head reveal"><h3>%s</h3>%s<span class="muted">%s</span></div>' % (e(b['title']), status_badge(b['status']), e(b['when'])),
        ('<p class="note reveal">%s</p>' % e(b['note'])) if b.get('note') else '',
        '<p class="prose reveal">%s</p>' % e(b['summary']),
        '<div class="reveal">%s<p class="source">%s Source: <code>%s</code>.</p></div>' % (
            table(b['columns'], b['rows'], 'Throughput, %s. The bar shows F# as a share of Rust; the tick marks parity.' % b['unit'],
                  ratio_col=b.get('ratio_col'), cls='baseline'), e(b['method']), e(b['source'])),
        '<div class="reveal">%s</div>' % other,
        wfline,
        process_change(c.get('process_change')),
        '<div class="pending-grid">%s</div>' % pend,
        window_tokens(tok, 'tuning'),
        '</section>'])


def ch_delivered(d, tok):
    c = d['chapters']['delivered']
    c['id'] = 'delivered'
    steps = ''.join('<li class="deliver reveal">%s<h3>%s</h3><p>%s</p>%s</li>'
                    % (status_badge(s['status']), e(s['title']), e(s['detail']),
                       '<a href="%s">%s</a>' % (e(s['link']), e(s['link'])) if s.get('link') else '')
                    for s in c['steps'])
    return ''.join(['<section class="chapter" id="delivered" aria-labelledby="delivered-title">', section_head(c),
                    '<ol class="deliver-list">%s</ol>' % steps, '</section>'])


def sec_tokens(d, tok):
    wfs = tok['workflows']
    main = tok['main']
    others = tok['others']
    g = tok['grand']
    s1 = [('v', 'Tokens', '--s1')]
    rows = [(w['title'].replace(': ', ' · ', 1), '%d agent%s' % (len(w['agents']), '' if len(w['agents']) == 1 else 's'), {'v': w['totals']['processed']}) for w in wfs]
    rows.append(('Orchestrator session', '%s calls' % n(main['totals']['calls']), {'v': main['totals']['processed']}))
    for o in others:
        rows.append(('This page\'s agent', 'at collection', {'v': o['totals']['processed']}))
    chart1 = bar_chart(rows, 'Tokens processed per phase',
                       'Horizontal bars: total tokens processed (input, cache writes, cache reads and output) by each workflow, the orchestrating session and this page\'s agent. Values are in the table below.',
                       fmt_tokens, 'chart-processed', s1)
    series = [('cache_write', 'Cache writes', '--s1'), ('output', 'Output', '--s2'), ('input', 'Uncached input', '--s3')]
    rows2 = [(w['title'].replace(': ', ' · ', 1), '', {k: w['totals'][k] for k, _, _ in series}) for w in wfs]
    rows2.append(('Orchestrator session', '', {k: main['totals'][k] for k, _, _ in series}))
    chart2 = bar_chart(rows2, 'Fresh tokens per phase',
                       'Stacked horizontal bars: fresh tokens (cache writes, output, uncached input) by workflow and the orchestrating session. Values are in the tables below.',
                       fmt_tokens, 'chart-fresh', series)
    # reconciliation
    rec_rows = []
    for w in wfs:
        h = w.get('harness')
        delta = (h['tokens'] - w['final_context_sum']) if h else None
        rec_rows.append('<tr><th scope="row">%s</th><td class="num">%d</td><td class="num">%s</td><td class="num">%s</td><td class="num">%s</td><td class="num">%s</td><td class="num">%s</td><td class="num">%s</td></tr>' % (
            e(w['title']), len(w['agents']), e(duration(w['first'], w['last'])) + ('<br><small>harness %s</small>' % e(h['duration']) if h else ''),
            n(h['tokens']) if h else '&ndash;', n(w['final_context_sum']),
            ('%+d' % delta) if delta is not None else '&ndash;', n(w['totals']['fresh']), n(w['totals']['processed'])))
    rec = ('<div class="table-wrap"><table class="rec"><caption>Reconciliation with the workflow harness</caption><thead><tr>'
           '<th scope="col">Workflow</th><th scope="col" class="num">Agents</th><th scope="col" class="num">Ran</th>'
           '<th scope="col" class="num">Harness figure</th><th scope="col" class="num">Final contexts, summed</th><th scope="col" class="num">Gap</th>'
           '<th scope="col" class="num">Fresh</th><th scope="col" class="num">Processed</th></tr></thead><tbody>%s</tbody></table></div>' % ''.join(rec_rows))
    # by role / model
    role_names = {'build': 'Builders', 'verify': 'Verifiers', 'fix': 'Fixers', 'reverify': 'Re-verifiers', 'bench': 'Bench', 'review': 'Reviewers'}
    role_rows = [[role_names.get(k, k), str(v['agents']), n(v['totals']['calls']), mega(v['totals']['processed']), mega(v['totals']['fresh']), n(v['totals']['output'])]
                 for k, v in tok['by_role'].items()]
    model_rows = [[k, str(v['agents']) + (' + orchestrator' if k in main['models'] else ''), n(v['totals']['calls']), mega(v['totals']['processed']), mega(v['totals']['fresh']), n(v['totals']['output'])]
                  for k, v in tok['by_model'].items()]
    cols = ['', 'Agents', 'API calls', 'Processed', 'Fresh', 'Output']
    # per agent
    ag_rows = []
    for w in wfs:
        for a in w['agents']:
            ag_rows.append([w['title'].split(':')[0], a['label'], a['model'], duration(a['first'], a['last']), n(a['totals']['calls']),
                            n(a['totals']['processed']), n(a['totals']['fresh']), n(a['final_context'])])
    agents_tbl = table(['Phase', 'Agent', 'Model', 'Ran', 'Calls', 'Processed', 'Fresh', 'Final context'], ag_rows, num_from=4, cls='dense')
    win_rows = [[w['label'], local(w['from']), n(w['totals']['calls']), n(w['totals']['processed']), n(w['totals']['fresh'])] for w in main['windows']]
    win_tbl = table(['Orchestrator window', 'From', 'Calls', 'Processed', 'Fresh'], win_rows, 'The orchestrating session, split at the events that changed its job', num_from=2)
    cache_share = 100.0 * g['cache_read'] / g['processed'] if g['processed'] else 0
    wf_agents = tok['workflow_agents']
    return ''.join([
        '<section class="chapter tokens" id="tokens" aria-labelledby="tokens-title">',
        '<header class="chapter-head reveal"><span class="chapter-n" aria-hidden="true">&Sigma;</span><div><p class="kicker">Every token, counted</p>'
        '<h2 id="tokens-title">The tokens it took</h2><p class="standfirst">Summed from the transcripts Claude Code wrote, once per API call, '
        'for the orchestrating session and all %d workflow agents. Nothing here is estimated. As of %s.</p></div></header>' % (wf_agents, e(local(tok['as_of']))),
        '<div class="big-numbers">',
        '<div class="bn reveal"><b>%s</b><span>tokens processed</span><p>Every token the models read or wrote: %s. Cache reads are %.1f%% of it: each turn re-reads the agent\'s whole context from the prompt cache.</p></div>' % (e(mega(g['processed'], 2)), n(g['processed']), cache_share),
        '<div class="bn reveal"><b>%s</b><span>fresh tokens</span><p>Uncached input, cache writes and output: %s. What was new to the model on each call.</p></div>' % (e(mega(g['fresh'])), n(g['fresh'])),
        '<div class="bn reveal"><b>%s</b><span>tokens written by models</span><p>Output across %s API calls, the code, tests, reports and commit messages included.</p></div>' % (e(mega(g['output'])), n(g['calls'])),
        '</div>',
        '<div class="card measures reveal"><h3>Three ways to count, and which one the harness used</h3><dl>'
        '<div><dt>Processed</dt><dd>input + cache writes + cache reads + output, summed over every API call. The fullest measure of work done.</dd></div>'
        '<div><dt>Fresh</dt><dd>the same without cache reads. Cache reads are cheap and repetitive; fresh tokens are closer to new work.</dd></div>'
        '<div><dt>Final context</dt><dd>for each agent, the size of its context on its last call (input + cache writes + cache reads). The workflow harness\'s per-phase figure is this, summed over the phase\'s agents: it lands within a few dozen tokens of ours on every phase. It measures how large the agents\' contexts grew, not how much they processed.</dd></div>'
        '</dl></div>',
        '<figure class="figure reveal"><figcaption><b>Tokens processed per phase</b><span>Linear scale. The orchestrator ran the whole time; this page\'s agent is counted as of collection.</span></figcaption>%s</figure>' % chart1,
        '<figure class="figure reveal"><figcaption><b>Fresh tokens per phase</b><span>Cache reads left out, so the new work is visible.</span></figcaption>%s</figure>' % chart2,
        '<div class="reveal">%s<p class="source">The gap is the harness figure minus our sum of final contexts. Durations are first to last transcript entry.</p></div>' % rec,
        '<div class="reveal">%s</div>' % table(cols, role_rows, 'By role (workflow agents)'),
        '<div class="reveal">%s</div>' % table(cols, model_rows, 'By model (everything, the orchestrator and this page\'s agent included)'),
        '<div class="reveal">%s</div>' % win_tbl,
        '<details class="reveal"><summary>All %d workflow agents</summary>%s</details>' % (len(ag_rows), agents_tbl),
        '<div class="card unknown slim reveal"><p><b>Not counted:</b> Ren\'s run in Muse. Its token use isn\'t in the hand-off and no transcript of it was shared, so it is unknown rather than zero.</p></div>',
        '<p class="source reveal">Method: <code>site/collect_tokens.py</code> reads the session transcript and every <code>agent-*.jsonl</code> under its <code>subagents/</code> folder, '
        'keeps the last line of each API message id (Claude Code repeats a message\'s usage on each of its content blocks), and maps workflow ids to phases by the script names it copies into <code>site/workflows/</code>. Rule: %s.</p>' % e(tok['rule']),
        '</section>'])


def sec_prompts(d):
    p = d['prompts']
    msgs = []
    words = 0
    for m in p['chris']:
        if m.get('kind') == 'attachment':
            msgs.append('<li class="msg attach reveal"><time>%s</time><p>%s</p></li>' % (e(m['time']), e(m['text'])))
            continue
        words += len(m['text'].split())
        msgs.append('<li class="msg reveal"><time>%s</time><p>%s</p></li>' % (e(m['time']), e(m['text']).replace('\n', '<br>')))
    sp = p['status_pings']
    words += sp['count']
    roles = ''.join('<div class="prompt-role reveal"><p class="kicker">%s</p><pre>%s</pre></div>' % (e(r['role']), e(r['text'])) for r in p['roles'])
    units = ''.join('<div class="unit-group reveal"><h4>%s <a href="site/workflows/%s">%s</a></h4><ul>%s</ul></div>'
                    % (e(d['token_config']['phase_titles'].get(u['phase'], u['phase'])), e(u['script']), e(u['script']),
                       ''.join('<li>%s</li>' % inline(i) for i in u['items'])) for u in p['units'])
    return ''.join([
        '<section class="chapter prompts" id="prompts" aria-labelledby="prompts-title">',
        '<header class="chapter-head reveal"><span class="chapter-n" aria-hidden="true">&ldquo;</span><div><p class="kicker">The prompts</p>'
        '<h2 id="prompts-title">What was actually said</h2><p class="standfirst">%s</p></div></header>' % e(p['intro']),
        '<div class="prompt-stats reveal"><div><b>%d</b><span>messages from Chris</span></div><div><b>%d</b><span>%s</span></div><div><b>%d</b><span>workflow scripts</span></div></div>'
        % (len([m for m in p['chris'] if m.get('kind') != 'attachment']) + sp['count'], words, e(p['word_count_note']), len(p['units'])),
        '<ol class="chat">%s</ol>' % ''.join(msgs),
        '<p class="muted reveal">%s</p>' % e(sp['note']),
        '<h3 class="sub reveal">The routine, as code</h3>',
        '<p class="prose reveal">%s</p>' % e(p['routine_intro']),
        '<div class="prompt-role common reveal"><p class="kicker">%s</p><pre>%s</pre></div>' % (e(p['common_label']), e(p['common'])),
        '<div class="roles">%s</div>' % roles,
        '<div class="units-grid">%s</div>' % units,
        '</section>'])


def hero(d, tok):
    h = d['hero']
    g = tok['grand']
    c = d.get('commits', {})
    stats = [
        (mega(g['processed'], 2), 'tokens processed', 'counted from transcripts'),
        (str(tok['workflow_agents'] + 1), 'Claude agents', '%d in workflows + the orchestrator' % tok['workflow_agents']),
        (str(c.get('total', '')), 'commits on port', 'Phases 0 to 5b'),
        ('996', 'tests at the Phase 5 gate', '0 failed'),
        ('22,374', 'renders, byte for byte', 'identical to the Rust port'),
    ]
    tiles = ''.join('<div class="stat"><b>%s</b><span>%s</span><i>%s</i></div>' % (e(a), e(b), e(c2)) for a, b, c2 in stats)
    return ('<section class="hero" aria-labelledby="hero-title"><div class="glow" aria-hidden="true"></div>'
            '<p class="kicker hero-in">%s</p><h1 id="hero-title" class="hero-in">%s</h1><p class="lede hero-in">%s</p>'
            '<div class="stats hero-in">%s</div>%s</section>' % (e(h['kicker']), e(h['title']), e(h['lede']), tiles, pills(h['pills'])))


def timeline(d):
    items = ''.join('<li class="tl-%s"><a href="#%s"><span class="tl-dot" aria-hidden="true"></span><span class="tl-n">%d</span>'
                    '<b>%s</b><time>%s</time><span class="tl-blurb">%s</span>%s</a></li>'
                    % (e(t['status']), e(t['id']), t['n'], e(t['title']), e(t['when']), e(t['blurb']), status_badge(t['status']))
                    for t in d['timeline'])
    return '<nav class="timeline reveal" aria-label="The five chapters"><ol>%s</ol></nav>' % items


CSS = r"""
:root{
  --bg:#f8f6f2;--surface:#ffffff;--surface-2:#f1ede6;--ink:#15130f;--ink-2:#4f4a43;--muted:#7d776d;
  --line:rgba(21,19,15,.12);--line-2:rgba(21,19,15,.06);--accent:#c2410c;--accent-2:#6d4aff;
  --good:#0b7a35;--warn:#a15c00;--crit:#c0262d;--minor:#6b665d;
  --s1:#2a78d6;--s2:#eb6834;--s3:#1baf7a;--grid:#e6e2da;--axis:#8a857c;
  --glow-a:rgba(234,88,12,.20);--glow-b:rgba(109,74,255,.14);
  --shadow:0 1px 2px rgba(0,0,0,.04),0 12px 32px -12px rgba(30,20,10,.12);
  --display:"Instrument Serif",ui-serif,Georgia,serif;
  --sans:system-ui,-apple-system,"SF Pro Text","Segoe UI",Roboto,sans-serif;
  --mono:ui-monospace,"SF Mono",Menlo,Consolas,monospace;
  color-scheme:light;
}
@media (prefers-color-scheme:dark){:root:not([data-theme="light"]){
  --bg:#0b0a09;--surface:#151311;--surface-2:#1d1a17;--ink:#f5f1ea;--ink-2:#c8c1b5;--muted:#8e877c;
  --line:rgba(255,255,255,.11);--line-2:rgba(255,255,255,.05);--accent:#ff8a4c;--accent-2:#a594ff;
  --good:#34c46a;--warn:#e8a33a;--crit:#ff6b6b;--minor:#a39d92;
  --s1:#3987e5;--s2:#d95926;--s3:#199e70;--grid:#2a2724;--axis:#8e877c;
  --glow-a:rgba(255,122,61,.22);--glow-b:rgba(140,110,255,.18);
  --shadow:0 1px 2px rgba(0,0,0,.4),0 16px 40px -16px rgba(0,0,0,.6);color-scheme:dark;}}
:root[data-theme="dark"]{
  --bg:#0b0a09;--surface:#151311;--surface-2:#1d1a17;--ink:#f5f1ea;--ink-2:#c8c1b5;--muted:#8e877c;
  --line:rgba(255,255,255,.11);--line-2:rgba(255,255,255,.05);--accent:#ff8a4c;--accent-2:#a594ff;
  --good:#34c46a;--warn:#e8a33a;--crit:#ff6b6b;--minor:#a39d92;
  --s1:#3987e5;--s2:#d95926;--s3:#199e70;--grid:#2a2724;--axis:#8e877c;
  --glow-a:rgba(255,122,61,.22);--glow-b:rgba(140,110,255,.18);
  --shadow:0 1px 2px rgba(0,0,0,.4),0 16px 40px -16px rgba(0,0,0,.6);color-scheme:dark;}
*{box-sizing:border-box}
html{scroll-behavior:smooth;-webkit-text-size-adjust:100%;overflow-x:clip}
body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.6 var(--sans);overflow-x:clip;-webkit-font-smoothing:antialiased}
a{color:var(--accent);text-underline-offset:3px}
code{overflow-wrap:anywhere;font:.88em var(--mono);background:var(--surface-2);padding:.1em .35em;border-radius:5px}
pre{font:13px/1.55 var(--mono);white-space:pre-wrap;word-break:break-word;margin:0}
.two-col>*,.facts>*,.findings>*,.phases>*,.routine>*,.decisions>*,.diffs>*,.incidents>*,.big-numbers>*,.pending-grid>*,.units-grid>*,.roles>*,.stats>*{min-width:0}
.wrap{max-width:1180px;margin:0 auto;padding:0 16px}
@media (min-width:720px){.wrap{padding:0 32px}}
.skip{position:absolute;left:-999px}.skip:focus{left:16px;top:8px;z-index:20;background:var(--surface);padding:8px 12px;border-radius:8px}
/* top bar */
.bar{position:sticky;top:0;z-index:10;backdrop-filter:saturate(1.6) blur(18px);-webkit-backdrop-filter:saturate(1.6) blur(18px);
  background:color-mix(in srgb,var(--bg) 78%,transparent);border-bottom:1px solid var(--line-2)}
.bar .wrap{display:flex;align-items:center;gap:16px;height:52px}
.brand{font-weight:650;letter-spacing:-.01em;color:var(--ink);text-decoration:none;white-space:nowrap}
.brand span{color:var(--accent)}
.bar nav{flex:1;overflow-x:auto;scrollbar-width:none}.bar nav::-webkit-scrollbar{display:none}
.bar nav ol{display:flex;gap:4px;list-style:none;margin:0;padding:0}
.bar nav a{display:block;padding:6px 10px;border-radius:999px;color:var(--ink-2);text-decoration:none;font-size:13px;white-space:nowrap}
.bar nav a:hover,.bar nav a:focus-visible{background:var(--surface-2);color:var(--ink)}
.theme{border:1px solid var(--line);background:var(--surface);color:var(--ink);border-radius:999px;width:34px;height:34px;cursor:pointer;flex:none;display:none}
.js .theme{display:inline-grid;place-items:center}
/* hero */
.hero{position:relative;padding:72px 0 40px;isolation:isolate}
.glow{position:absolute;inset:-120px -40vw auto;height:620px;z-index:-1;
  background:radial-gradient(40% 50% at 30% 40%,var(--glow-a),transparent 70%),radial-gradient(35% 45% at 72% 30%,var(--glow-b),transparent 70%);
  filter:blur(10px)}
.kicker{font-size:12px;letter-spacing:.14em;text-transform:uppercase;color:var(--accent);font-weight:650;margin:0 0 10px}
h1{font:400 clamp(56px,11vw,132px)/.92 var(--display);letter-spacing:-.02em;margin:0 0 24px}
.lede{font-size:clamp(18px,2.2vw,22px);line-height:1.5;color:var(--ink-2);max-width:760px;margin:0 0 36px}
.stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:12px;margin-bottom:24px}
.stat{background:var(--surface);border:1px solid var(--line);border-radius:18px;padding:18px 18px 16px;box-shadow:var(--shadow)}
.stat b{display:block;font:400 40px/1 var(--display);letter-spacing:-.01em}
.stat span{display:block;font-weight:600;margin-top:8px;font-size:14px}
.stat i{display:block;font-style:normal;color:var(--muted);font-size:13px}
.pills{display:flex;flex-wrap:wrap;gap:6px;list-style:none;margin:0;padding:0}
.pill{font-size:12.5px;padding:4px 10px;border-radius:999px;border:1px solid var(--line);background:var(--surface);color:var(--ink-2)}
.pill-row{display:flex;flex-wrap:wrap;gap:10px;align-items:center;margin:20px 0}
.pill-label{font-size:12px;text-transform:uppercase;letter-spacing:.1em;color:var(--muted)}
/* timeline */
.timeline{margin:24px 0 8px}
.timeline ol{list-style:none;margin:0;padding:0;display:grid;gap:12px;grid-template-columns:1fr}
@media (min-width:900px){.timeline ol{grid-template-columns:repeat(5,1fr);gap:0}}
.timeline li{position:relative}
.timeline a{display:grid;gap:4px;padding:18px 16px 18px 44px;text-decoration:none;color:var(--ink);border:1px solid var(--line);border-radius:16px;background:var(--surface);height:100%}
@media (min-width:900px){
  .timeline a{padding:44px 16px 18px;border:0;background:none;border-radius:0}
  .timeline li::before{content:"";position:absolute;top:17px;left:0;right:0;height:2px;background:var(--line)}
  .timeline li.tl-done::before{background:linear-gradient(90deg,var(--accent),var(--accent-2))}
  .timeline li.tl-in-progress::before{background:linear-gradient(90deg,var(--accent-2),var(--line))}
}
.tl-dot{position:absolute;left:16px;top:22px;width:14px;height:14px;border-radius:50%;border:2px solid var(--muted);background:var(--bg)}
@media (min-width:900px){.tl-dot{left:16px;top:11px}}
.tl-done .tl-dot{background:var(--accent);border-color:var(--accent)}
.tl-in-progress .tl-dot{border-color:var(--accent-2);box-shadow:0 0 0 5px color-mix(in srgb,var(--accent-2) 22%,transparent)}
.tl-n{font:400 15px var(--display);color:var(--muted)}
.timeline b{font-size:15px;line-height:1.3}
.timeline time{font-size:12.5px;color:var(--muted)}
.tl-blurb{font-size:13.5px;color:var(--ink-2)}
.timeline .badge{justify-self:start;margin-top:6px}
.badge{display:inline-flex;align-items:center;gap:6px;font-size:11.5px;font-weight:600;letter-spacing:.02em;padding:3px 9px;border-radius:999px;border:1px solid var(--line);white-space:nowrap}
.badge .dot{width:7px;height:7px;border-radius:50%;background:var(--muted)}
.badge-done .dot{background:var(--good)}.badge-in-progress .dot,.badge-running .dot{background:var(--accent-2)}
.badge-pending{color:var(--muted)}.badge-pending .dot{background:transparent;border:1.5px dashed var(--muted)}
/* chapters */
.chapter{padding:88px 0 24px;border-top:1px solid var(--line-2);scroll-margin-top:56px}
.chapter-head{display:grid;grid-template-columns:auto 1fr;gap:20px;align-items:start;margin-bottom:36px}
.chapter-n{font:400 clamp(72px,12vw,150px)/.8 var(--display);background:linear-gradient(160deg,var(--accent),var(--accent-2));
  -webkit-background-clip:text;background-clip:text;color:transparent;min-width:.6em}
h2{font:400 clamp(36px,6vw,64px)/1 var(--display);letter-spacing:-.015em;margin:0 0 14px}
.standfirst{font-size:clamp(17px,2vw,20px);color:var(--ink-2);max-width:780px;margin:0}
h3{font-size:19px;letter-spacing:-.01em;margin:0 0 10px;line-height:1.3}
h3.sub{font:400 30px/1.1 var(--display);margin:56px 0 18px}
h4{margin:0 0 8px;font-size:15px}
.muted{color:var(--muted)}.prose{max-width:780px;font-size:17px;color:var(--ink-2)}
.source{font-size:13px;color:var(--muted);margin:8px 0 0}
.card{background:var(--surface);border:1px solid var(--line);border-radius:20px;padding:22px;box-shadow:var(--shadow)}
.two-col{display:grid;gap:16px;margin:16px 0;grid-template-columns:1fr}
@media (min-width:860px){.two-col{grid-template-columns:1fr 1fr}}
.facts{display:grid;gap:12px;grid-template-columns:repeat(auto-fit,minmax(220px,1fr))}
.fact{background:var(--surface);border:1px solid var(--line);border-radius:18px;padding:20px;box-shadow:var(--shadow)}
.fact b{font:400 44px/1 var(--display)}.fact span{display:block;font-weight:600;margin:6px 0 6px}.fact p{margin:0;color:var(--ink-2);font-size:14px}
.log{list-style:none;margin:0;padding:0;display:grid;gap:10px}
.log li{display:grid;grid-template-columns:52px minmax(0,1fr);overflow-wrap:anywhere;gap:10px;font-size:14.5px}
.log time{font:12.5px var(--mono);color:var(--accent);padding-top:2px}
/* tables */
.table-wrap{overflow-x:auto;margin:18px 0;border:1px solid var(--line);border-radius:16px;background:var(--surface);box-shadow:var(--shadow)}
table{border-collapse:collapse;width:100%;font-size:14px}
caption{text-align:left;padding:14px 16px 4px;font-size:13px;color:var(--muted);caption-side:top}
th,td{padding:10px 16px;border-bottom:1px solid var(--line-2);text-align:left;vertical-align:top}
thead th{font-size:12px;text-transform:uppercase;letter-spacing:.06em;color:var(--muted);font-weight:600;white-space:nowrap}
tbody th{font-weight:600}
.num{text-align:right;font-variant-numeric:tabular-nums;white-space:nowrap}
tbody tr:last-child>*{border-bottom:0}
.dense th,.dense td{padding:7px 12px;font-size:13px}
td small{color:var(--muted)}
.ratio{min-width:170px}
.ratio-bar{display:inline-block;position:relative;width:96px;height:8px;border-radius:4px;background:var(--surface-2);vertical-align:middle;margin-right:10px}
.ratio-fill{position:absolute;left:0;top:0;bottom:0;border-radius:4px;background:var(--s1)}
.ratio-fill.ahead{background:var(--good)}
.ratio-one{position:absolute;top:-3px;bottom:-3px;width:2px;background:var(--ink);opacity:.55}
.ratio-val{display:inline-block;min-width:44px}
/* handoff */
.handoff{margin:0}.handoff figcaption{color:var(--muted);font-size:14px;margin-bottom:10px}
.bubble{background:#1a1d21;color:#e8e8e8;border-radius:18px;padding:18px 20px;box-shadow:var(--shadow);font-size:15px}
.bubble-head{display:flex;align-items:center;gap:10px;margin-bottom:6px}
.bubble-head b{color:#fff}.bubble-head time{color:#9aa0a6;font-size:12.5px}
.avatar{width:32px;height:32px;border-radius:8px;display:grid;place-items:center;background:linear-gradient(135deg,#c2410c,#6d4aff);color:#fff;font-size:12px;font-weight:700}
.bubble-body p{margin:.5em 0}
.unknown{display:flex;flex-direction:column;justify-content:center}
.unknown-mark{font:400 120px/1 var(--display);margin:0;color:var(--muted);opacity:.5}
.unknown.slim{margin:16px 0}.unknown.slim p{margin:0}
/* findings */
.findings{display:grid;gap:16px;grid-template-columns:1fr}
@media (min-width:860px){.findings{grid-template-columns:1fr 1fr}}
.finding{background:var(--surface);border:1px solid var(--line);border-radius:20px;padding:24px;box-shadow:var(--shadow);display:flex;flex-direction:column}
.finding p{margin:0 0 12px;color:var(--ink-2)}
.tag{font-size:12px;text-transform:uppercase;letter-spacing:.1em;color:var(--crit)!important;font-weight:650}
.evidence{margin-top:auto!important;font:13px/1.5 var(--mono);color:var(--muted)!important;border-top:1px dashed var(--line);padding-top:12px}
.versus{display:grid;grid-template-columns:1fr 1fr;gap:10px;margin:4px 0 14px}
.v{border-radius:14px;padding:14px;background:var(--surface-2)}
.v b{display:block;font:400 30px/1.05 var(--display)}.v span{font-size:13px;color:var(--muted)}
.v.bad b{color:var(--crit);text-decoration:line-through;text-decoration-thickness:2px}.v.good b{color:var(--good)}
.pull{margin:48px 0 20px;padding:0 0 0 24px;border-left:3px solid var(--accent)}
.pull-q{font:400 clamp(24px,3.4vw,36px)/1.25 var(--display);margin:6px 0 10px;max-width:900px}
.pull footer{color:var(--muted);font-size:14px}
.decisions{display:grid;gap:12px;grid-template-columns:1fr;margin:20px 0}
@media (min-width:640px){.decisions{grid-template-columns:1fr 1fr}}@media (min-width:980px){.decisions{grid-template-columns:repeat(3,1fr)}}
.phase-when{font-size:13px;color:var(--muted);margin-left:auto;font-variant-numeric:tabular-nums}
.decision{background:var(--surface);border:1px solid var(--line);border-radius:16px;padding:16px 18px}
.decision b{font:400 26px var(--display);color:var(--accent)}.decision .q{font-weight:600;margin:2px 0 6px}.decision p{margin:0;font-size:14px;color:var(--ink-2)}
.reply{background:var(--surface-2);border-radius:16px;padding:16px 20px;margin:16px 0;max-width:640px}
.token-aside{margin:28px 0 0;border:1px dashed var(--line);border-radius:16px;padding:14px 18px}
.token-aside h4{font-size:12px;text-transform:uppercase;letter-spacing:.1em;color:var(--muted);font-weight:600}
.token-aside ul{list-style:none;margin:0;padding:0;display:grid;gap:6px}
.token-aside li{display:flex;flex-wrap:wrap;gap:4px 12px;font-size:14px;align-items:baseline}
.token-aside li span{flex:1 1 200px;min-width:0}.token-aside li i{font-style:normal;color:var(--muted);font-size:13px}
/* routine */
.routine{list-style:none;margin:0;padding:0;display:grid;gap:12px;grid-template-columns:1fr;counter-reset:s}
@media (min-width:980px){.routine{grid-template-columns:repeat(5,1fr)}}
.step{position:relative;background:var(--surface);border:1px solid var(--line);border-radius:18px;padding:18px;display:flex;flex-direction:column;gap:2px}
@media (min-width:980px){.step:not(:last-child)::after{content:"\2192";position:absolute;right:-12px;top:22px;color:var(--accent);font-weight:700;z-index:1;background:var(--bg);border-radius:50%;width:20px;text-align:center;line-height:20px}}
.step-n{font:400 34px/1 var(--display);color:var(--accent)}
.step b{font-size:17px}.step .who{font-size:13px;font-weight:600;color:var(--accent-2)}.step .count{font-size:12.5px;color:var(--muted)}
.step p{margin:8px 0 0;font-size:14px;color:var(--ink-2)}
/* phases */
.phases{display:grid;gap:16px;grid-template-columns:1fr}
@media (min-width:860px){.phases{grid-template-columns:1fr 1fr}}
.phase,.pending-card{background:var(--surface);border:1px solid var(--line);border-radius:22px;padding:24px;box-shadow:var(--shadow);display:flex;flex-direction:column;gap:12px}
.phase-top{display:flex;align-items:baseline;gap:14px;flex-wrap:wrap}
.phase-top h3{margin:0;font:400 30px/1 var(--display)}
.phase-n{font:400 46px/.8 var(--display);background:linear-gradient(160deg,var(--accent),var(--accent-2));-webkit-background-clip:text;background-clip:text;color:transparent}
.phase-meta{display:grid;grid-template-columns:repeat(auto-fit,minmax(92px,1fr));gap:8px;margin:0}
.phase-meta div{background:var(--surface-2);border-radius:12px;padding:8px 10px}
.phase-meta dt{font-size:11px;text-transform:uppercase;letter-spacing:.08em;color:var(--muted)}
.phase-meta dd{margin:0;font-weight:600;font-variant-numeric:tabular-nums;font-size:14px}
.units{display:flex;flex-wrap:wrap;gap:6px;list-style:none;margin:0;padding:0}
.units li{font-size:13px;padding:4px 10px;border-radius:8px;background:color-mix(in srgb,var(--accent-2) 10%,transparent);color:var(--ink)}
.hl{margin:0;padding-left:18px;display:grid;gap:8px;font-size:14.5px;color:var(--ink-2)}
.gate{margin:0;font-size:13.5px;font-weight:600}.gate span{font-size:11px;text-transform:uppercase;letter-spacing:.1em;color:var(--good);margin-right:8px}
.findings-line{display:flex;flex-wrap:wrap;gap:8px 16px;font-size:13px}
.fl{display:inline-flex;gap:6px;align-items:center;flex-wrap:wrap}.fl b{color:var(--muted);font-weight:600}
.sev{padding:2px 8px;border-radius:999px;font-weight:600;font-size:12px;border:1px solid currentColor}
.sev-critical{color:var(--crit)}.sev-major{color:var(--warn)}.sev-minor{color:var(--minor)}.sev-none{color:var(--good)}
.diffs{display:grid;gap:12px;grid-template-columns:repeat(auto-fit,minmax(200px,1fr))}
.diff{padding:20px;border-radius:18px;background:linear-gradient(160deg,color-mix(in srgb,var(--accent) 9%,var(--surface)),var(--surface));border:1px solid var(--line)}
.diff b{display:block;font:400 34px/1.05 var(--display)}.diff span{font-size:14px;color:var(--ink-2)}
.incidents{display:grid;gap:12px;grid-template-columns:repeat(auto-fit,minmax(250px,1fr))}
.incident{border:1px solid var(--line);border-radius:18px;padding:18px;background:var(--surface)}
.incident p{margin:0;font-size:14px;color:var(--ink-2)}
/* tuning */
.baseline-head{display:flex;gap:12px;align-items:center;flex-wrap:wrap;margin-top:40px}
.baseline-head h3{margin:0;font:400 30px var(--display)}
.pending-grid{display:grid;gap:16px;grid-template-columns:1fr;margin-top:28px}
@media (min-width:860px){.pending-grid{grid-template-columns:1fr 1fr}}
.pending-card{border-style:dashed;box-shadow:none;background:transparent}
.pending-card p{margin:0;color:var(--ink-2);font-size:14.5px}
.note{font-size:14px;color:var(--ink-2);border-left:3px solid var(--accent-2);padding:2px 0 2px 12px;margin:12px 0}
.result.empty{color:var(--muted);font-style:italic}
.deliver-list{list-style:none;margin:0;padding:0;display:grid;gap:12px;grid-template-columns:repeat(auto-fit,minmax(250px,1fr))}
.deliver{border:1px dashed var(--line);border-radius:20px;padding:22px}
.deliver h3{margin:12px 0 6px}.deliver p{margin:0;color:var(--ink-2);font-size:14.5px}
/* tokens */
.big-numbers{display:grid;gap:12px;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));margin-bottom:16px}
.bn{border-radius:22px;padding:24px;background:var(--surface);border:1px solid var(--line);box-shadow:var(--shadow)}
.bn b{display:block;font:400 clamp(48px,7vw,72px)/1 var(--display);letter-spacing:-.02em}
.bn span{display:block;font-weight:600;margin:8px 0 6px}.bn p{margin:0;color:var(--ink-2);font-size:14px}
.measures dl{display:grid;gap:12px;margin:0;grid-template-columns:1fr}
@media (min-width:860px){.measures dl{grid-template-columns:repeat(3,1fr)}}
.measures dt{font-weight:650;color:var(--accent)}.measures dd{margin:4px 0 0;font-size:14px;color:var(--ink-2)}
.figure{margin:28px 0;background:var(--surface);border:1px solid var(--line);border-radius:22px;padding:20px;box-shadow:var(--shadow)}
.figure figcaption{display:flex;flex-direction:column;gap:2px;margin-bottom:10px}
.figure figcaption span{color:var(--muted);font-size:13.5px}
.chart-box{overflow-x:auto}
.chart{display:block;width:100%;min-width:560px;height:auto;font-family:var(--sans)}
.chart .grid{stroke:var(--grid);stroke-width:1}
.chart .tick{fill:var(--axis);font-size:11px;font-variant-numeric:tabular-nums}
.chart .rlabel{fill:var(--ink);font-size:13px;font-weight:600}
.chart .rsub{fill:var(--muted);font-size:11px}
.chart .vlabel{fill:var(--ink-2);font-size:12px;font-variant-numeric:tabular-nums;font-weight:600}
.chart .mark{outline:none;cursor:default}
.chart .mark:hover .bar,.chart .mark:focus .bar{filter:brightness(1.12);stroke:var(--ink);stroke-width:1}
.legend{display:flex;flex-wrap:wrap;gap:14px;list-style:none;margin:0 0 8px;padding:0;font-size:13px;color:var(--ink-2)}
.swatch{display:inline-block;width:12px;height:12px;border-radius:3px;margin-right:6px;vertical-align:-1px}
.tip{position:fixed;z-index:30;pointer-events:none;background:var(--ink);color:var(--bg);font-size:12.5px;padding:6px 10px;border-radius:8px;opacity:0;transition:opacity .12s;font-variant-numeric:tabular-nums;max-width:280px}
details{margin:18px 0;border:1px solid var(--line);border-radius:16px;background:var(--surface);padding:4px 16px}
details summary{cursor:pointer;padding:12px 0;font-weight:600}
details .table-wrap{box-shadow:none;border:0;margin:0 -16px 8px}
/* prompts */
.prompt-stats{display:grid;gap:12px;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));margin-bottom:24px}
.prompt-stats div{border:1px solid var(--line);border-radius:16px;padding:16px;background:var(--surface)}
.prompt-stats b{display:block;font:400 40px/1 var(--display)}.prompt-stats span{font-size:13.5px;color:var(--ink-2)}
.chat{list-style:none;margin:0;padding:0;display:grid;gap:12px;max-width:820px}
.msg{display:grid;gap:4px}
.msg time{font:12px var(--mono);color:var(--muted)}
.msg p{margin:0;background:linear-gradient(160deg,color-mix(in srgb,var(--accent) 14%,var(--surface)),color-mix(in srgb,var(--accent-2) 10%,var(--surface)));
  border:1px solid var(--line);border-radius:18px 18px 18px 6px;padding:12px 16px;font-size:15.5px;justify-self:start;max-width:100%}
.msg.attach p{background:var(--surface-2);font-style:italic;color:var(--muted)}
.prompt-role{background:var(--surface);border:1px solid var(--line);border-radius:18px;padding:18px;margin:12px 0}
.prompt-role pre{color:var(--ink-2)}
.prompt-role.common{border-color:color-mix(in srgb,var(--accent) 40%,var(--line))}
.roles{display:grid;gap:12px;grid-template-columns:1fr}
@media (min-width:980px){.roles{grid-template-columns:repeat(3,1fr)}.roles .prompt-role{margin:0}}
.units-grid{display:grid;gap:12px;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));margin-top:16px}
.unit-group{border:1px solid var(--line);border-radius:18px;padding:18px;background:var(--surface)}
.unit-group h4 a{font:12px var(--mono);margin-left:6px}
.unit-group ul{margin:0;padding-left:18px;display:grid;gap:6px;font-size:14px;color:var(--ink-2)}
footer.site{border-top:1px solid var(--line-2);margin-top:80px;padding:40px 0 64px;color:var(--muted);font-size:14px}
footer.site p{max-width:820px}
/* motion */
.js .reveal{opacity:0;transform:translateY(18px);transition:opacity .7s cubic-bezier(.2,.7,.2,1),transform .7s cubic-bezier(.2,.7,.2,1)}
.js .reveal.in{opacity:1;transform:none}
.js .hero-in{animation:rise .9s cubic-bezier(.2,.7,.2,1) both}
.js h1.hero-in{animation-delay:.06s}.js .lede.hero-in{animation-delay:.14s}.js .stats.hero-in{animation-delay:.22s}
@keyframes rise{from{opacity:0;transform:translateY(24px)}to{opacity:1;transform:none}}
.js .chart .bar{transform-box:fill-box;transform-origin:left center;transform:scaleX(0);transition:transform 1s cubic-bezier(.2,.7,.2,1) var(--d,0ms)}
.js .in .chart .bar{transform:scaleX(1)}
@media (prefers-reduced-motion:reduce){.js .reveal,.js .hero-in,.js .chart .bar{opacity:1!important;transform:none!important;animation:none!important;transition:none!important}html{scroll-behavior:auto}}
@media print{.bar,.theme{display:none}.js .reveal{opacity:1;transform:none}}
"""

JS = r"""
(function(){
  var root=document.documentElement;
  try{var saved=localStorage.getItem('theme');if(saved==='light'||saved==='dark')root.setAttribute('data-theme',saved);}catch(e){}
  var btn=document.querySelector('.theme');
  function current(){var t=root.getAttribute('data-theme');if(t)return t;return matchMedia('(prefers-color-scheme: dark)').matches?'dark':'light';}
  function label(){if(btn){var c=current();btn.textContent=c==='dark'?'☀':'☾';btn.setAttribute('aria-label','Switch to '+(c==='dark'?'light':'dark')+' theme');}}
  if(btn){btn.addEventListener('click',function(){var next=current()==='dark'?'light':'dark';root.setAttribute('data-theme',next);try{localStorage.setItem('theme',next);}catch(e){}label();});label();}
  var els=document.querySelectorAll('.reveal');
  if('IntersectionObserver' in window){
    var io=new IntersectionObserver(function(es){es.forEach(function(en){if(en.isIntersecting){en.target.classList.add('in');io.unobserve(en.target);}});},{rootMargin:'0px 0px -8% 0px',threshold:0.05});
    els.forEach(function(el){io.observe(el);});
  }else{els.forEach(function(el){el.classList.add('in');});}
  var tip=document.createElement('div');tip.className='tip';tip.setAttribute('role','status');document.body.appendChild(tip);
  function show(el,x,y){tip.textContent=el.getAttribute('data-tip');tip.style.left=Math.min(x+14,innerWidth-290)+'px';tip.style.top=(y-36)+'px';tip.style.opacity=1;}
  document.querySelectorAll('.chart .mark').forEach(function(m){
    m.addEventListener('mousemove',function(ev){show(m,ev.clientX,ev.clientY);});
    m.addEventListener('mouseleave',function(){tip.style.opacity=0;});
    m.addEventListener('focus',function(){var r=m.getBoundingClientRect();show(m,r.right,r.top);});
    m.addEventListener('blur',function(){tip.style.opacity=0;});
  });
})();
"""


def build():
    d = json.load(open(os.path.join(SITE, 'data.json'), encoding='utf-8'))
    tok = d['tokens']
    m = d['meta']
    nav = ''.join('<li><a href="#%s">%d&nbsp;%s</a></li>' % (e(t['id']), t['n'], e(t['title'].split(',')[0])) for t in d['timeline'])
    nav += '<li><a href="#tokens">Tokens</a></li><li><a href="#prompts">Prompts</a></li>'
    body = ''.join([
        '<a class="skip" href="#main">Skip to content</a>',
        '<div class="bar"><div class="wrap"><a class="brand" href="#top">Campfire <span>in F#</span></a>'
        '<nav aria-label="Sections"><ol>%s</ol></nav><button class="theme" type="button" aria-label="Switch theme"></button></div></div>' % nav,
        '<main id="main"><div class="wrap" id="top">',
        hero(d, tok),
        timeline(d),
        ch_muse(d, tok), ch_handover(d, tok), ch_build(d, tok), ch_tuning(d, tok), ch_delivered(d, tok),
        sec_tokens(d, tok), sec_prompts(d),
        '</div></main>',
        '<footer class="site"><div class="wrap"><p><b>Sources.</b> Chapter 1: Ren\'s <code>STATUS.md</code>, <code>AI-ORIENTATION.md</code>, '
        '<code>campfire-fs/Program.fs</code> and <code>bench_official.sh</code> from the hand-off zip. Chapter 2: the M4 logs '
        '<code>bench-mac-rebaseline.log</code> and <code>bench-mac-rebaseline-run1-badpost.log</code>. Chapters 3 to 5: '
        '<a href="plans/fsharp-port.md">plans/fsharp-port.md</a>, <a href="AGENTS.md">AGENTS.md</a>, <a href="README.md">README.md</a> (Known differences), '
        '<code>bench/results/</code>, the git history of <code>port</code>, and the workflow scripts in <a href="site/workflows/">site/workflows/</a>. '
        'Tokens and prompts: the session transcripts, via <code>site/collect_tokens.py</code>. %s</p>'
        '<p>Written by %s with Claude. Campfire, the Rust port, its parity harness and vectors are &copy; 37signals, LLC, MIT. '
        'This page is generated from <code>site/data.json</code> by <code>site/build.py</code>.</p></div></footer>' % (e(m['timezone_note']), e(m['author'])),
    ])
    page = ('<!doctype html>\n<html lang="en"><head><meta charset="utf-8">'
            '<meta name="viewport" content="width=device-width, initial-scale=1">'
            '<title>%s</title><meta name="description" content="%s">'
            '<meta name="color-scheme" content="light dark">'
            '<script>document.documentElement.className+=" js";try{var t=localStorage.getItem("theme");if(t==="light"||t==="dark")document.documentElement.setAttribute("data-theme",t)}catch(e){}</script>'
            '<link rel="preconnect" href="https://fonts.googleapis.com"><link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>'
            '<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Instrument+Serif:ital@0;1&display=swap">'
            '<style>%s</style></head><body>%s<script>%s</script></body></html>\n'
            % (e(m['title']), e(m['description']), CSS.strip(), body, JS.strip()))
    out = os.path.join(REPO, 'index.html')
    with open(out, 'w', encoding='utf-8') as f:
        f.write(page)
    print('wrote %s (%s bytes)' % (out, format(len(page.encode('utf-8')), ',')))


if __name__ == '__main__':
    build()
