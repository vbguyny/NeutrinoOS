import re

lines = open('/tmp/m2-serial.log', 'rb').read().decode('latin1').splitlines()
ents = []
for l in lines:
    m = re.search(r'\[j\|0x([0-9A-Fa-f]+) at 0x([0-9A-Fa-f]+)', l)
    if m:
        ents.append((int(m.group(2), 16), m.group(1)))
        continue
    m = re.search(r'\[j\|0x([0-9A-Fa-f]+) at 0x([0-9A-Fa-f]+)\s*$', l)
    if m:
        ents.append((int(m.group(2), 16), m.group(1)))

print('entries:', len(ents))

def find(addr):
    best = None
    for a, t in ents:
        if a <= addr and (best is None or a > best[0]):
            best = (a, t)
    return best

for name, target in (
    ('crash-rip', 0x2010A0031),
    ('caller-ret', 0x200FBDB9B),
    ('methodspec-native', 0x2010A50A0),
):
    b = find(target)
    if b:
        print(f'{name} {hex(target)} -> method at {hex(b[0])} tok=0x{b[1]} (offset {hex(target - b[0])})')
    else:
        print(f'{name} {hex(target)} -> no entry at or below')

# Also list every method entry in the crash chunk 0x2010A00xx (first 0x200 bytes)
print('--- methods in 0x2010A0000..0x2010A0200 ---')
for a, t in sorted(ents):
    if 0x2010A0000 <= a < 0x2010A0200:
        print(hex(a), 'tok=0x' + t)
