#!/bin/bash
# Map a faulting RIP to the nearest JIT method entry from a [j| dump.
# usage: faultmap.sh <log.txt> <rip-hex-without-0x>
LOG="$1"
RIP="$2"
python3 - "$LOG" "$RIP" <<'PY'
import re, sys
log, rip = sys.argv[1], int(sys.argv[2], 16)
entries = []
pat = re.compile(r'\[j\|0x([0-9A-Fa-f]+) at 0x([0-9A-Fa-f]+)')
for line in open(log, 'r', errors='replace'):
    m = pat.search(line)
    if m:
        entries.append((int(m.group(2), 16), m.group(1)))
best = None
for addr, tok in entries:
    if addr <= rip and (best is None or addr > best[0]):
        best = (addr, tok)
print('rip=0x%X total_entries=%d' % (rip, len(entries)))
if best:
    print('nearest entry below: 0x%X token=0x%s (delta=+0x%X)' % (best[0], best[1], rip - best[0]))
    # second nearest below for range sanity
    below = sorted([e for e in entries if e[0] <= rip], reverse=True)[:3]
    for a, t in below:
        print('  0x%X token=0x%s +0x%X' % (a, t, rip - a))
PY
