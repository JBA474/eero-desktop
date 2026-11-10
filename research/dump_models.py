"""Dump Gson model fields (JSON name -> Java type) for named classes in decompiled sources. Text-only read.
JSON name = @SerializedName value (string literal or resolved constant) else the Java field name (Gson IDENTITY policy)."""
import os, re, sys

root, out = sys.argv[1], sys.argv[2]
names = sys.argv[3].split(',')
FIELD = re.compile(r'^\s*(?:private|public|protected)\s+(?!static)(?:final\s+)?(?:transient\s+)?([\w.<>,? \[\]]+?)\s+(\w+);')
SER_LIT = re.compile(r'@SerializedName\((?:alternate\s*=\s*\{[^}]*\},\s*)?(?:value\s*=\s*)?"([^"]+)"')
SER_REF = re.compile(r'@SerializedName\((?:alternate\s*=\s*\{[^}]*\},\s*)?(?:value\s*=\s*)?([\w.]+)\)')
CONST = re.compile(r'(?:public|private)?\s*static final String (\w+) = "([^"]*)";')
CLASS = re.compile(r'^\s*(?:public |private |protected )?(?:static )?(?:final )?(?:/\* \w+ \*/ )?(?:abstract )?(?:class|enum) (\w+)')

index = {}
for d, _, fs in os.walk(os.path.join(root, 'com', 'eero')):
    for f in fs:
        if f.endswith('.java'):
            index.setdefault(f[:-5], []).append(os.path.join(d, f))

const_cache = {}


def resolve(ref):
    cls, _, name = ref.rpartition('.')
    cls = cls.split('.')[-1] if cls else ''
    for path in index.get(cls, []):
        if path not in const_cache:
            const_cache[path] = dict(CONST.findall(open(path, encoding='utf-8', errors='replace').read()))
        if name in const_cache[path]:
            return const_cache[path][name]
    return '?' + ref


with open(out, 'w', encoding='utf-8') as o:
    for name in names:
        for path in index.get(name, []):
            lines = open(path, encoding='utf-8', errors='replace').read().splitlines()
            o.write(f'\n### {name}  ({os.path.relpath(path, root)})\n')
            ann = None
            for ln in lines:
                s = ln.strip()
                cm = CLASS.match(ln)
                if cm and cm.group(1) != name:
                    o.write(f'  -- nested {cm.group(1)}\n')
                if s.startswith('@SerializedName'):
                    m = SER_LIT.search(s)
                    ann = m.group(1) if m else (resolve(SER_REF.search(s).group(1)) if SER_REF.search(s) else '?')
                    continue
                fm = FIELD.match(ln)
                if fm:
                    o.write(f'  {(ann or fm.group(2)):34} {fm.group(1).strip()}\n')
                    ann = None
                elif s and not s.startswith('@'):
                    ann = None
                if name.startswith('enum:'):
                    pass
print('written', out)
