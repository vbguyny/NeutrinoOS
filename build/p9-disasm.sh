#!/bin/bash
# Full sequence: rebuild, boot-dump AML tables, extract, disassemble.
set -uo pipefail
cd /root/neutrino || exit 1
rsync -a --delete --exclude obj --exclude bin /mnt/d/Projects/Code/NeutrinoOS/src/ src/
make image > /tmp/p9dump-build.log 2>&1
echo "make rc=$? cs-errors=$(grep -ac 'error CS' /tmp/p9dump-build.log || true)"
bash /root/p9-dump.sh
python3 /root/p9-extract-aml.py /root/p9dump.log
cd /root/aml || exit 1
rm -f *.dsl
for f in *.aml; do
  iasl -d "$f" > /dev/null 2>&1
done
ls -la /root/aml
echo "=== _PTS / _WAK / _S3 occurrences in ASL ==="
grep -n '_PTS\|_WAK\|_S3\b' *.dsl 2>/dev/null | head -30
