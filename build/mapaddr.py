import re, sys

path = sys.argv[1]
addrs = [int(a, 16) for a in sys.argv[2:]]

entries = []
with open(path, 'rb') as f:
    data = f.read().decode('latin1')

for m in re.finditer(r'\[j\|0x([0-9A-Fa-f]+) at 0x([0-9A-Fa-f]+)', data):
    entries.append((int(m.group(2), 16), int(m.group(1), 16)))

entries.sort()
print(f"parsed {len(entries)} method entries")

for a in addrs:
    best = None
    for addr, tok in entries:
        if addr <= a:
            best = (addr, tok)
        else:
            break
    if best:
        print(f"0x{a:X} -> [j|0x{best[1]:X} at 0x{best[0]:X} (+0x{a - best[0]:X})")
    else:
        print(f"0x{a:X} -> no method entry below")
