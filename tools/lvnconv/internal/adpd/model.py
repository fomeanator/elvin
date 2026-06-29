#!/usr/bin/env python3
"""
adpd model — reconstruct the articy object model from a `.adpd` Flow partition and
emit it in the **articy JSON-export shape** that lvnconv's `internal/articy`
front-end already consumes. The two-stage pipeline:

    .adpd  ->  articy model (this script)  ->  lvnconv convert -f articy  ->  .lvn

So the binary project converts through the SAME back-end as a real JSON export —
no duplicated flow logic. See ../../docs/articy-adpd-format.md.

Decoded per object: ordinal (propid 0x00), GUID (0x3a), text (0x200), speaker
caption (0x100); edges from connection lists (propid 0x02 = [src,dst,spin,tpin]).
We emit DialogueFragment models with OutputPins/Connections (Target = GUID), a
synthetic Dialogue wrapping the chosen start, Entity models for speakers, and the
Global Variables. Choices fall out of fragments with >1 outgoing connection.

Usage:
    python3 model.py "<Project>" [--start <ordinal>] [--max N] [-o export.json]
"""
import sys, os, glob, struct, re, json, html as _html
from collections import deque, Counter

TAGSZ = {0x12: 'str', 0xf6: 8, 0xf7: 8, 0xfa: 4, 0xfb: 4, 0xfc: 4, 0xfd: 4, 0xfe: 4, 0xee: 4, 0xef: 4}
GUID_RE = re.compile(r'^[0-9a-f]{8}-[0-9a-f]{4}-')


def parse_entries(d, start, end):
    o, out = start, []
    while o < end and o + 5 <= len(d):
        seq = struct.unpack_from('<H', d, o)[0]
        pid = struct.unpack_from('<H', d, o + 2)[0]
        tag = d[o + 4]; v = o + 5
        ts = TAGSZ.get(tag); ok = False
        if ts == 'str' and pid < 0x400 and seq < 0x600:
            ln = struct.unpack_from('<I', d, v)[0]
            if ln < 200000 and v + 4 + ln <= len(d):
                raw = d[v + 4:v + 4 + ln]
                try: val = raw.decode('utf-8')
                except UnicodeDecodeError: val = None
                out.append((seq, pid, tag, val)); o = v + 4 + ln; ok = True
        elif ts in (4, 8) and pid < 0x400 and seq < 0x600:
            val = struct.unpack_from('<d', d, v)[0] if ts == 8 else struct.unpack_from('<I', d, v)[0]
            out.append((seq, pid, tag, val)); o = v + ts; ok = True
        if not ok:
            o += 1
    return out


def strip_html(t):
    if not t or '<' not in t:
        return (t or '').strip()
    b = re.sub(r'(?is).*<body[^>]*>', '', t)
    b = re.sub(r'(?is)</body>.*', '', b)
    b = re.sub(r'(?s)<[^>]+>', ' ', b)
    return _html.unescape(re.sub(r'\s+', ' ', b)).strip()


def decode_flow(d):
    """→ (objs: ordinal→{guid,text,speaker}, edges: list[(src,dst,sp,tp)])."""
    idx = struct.unpack_from('<Q', d, 8)[0]
    E = parse_entries(d, 24, idx)

    edges, i = [], 0
    while i < len(E):
        if E[i][1] == 0x02 and E[i][2] == 0xfe:
            lst, j = [], i
            while j < len(E) and E[j][1] == 0x02 and E[j][2] == 0xfe:
                lst.append(E[j][3]); j += 1
            if len(lst) == 4:
                edges.append(tuple(lst))
            i = j
        else:
            i += 1

    # Group into fragments keyed by the SAME ordinal the connection lists use:
    # a fragment ends with its GUID (0x3a); its ordinal is the modal parent (0x0c)
    # of the pin entries that precede the GUID (consistent with edge endpoints).
    objs, cur, acc = {}, {}, []
    for seq, pid, tag, val in E:
        if pid == 0x0c and tag == 0xfe:
            acc.append(val)
        elif pid == 0x100 and tag == 0x12 and val:
            cur['speaker'] = val
        elif pid == 0x200 and tag == 0x12:
            cur['text'] = strip_html(val)
        elif pid == 0x3a and tag == 0x12 and val and GUID_RE.match(val):
            if acc:
                ordn = Counter(acc).most_common(1)[0][0]
                objs[ordn] = {'guid': val, 'text': cur.get('text', ''),
                              'speaker': cur.get('speaker', '')}
            cur, acc = {}, []
    return objs, edges


def global_vars(project_dir):
    """Namespaced variable names from the Global_Variables partition."""
    hits = glob.glob(os.path.join(project_dir, '**', '*Global_Variables*.adpd'), recursive=True)
    if not hits:
        return []
    d = open(hits[0], 'rb').read()
    idx = struct.unpack_from('<Q', d, 8)[0]
    E = parse_entries(d, 24, idx)
    names = [v for _s, p, t, v in E if t == 0x12 and isinstance(v, str)
             and re.fullmatch(r'[A-Za-z_]\w*', v)]
    # crude: treat distinct CamelCase identifiers as a flat namespace "GV"
    seen, vs = set(), []
    for n in names:
        if n in seen or n in ('Articy',):
            continue
        seen.add(n); vs.append({'Variable': n, 'Type': 'Boolean', 'Value': 'false'})
    return [{'Namespace': 'GV', 'Variables': vs}] if vs else []


def build_export(objs, edges, start, maxn, gvars):
    succ = {}
    for s, t, _sp, _tp in edges:
        succ.setdefault(s, []).append(t)

    DLG = 'dialogue-root-0000-0000-000000000000'

    def nid(o):                                      # stable node id for any ordinal
        return objs[o]['guid'] if o in objs else f'node-{o:08d}-0000-0000-000000000000'

    # reachable subgraph from start over the FULL edge graph (through hubs too)
    reach, seen, q = [], set(), deque([start])
    while q and len(reach) < maxn:
        x = q.popleft()
        if x in seen:
            continue
        seen.add(x); reach.append(x)
        for y in succ.get(x, []):
            q.append(y)

    speakers, models = {}, []
    for o in reach:
        outs = [t for t in succ.get(o, []) if t in seen]
        conns = [{'Target': nid(t)} for t in outs] or [{'Target': DLG}]  # leaf → exit
        if o in objs:                                # a DialogueFragment (has a line)
            ob = objs[o]; sp = ob['speaker'] or ''
            if sp:
                speakers[sp] = True
            models.append({'Type': 'DialogueFragment', 'Properties': {
                'Id': nid(o), 'Text': ob['text'], 'MenuText': ob['text'][:80],
                'Speaker': sp,
                'InputPins': [{'Text': '', 'Connections': []}],
                'OutputPins': [{'Text': '', 'Connections': conns}],
            }})
        else:                                        # a Hub / container — routes flow
            models.append({'Type': 'Hub', 'Properties': {
                'Id': nid(o), 'DisplayName': '',
                'InputPins': [{'Text': '', 'Connections': []}],
                'OutputPins': [{'Text': '', 'Connections': conns}],
            }})
    for sp in speakers:
        models.append({'Type': 'Entity', 'Properties': {'Id': sp, 'DisplayName': sp}})
    models.append({'Type': 'Dialogue', 'Properties': {
        'Id': DLG, 'TechnicalName': 'chapter', 'DisplayName': 'chapter',
        'InputPins': [{'Text': '', 'Connections': [{'Target': nid(start)}]}],
        'OutputPins': [{'Text': '', 'Connections': []}],
    }})
    return {'GlobalVariables': gvars, 'Packages': [{'Models': models}]}


def find_flow(path):
    if os.path.isdir(path):
        hits = glob.glob(os.path.join(path, '**', '*Flow*.adpd'), recursive=True)
        if not hits:
            sys.exit('no Flow partition under ' + path)
        return hits[0], path
    return path, os.path.dirname(os.path.dirname(path))


def main():
    args = sys.argv[1:]
    if not args:
        sys.exit(__doc__)
    path = args[0]
    start = None; maxn = 400; out = None
    if '--start' in args: start = int(args[args.index('--start') + 1])
    if '--max' in args: maxn = int(args[args.index('--max') + 1])
    if '-o' in args: out = args[args.index('-o') + 1]

    flow, proj = find_flow(path)
    d = open(flow, 'rb').read()
    objs, edges = decode_flow(d)

    if start is None:
        # default start: the source of the first connection whose src has text
        for s, _t, _sp, _tp in edges:
            if s in objs and objs[s]['text']:
                start = s; break
    gvars = global_vars(proj)
    export = build_export(objs, edges, start, maxn, gvars)
    js = json.dumps(export, ensure_ascii=False, indent=1)
    if out:
        open(out, 'w').write(js)
        n = len(export['Packages'][0]['Models'])
        print(f'wrote {out}: {n} models, start ordinal {start}, {len(gvars and gvars[0]["Variables"] or [])} vars')
    else:
        print(js)


if __name__ == '__main__':
    main()
