#!/usr/bin/env python3
"""
adpd extract — pull the readable content out of an articy:draft `.adpd` partition.

articy:draft stores its project as proprietary binary `ADPD8` partitions. There is
no public spec. This reference extractor recovers what is *reliably* recoverable
from the bytes alone — the dialogue text, speakers, variables and instructions —
without articy's (unshipped) internal object schema. See ./FINDINGS.md and
../../docs/articy-adpd-format.md for the format write-up.

What it does NOT do: reconstruct the branching flow graph (pin connections /
choices / conditions). Those are encoded against articy's hardcoded object model
which is not present in the project files; export JSON from articy for that.

Usage:
    python3 extract.py "<Project>/Partitions/'Flow'-TypedPartition(...).adpd"
    python3 extract.py <project-dir>        # finds the Flow partition itself
"""
import sys, os, glob, struct, re, html as htmlmod

GUID = re.compile(r'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$')


def read_string(buf, o):
    """An ADPD string: tag 0x12, uint32 LE length, then `length` UTF-8 bytes."""
    if o + 5 <= len(buf) and buf[o] == 0x12:
        ln = struct.unpack_from('<I', buf, o + 1)[0]
        if 0 < ln < 1 << 20 and o + 5 + ln <= len(buf):
            try:
                return buf[o + 5:o + 5 + ln].decode('utf-8'), o + 5 + ln
            except UnicodeDecodeError:
                pass
    return None, o


def tokenize(buf):
    """Walk the body, yielding every embedded (offset, string) in order."""
    o, out = 24, []
    while o < len(buf):
        s, no = read_string(buf, o)
        if s is not None:
            out.append((o, s)); o = no
        else:
            o += 1
    return out


def strip_html(t):
    if '<' not in t:
        return t.strip()
    body = re.sub(r'(?is).*<body[^>]*>', '', t)
    body = re.sub(r'(?is)</body>.*', '', body)
    body = re.sub(r'(?s)<[^>]+>', ' ', body)
    return htmlmod.unescape(re.sub(r'\s+', ' ', body)).strip()


def extract(buf):
    """Recover dialogue records (speaker + text + own id) and instructions."""
    toks = tokenize(buf)
    records, i = [], 0
    while i < len(toks):
        _, s = toks[i]
        # A DialogueFragment serialises: ... DisplayNameMultiLanguageText <caption>
        # ... Entity ... <fragment-guid> ... <html-text>
        if s == 'DisplayNameMultiLanguageText' and i + 1 < len(toks):
            caption = toks[i + 1][1]
            k = i + 2
            while k < len(toks) and k < i + 6 and toks[k][1] != 'Entity':
                k += 1
            if k < len(toks) and toks[k][1] == 'Entity' and k + 2 < len(toks) \
                    and GUID.match(toks[k + 1][1]):
                records.append({
                    'id': toks[k + 1][1],
                    'speaker': caption,
                    'text': strip_html(toks[k + 2][1]),
                })
                i = k + 2
                continue
        i += 1
    instr = [s for _, s in toks
             if ';' in s and '<' not in s and len(s) < 200
             and re.search(r'[A-Za-z_][\w.]*\s*[-+]?=', s)]
    return records, instr


def find_flow(path):
    if os.path.isdir(path):
        hits = glob.glob(os.path.join(path, '**', '*Flow*.adpd'), recursive=True)
        if not hits:
            sys.exit('no Flow partition found under ' + path)
        return hits[0]
    return path


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    path = find_flow(sys.argv[1])
    buf = open(path, 'rb').read()
    if buf[:5] != b'ADPD8':
        sys.exit('not an ADPD8 partition: ' + path)
    records, instr = extract(buf)
    from collections import Counter
    print(f'# {os.path.basename(path)}')
    print(f'# {len(records)} dialogue lines, {len(instr)} instructions')
    print(f'# speakers: {Counter(r["speaker"] for r in records).most_common(10)}')
    print()
    for r in records:
        if r['text']:
            print(f'{r["speaker"]}: {r["text"]}')


if __name__ == '__main__':
    main()
