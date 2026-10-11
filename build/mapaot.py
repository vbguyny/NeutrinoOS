import subprocess, sys

elf = sys.argv[1]
addrs = [int(a, 16) for a in sys.argv[2:]]

out = subprocess.run(['nm', elf], capture_output=True, text=True).stdout
syms = []
for line in out.splitlines():
    parts = line.split()
    if len(parts) >= 3:
        try:
            val = int(parts[0], 16)
        except ValueError:
            continue
        syms.append((val, parts[2]))
syms.sort()

for a in addrs:
    best = None
    prev = None
    for val, name in syms:
        if val <= a:
            prev, best = best, (val, name)
        else:
            break
    if best:
        print(f"0x{a:X} -> {best[1]} (0x{best[0]:X}) +0x{a-best[0]:X}")
        if prev:
            print(f"   (prev: {prev[1]} 0x{prev[0]:X})")
    else:
        print(f"0x{a:X} -> no symbol below")
