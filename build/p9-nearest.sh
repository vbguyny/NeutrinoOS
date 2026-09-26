#!/bin/bash
# Nearest-symbol lookup for a runtime kernel address (base 0x8000000).
A=${1:?usage: p9-nearest.sh <runtime hex addr without 0x>}
RT=$((16#$A))
ELF=$((0x140000000 + RT - 0x8000000))
printf 'rt=0x%x elf=0x%x\n' "$RT" "$ELF"
nm /root/neutrino/build/x64/kernel_syms.elf 2>/dev/null | sort > /tmp/p9syms.txt
awk -v t="$(printf '%016x' $ELF)" '
{
  a=$1; sub(/^0+/, "", a); if (a=="") a="0";
  ta=t; sub(/^0+/, "", ta); if (ta=="") ta="0";
  if (length(a) < length(ta) || (length(a)==length(ta) && a<=ta)) {
    if (best=="" || length(a)>length(best) || (length(a)==length(best) && a>best)) { best=a; bestn=$3 }
  }
}
END { printf "nearest: 0x%s  %s\n", best, bestn }
' /tmp/p9syms.txt
