#!/bin/bash
# Dump method names + IL for a contiguous MethodDef token row range.
# Usage: p8-rowdump.sh <dll> <startrow-hex> <count>
set -uo pipefail
DLL="${1:?usage: p8-rowdump.sh <dll> <startrow-hex> <count>}"
START="${2:?startrow}"
COUNT="${3:?count}"
args=()
for i in $(seq 0 $((COUNT - 1))); do
  printf -v t '0x%X' $((START + i))
  args+=("$t")
done
exec bash /mnt/d/Projects/Code/NeutrinoOS/build/p8-ildump.sh "$DLL" "${args[@]}"
