#!/bin/bash
# Compare boot-test fault signatures across logs
for f in /root/p7prof.log /root/p7loop.log /root/p7bench.log /root/p6sys.log /root/p7file.log; do
  [ -f "$f" ] || continue
  halts=$(strings "$f" | grep -c 'SYSTEM HALTED')
  rawv=$(strings "$f" | grep -c 'RAWV v=0xE rip=0x202')
  boot=$(strings "$f" | grep -c 'Boot tests complete')
  printf '%-24s halts=%s rawv=%s bootcomplete=%s\n' "$f" "$halts" "$rawv" "$boot"
done
