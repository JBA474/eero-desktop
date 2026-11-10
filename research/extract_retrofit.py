"""Scan decompiled Java sources for Retrofit service interfaces and dump each endpoint:
HTTP method, path, Java method signature, and source file. Reads files as text only."""
import os, re, sys
from concurrent.futures import ThreadPoolExecutor

root, out = sys.argv[1], sys.argv[2]
HTTP = re.compile(r'^@(GET|POST|PUT|DELETE|PATCH|HTTP)(?:\((.*?)\))?\s*$')
SIG = re.compile(r'^\s*(?:public\s+)?(?:abstract\s+)?(?:/\*.*?\*/\s*)?(.+?)\s+(\w+)\((.*)\);?\s*$')


def scan(path):
    try:
        with open(path, encoding='utf-8', errors='replace') as f:
            text = f.read()
    except OSError:
        return []
    if 'retrofit2.http' not in text:
        return []
    lines = text.splitlines()
    rel = os.path.relpath(path, root)
    res = []
    i = 0
    while i < len(lines):
        m = HTTP.search(lines[i].strip())
        if m:
            verb, arg = m.group(1), (m.group(2) or '<@Url dynamic>')
            annos = [lines[i].strip()]
            j = i + 1
            while j < len(lines) and lines[j].strip().startswith('@'):
                annos.append(lines[j].strip()); j += 1
            sig = lines[j].strip() if j < len(lines) else ''
            res.append((verb, arg, ' '.join(annos[1:]), sig, rel))
            i = j
        i += 1
    return res


files = []
for d, _, fs in os.walk(root):
    files.extend(os.path.join(d, f) for f in fs if f.endswith('.java'))
with ThreadPoolExecutor(16) as ex:
    results = [r for rs in ex.map(scan, files) for r in rs]
results.sort(key=lambda r: (r[4], r[1]))
with open(out, 'w', encoding='utf-8') as f:
    cur = None
    for verb, arg, extra, sig, rel in results:
        if rel != cur:
            f.write(f'\n### {rel}\n'); cur = rel
        f.write(f'{verb:6} {arg}\n')
        if extra:
            f.write(f'       {extra}\n')
        f.write(f'       {sig}\n')
print(len(files), 'files scanned;', len(results), 'endpoints in', len({r[4] for r in results}), 'interfaces')
