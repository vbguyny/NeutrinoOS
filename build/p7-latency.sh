#!/bin/bash
# Phase 7: external TLS/SSH latency probe against the running serve VM.
# usage: p7-latency.sh [label]
set -u
LABEL="${1:-run}"
echo "=== latency probe: $LABEL ($(date +%T)) ==="

A=$(strings /root/p6serve.log | wc -l)
for i in 1 2 3 4 5; do
  /usr/bin/time -f 'ssh %e s' ssh -i /root/p6key -p 2222 -o StrictHostKeyChecking=no \
    -o UserKnownHostsFile=/dev/null -o BatchMode=yes user@127.0.0.1 true 2>&1 | tail -1
done
B=$(strings /root/p6serve.log | wc -l)
echo "ssh trace lines: $((B - A))"

for i in 1 2 3 4 5; do
  curl -k -s -o /dev/null -w 'tls %{time_appconnect}s\n' https://127.0.0.1:8444/health
done
C=$(strings /root/p6serve.log | wc -l)
echo "tls trace lines: $((C - B))"
echo "=== recent trace lines ==="
strings /root/p6serve.log | tail -12
