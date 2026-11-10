"""Search decompiled Java sources for a regex; print file:line: text (max N hits). Text-only read."""
import os, re, sys
from concurrent.futures import ThreadPoolExecutor

root, pattern = sys.argv[1], re.compile(sys.argv[2])
limit = int(sys.argv[3]) if len(sys.argv) > 3 else 80


def scan(path):
    hits = []
    try:
        with open(path, encoding='utf-8', errors='replace') as f:
            for n, line in enumerate(f, 1):
                if pattern.search(line):
                    hits.append(f'{os.path.relpath(path, root)}:{n}: {line.strip()[:220]}')
    except OSError:
        pass
    return hits


files = [os.path.join(d, f) for d, _, fs in os.walk(root) for f in fs if f.endswith('.java')]
with ThreadPoolExecutor(16) as ex:
    hits = [h for hs in ex.map(scan, files) for h in hs]
for h in sorted(hits)[:limit]:
    print(h)
print(f'-- {len(hits)} hits')
