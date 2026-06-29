#!/usr/bin/env python3
"""
adpd decode — recover the FULL flow graph (incl. branching) from an articy:draft
`.adpd` Flow partition. The branching is encoded; this reads it.

The `ADPD8` body is a flat stream of typed property entries
    <seq:uint16> <propid:uint16> <tag:uint8> <value>
grouped into objects that carry small local numeric ordinals. Connections are
ordinary objects whose `propid 0x02` is a 4-element ordinal list:
    [ source_fragment, target_fragment, source_pin, target_pin ]
i.e. one directed edge of the flow graph. Reconstructing every 0x02 list gives
the complete graph — linear runs (out-degree 1) and choices (out-degree ≥ 2).

See ../../docs/articy-adpd-format.md for the full format write-up.

Usage:
    python3 decode.py "<Project>"        # finds the Flow partition
    python3 decode.py <Flow.adpd> [--dot out.dot]
"""
import sys, os, glob, struct, re, html as _html

# value tags → size ('str' = uint32 length prefix + bytes, 8 = f64, 4 = uint32)
TAGSZ = {0x12: 'str', 0xf6: 8, 0xf7: 8, 0xfa: 4, 0xfb: 4, 0xfc: 4, 0xfd: 4, 0xfe: 4, 0xee: 4, 0xef: 4}

# propids that matter for the flow graph (the rest are layout/style/bookkeeping)
P_ID, P_TEXT, P_CAPTION = 0x3a, 0x200, 0x100
P_PARENT, P_SELF, P_CONN = 0x0c, 0x39, 0x02
GUID_RE = re.compile(r'^[0-9a-f]{8}-[0-9a-f]{4}-')


def parse_entries(d, start, end):
    """Flat entry stream. Bytes that don't form a plausible entry (object/array
    headers) are skipped one at a time — the entries themselves are unambiguous."""
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
                out.append((o, seq, pid, tag, val)); o = v + 4 + ln; ok = True
        elif ts in (4, 8) and pid < 0x400 and seq < 0x600:
            val = struct.unpack_from('<d', d, v)[0] if ts == 8 else struct.unpack_from('<I', d, v)[0]
            out.append((o, seq, pid, tag, val)); o = v + ts; ok = True
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


def decode(d):
    """→ (fragments: ordinal→{guid,caption,text}, edges: list[(src,dst,spin,tpin)])."""
    idx = struct.unpack_from('<Q', d, 8)[0]
    E = parse_entries(d, 24, idx)

    # connections: consecutive propid-0x02 ref entries (seq 1..4) = one edge
    edges, i = [], 0
    while i < len(E):
        if E[i][2] == P_CONN and E[i][3] == 0xfe:
            lst, j = [], i
            while j < len(E) and E[j][2] == P_CONN and E[j][3] == 0xfe:
                lst.append(E[j][4]); j += 1
            if len(lst) == 4:
                edges.append(tuple(lst))
            i = j
        else:
            i += 1

    # fragments: each ends with its GUID (propid 0x3a). Its ordinal is the modal
    # parent (0x0c) of the pins that precede the GUID.
    from collections import Counter
    fragments, acc = {}, []
    pending = {}
    for o, seq, pid, tag, val in E:
        if pid == P_PARENT and tag == 0xfe:
            acc.append(val)
        elif pid == P_CAPTION and tag == 0x12 and val:
            pending['caption'] = val
        elif pid == P_TEXT and tag == 0x12:
            pending['text'] = strip_html(val)
        elif pid == P_ID and tag == 0x12 and val and GUID_RE.match(val):
            if acc:
                ordn = Counter(acc).most_common(1)[0][0]
                fragments[ordn] = {'guid': val, 'caption': pending.get('caption', ''),
                                   'text': pending.get('text', '')}
            acc, pending = [], {}
    return fragments, edges


def build_graph(edges):
    g = {}
    for s, t, _sp, _tp in edges:
        g.setdefault(s, []).append(t)
    return g


def find_flow(path):
    if os.path.isdir(path):
        hits = glob.glob(os.path.join(path, '**', '*Flow*.adpd'), recursive=True)
        if not hits:
            sys.exit('no Flow partition under ' + path)
        return hits[0]
    return path


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    d = open(find_flow(sys.argv[1]), 'rb').read()
    if d[:5] != b'ADPD8':
        sys.exit('not an ADPD8 partition')
    fragments, edges = decode(d)
    g = build_graph(edges)
    from collections import Counter
    nodes = {n for e in edges for n in (e[0], e[1])}
    outdeg = Counter(len(set(v)) for v in g.values())
    choices = sum(c for k, c in outdeg.items() if k >= 2)
    print(f'# nodes={len(nodes)} edges={len(edges)} fragments={len(fragments)}')
    print(f'# out-degree: {dict(sorted(outdeg.items()))}  choice-nodes={choices}')

    def label(o):
        fr = fragments.get(o)
        return f'{fr["caption"]}: {fr["text"][:70]}' if fr else f'<node {o}>'

    # show a couple of branches as proof
    branches = [(s, sorted(set(v))) for s, v in g.items() if len(set(v)) >= 2]
    print(f'\n# {len(branches)} branch points; first 3:')
    for s, outs in branches[:3]:
        print(f'  FROM {label(s)}')
        for t in outs:
            print(f'    -> {label(t)}')


if __name__ == '__main__':
    main()
