#!/usr/bin/env python3
"""Extract [amldump2] hex rows from the boot log into raw AML files."""
import re, os, sys

log = sys.argv[1] if len(sys.argv) > 1 else '/root/p9dump.log'
outdir = '/root/aml'
os.makedirs(outdir, exist_ok=True)

cur = None
tables = []  # (sig, bytearray)
for line in open(log, 'r', errors='replace'):
    if '[amldump1]' in line:
        m = re.search(r'amldump1\]\s+(\w+)\s+len=', line)
        sig = m.group(1) if m else 'UNKN'
        cur = (sig, bytearray())
    elif '[amldump2]' in line and cur is not None:
        hexpart = line.split('[amldump2]', 1)[1]
        for tok in hexpart.split():
            if len(tok) == 2:
                cur[1].append(int(tok, 16))
    elif '[amldump3]' in line and cur is not None:
        tables.append(cur)
        cur = None

i = 0
for sig, data in tables:
    fn = os.path.join(outdir, f'{sig}_{i}.aml')
    with open(fn, 'wb') as f:
        f.write(data)
    print(f'{fn}: {len(data)} bytes')
    i += 1
