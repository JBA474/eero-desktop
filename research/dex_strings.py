"""Read the string pool of every classes*.dex inside an APK (as data only) and write unique strings to a file."""
import struct, sys, zipfile


def uleb(buf, off):
    result = shift = 0
    while True:
        b = buf[off]; off += 1
        result |= (b & 0x7F) << shift
        if b < 0x80:
            return result, off
        shift += 7


def dex_strings(buf):
    size, ids_off = struct.unpack_from('<II', buf, 0x38)
    for i in range(size):
        (data_off,) = struct.unpack_from('<I', buf, ids_off + 4 * i)
        _, p = uleb(buf, data_off)
        end = buf.index(b'\x00', p)
        yield buf[p:end].decode('utf-8', 'replace')


apk, out = sys.argv[1], sys.argv[2]
seen = set()
with zipfile.ZipFile(apk) as z:
    for name in sorted(n for n in z.namelist() if n.startswith('classes') and n.endswith('.dex')):
        seen.update(dex_strings(z.read(name)))
with open(out, 'w', encoding='utf-8') as f:
    for s in sorted(seen):
        f.write(s.replace('\n', '\\n') + '\n')
print(len(seen), 'unique strings')
