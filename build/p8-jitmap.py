#!/usr/bin/env python3
"""Map a JIT runtime address to the JIT method containing it.

Usage: p8-jitmap.py <logfile> <address> [address...]
Parses '[j|0xtoken at 0xaddr' entries from a kernel fault dump, sorts by
address, and reports the method whose extent contains each queried address.
"""
import re
import sys

def main():
    log = sys.argv[1]
    targets = [int(a, 16) for a in sys.argv[2:]]
    entries = []
    with open(log, 'rb') as f:
        data = f.read().decode('utf-8', 'replace')
    for m in re.finditer(r'\[j\|(0x[0-9A-Fa-f]+) at (0x[0-9A-Fa-f]+)', data):
        entries.append((int(m.group(2), 16), int(m.group(1), 16)))
    # dedupe, keep last occurrence per address
    seen = {}
    for addr, tok in entries:
        seen[addr] = tok
    entries = sorted((a, t) for a, t in seen.items())
    print(f'total entries: {len(entries)}')
    for target in targets:
        best = None
        nxt = None
        for i, (addr, tok) in enumerate(entries):
            if addr <= target:
                best = (addr, tok)
                nxt = entries[i + 1][0] if i + 1 < len(entries) else None
            else:
                break
        if best is None:
            print(f'{target:#x}: no entry <= target')
            continue
        size = (nxt - best[0]) if nxt is not None else -1
        print(f'{target:#x}: method token 0x{best[1]:X} at 0x{best[0]:X} (next=0x{nxt:X} size~{size:#x}) offset=+{target - best[0]:#x}')

if __name__ == '__main__':
    main()
