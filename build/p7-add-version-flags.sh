#!/bin/bash
# Phase 7: add --version handling to every utility Main (idempotent).
set -u
cd /mnt/d/Projects/Code/NeutrinoOS/src/utilities

ADDED=0
for f in */Program.cs; do
  if grep -q 'VersionFlag.Handle' "$f"; then
    continue
  fi
  perl -0pi -e 's/(public static int Main\(string\[\] args\)\r?\n(\s*)\{\r?\n)/$1$2    if (ProtonOS.DDK.Util.VersionFlag.Handle(args))\n$2        return 0;\n/' "$f"
  if grep -q 'VersionFlag.Handle' "$f"; then
    ADDED=$((ADDED+1))
  else
    echo "MISSED: $f"
  fi
done

# The unsafe-Main trio (dhcp, free, ssh).
for f in */Program.cs; do
  if grep -q 'VersionFlag.Handle' "$f"; then
    continue
  fi
  perl -0pi -e 's/(public static unsafe int Main\(string\[\] args\)\r?\n(\s*)\{\r?\n)/$1$2    if (ProtonOS.DDK.Util.VersionFlag.Handle(args))\n$2        return 0;\n/' "$f"
  if grep -q 'VersionFlag.Handle' "$f"; then
    ADDED=$((ADDED+1))
  else
    echo "MISSED2: $f"
  fi
done

echo "added=$ADDED"
grep -l 'VersionFlag.Handle' */Program.cs | wc -l
echo "--- sample (cat):"
grep -n -A3 'static int Main' cat/Program.cs | head -8
