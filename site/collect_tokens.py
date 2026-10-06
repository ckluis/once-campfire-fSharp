#!/usr/bin/env python3
"""Regenerate the token figures in site/data.json from the Claude Code transcripts.

Reads, for the session named in data.json's token_config.session_id:
  - the main session transcript   ~/.claude/projects/*/<session>.jsonl
  - every workflow agent           ~/.claude/projects/*/<session>/subagents/workflows/wf_*/agent-*.jsonl
  - each workflow's journal        .../wf_*/journal.jsonl (labels, verifier findings, run state)
  - other subagents of the session ~/.claude/projects/*/<session>/subagents/agent-*.jsonl
  - the workflow scripts           ~/.claude/projects/*/<session>/workflows/scripts/<name>-wf_<id>.js
and the commits of this repository (git log), then replaces data.json's "tokens" and
"commits" keys. Everything else in data.json is left as it is, so re-running is safe.

Counting rule: Claude Code writes one assistant message as several JSONL lines (one per
content block) that repeat the same usage object, so usage is summed once per API
message id (the last line of each id, which carries the final output count).
Python 3 standard library only.
"""
import collections
import glob
import json
import os
import re
import subprocess
import sys

SITE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(SITE)
DATA = os.path.join(SITE, 'data.json')
PROJECTS = os.path.expanduser('~/.claude/projects')
KEYS = ('input_tokens', 'cache_creation_input_tokens', 'cache_read_input_tokens', 'output_tokens')
SHORT = {'input_tokens': 'input', 'cache_creation_input_tokens': 'cache_write',
         'cache_read_input_tokens': 'cache_read', 'output_tokens': 'output'}


def zero():
    return {'input': 0, 'cache_write': 0, 'cache_read': 0, 'output': 0, 'calls': 0}


def add(acc, u):
    for k in ('input', 'cache_write', 'cache_read', 'output', 'calls'):
        acc[k] += u.get(k, 0)
    return acc


def finish(acc):
    acc['processed'] = acc['input'] + acc['cache_write'] + acc['cache_read'] + acc['output']
    acc['fresh'] = acc['input'] + acc['cache_write'] + acc['output']
    return acc


def read_transcript(path):
    """Return (calls, first_ts, last_ts): calls is a list of dicts, one per API message id."""
    per_id = collections.OrderedDict()
    first = last = None
    with open(path, encoding='utf-8') as f:
        for line in f:
            try:
                d = json.loads(line)
            except ValueError:
                continue
            ts = d.get('timestamp')
            if ts:
                first = first or ts
                last = ts
            if d.get('type') != 'assistant':
                continue
            m = d.get('message') or {}
            u = m.get('usage')
            if not u:
                continue
            key = m.get('id') or d.get('requestId') or d.get('uuid')
            rec = {SHORT[k]: int(u.get(k) or 0) for k in KEYS}
            rec['model'] = m.get('model') or 'unknown'
            rec['ts'] = ts
            per_id[key] = rec  # last line of an id wins
    calls = list(per_id.values())
    return calls, first, last


def sum_calls(calls):
    acc = zero()
    for c in calls:
        add(acc, dict(c, calls=1))
    return finish(acc)


def model_name(m):
    return {'claude-opus-5-5': 'Opus 5.5', 'claude-sonnet-5-5': 'Sonnet 5.5'}.get(m, m)


def journal_info(path):
    """Labels, run state and verifier findings by agent id."""
    info = {'state': 'unknown', 'agents': {}}
    if not os.path.exists(path):
        return info
    results = 0
    started = 0
    with open(path, encoding='utf-8') as f:
        for line in f:
            try:
                d = json.loads(line)
            except ValueError:
                continue
            aid = d.get('agentId')
            if d.get('type') == 'started' and aid:
                started += 1
                info['agents'].setdefault(aid, {})['label'] = d.get('label')
            if d.get('type') == 'result' and aid:
                results += 1
                r = d.get('result')
                if isinstance(r, str):
                    try:
                        r = json.loads(r)
                    except ValueError:
                        r = None
                a = info['agents'].setdefault(aid, {})
                a['finished'] = True
                if isinstance(r, dict) and 'findings' in r:
                    c = collections.Counter(x.get('severity') for x in r.get('findings') or [])
                    a['findings'] = {s: c.get(s, 0) for s in ('critical', 'major', 'minor')}
    info['state'] = 'done' if started and results == started else ('running' if started else 'unknown')
    return info


def git_commits():
    try:
        out = subprocess.run(
            ['git', '-C', REPO, 'log', '--format=%H %cI', 'HEAD', '--', '.', ':(exclude)site', ':(exclude)index.html'],
            capture_output=True, text=True, check=True).stdout
    except (OSError, subprocess.CalledProcessError):
        return []
    rows = []
    for line in out.splitlines():
        h, iso = line.split(' ', 1)
        rows.append((h, iso))
    return rows


def to_utc(iso):
    # git's %cI has an offset; transcripts use Z. Normalise both to comparable UTC strings.
    from datetime import datetime, timezone
    if iso.endswith('Z'):
        iso = iso[:-1] + '+00:00'
    return datetime.fromisoformat(iso).astimezone(timezone.utc).strftime('%Y-%m-%dT%H:%M:%S')


def main():
    data = json.load(open(DATA, encoding='utf-8'))
    cfg = data['token_config']
    sid = cfg['session_id']
    phase_titles = cfg['phase_titles']

    main_paths = glob.glob(os.path.join(PROJECTS, '*', sid + '.jsonl'))
    if not main_paths:
        sys.exit('main transcript for session %s not found under %s' % (sid, PROJECTS))
    session_dirs = glob.glob(os.path.join(PROJECTS, '*', sid))

    # workflow id -> script name
    scripts = {}
    for d in session_dirs:
        for p in glob.glob(os.path.join(d, 'workflows', 'scripts', '*.js')):
            m = re.match(r'(.+)-(wf_[0-9a-f-]+)\.js$', os.path.basename(p))
            if m:
                scripts[m.group(2)] = m.group(1)

    # ---- main session
    main_calls, main_first, main_last = [], None, None
    for p in main_paths:
        calls, f, l = read_transcript(p)
        main_calls += calls
        main_first = min(x for x in (main_first, f) if x) if f else main_first
        main_last = max(x for x in (main_last, l) if x) if l else main_last
    windows = cfg['main_windows']
    win_tot = []
    for i, w in enumerate(windows):
        start = w['from']
        end = windows[i + 1]['from'] if i + 1 < len(windows) else '9999'
        cs = [c for c in main_calls if c['ts'] and start <= c['ts'] < end]
        win_tot.append(dict(label=w['label'], chapter=w['chapter'], **{'from': start}, totals=sum_calls(cs)))
    main_models = collections.Counter(model_name(c['model']) for c in main_calls)
    main = {'totals': sum_calls(main_calls), 'models': dict(main_models),
            'first': main_first, 'last': main_last, 'windows': win_tot}

    # ---- workflows
    workflows = []
    all_agents = []
    for d in session_dirs:
        for wdir in sorted(glob.glob(os.path.join(d, 'subagents', 'workflows', 'wf_*'))):
            wid = os.path.basename(wdir)
            name = scripts.get(wid, wid)
            pm = re.search(r'phase(\d+)$', name)
            key = 'phase' + pm.group(1) if pm else ('phase5b' if name.endswith('baseline') else wid)
            jinfo = journal_info(os.path.join(wdir, 'journal.jsonl'))
            agents = []
            for ap in sorted(glob.glob(os.path.join(wdir, 'agent-*.jsonl'))):
                aid = os.path.basename(ap)[len('agent-'):-len('.jsonl')]
                meta_p = ap[:-len('.jsonl')] + '.meta.json'
                meta = json.load(open(meta_p)) if os.path.exists(meta_p) else {}
                calls, f, l = read_transcript(ap)
                if not calls:
                    continue
                label = meta.get('description') or (jinfo['agents'].get(aid) or {}).get('label') or aid
                role = label.split(':')[0]
                models = collections.Counter(model_name(c['model']) for c in calls)
                lastc = calls[-1]
                ag = {
                    'id': aid, 'label': label, 'role': role,
                    'model': models.most_common(1)[0][0],
                    'first': f, 'last': l,
                    'totals': sum_calls(calls),
                    # what the harness reports per agent: the context of the agent's final API call
                    'final_context': lastc['input'] + lastc['cache_write'] + lastc['cache_read'],
                    'final_output': lastc['output'],
                    'finished': bool((jinfo['agents'].get(aid) or {}).get('finished')),
                }
                fnd = (jinfo['agents'].get(aid) or {}).get('findings')
                if fnd is not None:
                    ag['findings'] = fnd
                agents.append(ag)
            agents.sort(key=lambda a: a['first'] or '')
            tot = zero()
            for a in agents:
                add(tot, a['totals'])
            finish(tot)
            first = min((a['first'] for a in agents if a['first']), default=None)
            last = max((a['last'] for a in agents if a['last']), default=None)
            workflows.append({
                'id': wid, 'script': name + '.js', 'key': key,
                'title': phase_titles.get(key, name),
                'state': jinfo['state'],
                'agents': agents, 'totals': tot,
                'final_context_sum': sum(a['final_context'] for a in agents),
                'first': first, 'last': last,
                'harness': cfg.get('harness', {}).get(key),
            })
            all_agents += [dict(a, workflow=key) for a in agents]
    workflows.sort(key=lambda w: w['first'] or '')

    # ---- other subagents of the session (not part of a workflow)
    others = []
    for d in session_dirs:
        for ap in sorted(glob.glob(os.path.join(d, 'subagents', 'agent-*.jsonl'))):
            meta_p = ap[:-len('.jsonl')] + '.meta.json'
            meta = json.load(open(meta_p)) if os.path.exists(meta_p) else {}
            calls, f, l = read_transcript(ap)
            if not calls:
                continue
            models = collections.Counter(model_name(c['model']) for c in calls)
            others.append({'label': meta.get('description') or os.path.basename(ap),
                           'model': models.most_common(1)[0][0], 'first': f, 'last': l,
                           'totals': sum_calls(calls)})

    # ---- roll-ups
    by_role = collections.OrderedDict()
    for role in ('build', 'verify', 'fix', 'reverify', 'bench', 'review'):
        rs = [a for a in all_agents if a['role'] == role]
        if rs:
            t = zero()
            for a in rs:
                add(t, a['totals'])
            by_role[role] = {'agents': len(rs), 'totals': finish(t)}
    by_model = collections.OrderedDict()
    for who, model, t in ([('orchestrator', m, None) for m in main_models] +
                          [('agent', a['model'], a['totals']) for a in all_agents] +
                          [('agent', o['model'], o['totals']) for o in others]):
        by_model.setdefault(model, {'agents': 0, 'totals': zero()})
    for c in main_calls:
        add(by_model[model_name(c['model'])]['totals'], dict(c, calls=1))
    for a in all_agents + others:
        by_model[a['model']]['agents'] += 1
        add(by_model[a['model']]['totals'], a['totals'])
    for v in by_model.values():
        finish(v['totals'])

    grand = zero()
    add(grand, main['totals'])
    for w in workflows:
        add(grand, w['totals'])
    for o in others:
        add(grand, o['totals'])
    finish(grand)

    as_of = max(x for x in [main_last] + [w['last'] for w in workflows] + [o['last'] for o in others] if x)

    data['tokens'] = {
        'as_of': as_of,
        'rule': 'usage summed once per API message id; processed = input + cache writes + cache reads + output; fresh = input + cache writes + output',
        'grand': grand,
        'main': main,
        'workflows': workflows,
        'others': others,
        'by_role': by_role,
        'by_model': by_model,
        'workflow_agents': len(all_agents),
    }

    # ---- commits per phase window (port branch history, site/ excluded)
    commits = git_commits()
    bounds = [(w['key'], w['first']) for w in workflows if w['first']]
    per = collections.OrderedDict([('phase0', 0)] + [(k, 0) for k, _ in bounds] + [('after', 0)])
    for h, iso in commits:
        t = to_utc(iso)
        k = 'phase0'
        for key, start in bounds:
            if t >= start[:19]:
                k = key
        per[k] += 1
    data['commits'] = {'total': len(commits), 'by_phase_window': per}

    with open(DATA, 'w', encoding='utf-8') as f:
        json.dump(data, f, indent=2, ensure_ascii=False)
        f.write('\n')
    print('tokens: grand processed %s, fresh %s; %d workflows, %d workflow agents; as of %s' % (
        format(grand['processed'], ','), format(grand['fresh'], ','), len(workflows), len(all_agents), as_of))
    for w in workflows:
        h = w['harness']
        print('  %-8s %-24s agents=%d processed=%s fresh=%s final_context=%s harness=%s' % (
            w['key'], w['script'], len(w['agents']), format(w['totals']['processed'], ','),
            format(w['totals']['fresh'], ','), format(w['final_context_sum'], ','),
            format(h['tokens'], ',') if h else '-'))


if __name__ == '__main__':
    main()
