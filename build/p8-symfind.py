#!/usr/bin/env python3
# Find nearest kernel-symbol for runtime addresses (elf addr = runtime + 0x138000000).
import re, subprocess, sys

elf = "/root/neutrino/build/x64/kernel_syms.elf"
targets = [int(a, 16) for a in sys.argv[1:]] or [0x8128f20, 0x8128f66, 0x8131a00]

out = subprocess.run(["readelf", "-sW", elf], capture_output=True, text=True).stdout
syms = []
for line in out.splitlines():
    m = re.match(r"\s*\d+:\s+([0-9a-f]{8,16})\s+\d+\s+(\w+)\s+\S+\s+\S+\s+\S+\s+(.*)", line)
    if m:
        addr = int(m.group(1), 16)
        syms.append((addr, m.group(3).strip()))
syms.sort()

OFF = 0x138000000
print(f"{len(syms)} symbols parsed")
for t in targets:
    e = t + OFF
    lo, hi = 0, len(syms)
    while lo < hi:
        mid = (lo + hi) // 2
        if syms[mid][0] <= e:
            lo = mid + 1
        else:
            hi = mid
    if lo == 0:
        print(f"0x{t:x} (elf 0x{e:x}): no preceding symbol")
        continue
    addr, name = syms[lo - 1]
    print(f"0x{t:x} (elf 0x{e:x}) -> +0x{e - addr:x} {name} @0x{addr - OFF:x}")
