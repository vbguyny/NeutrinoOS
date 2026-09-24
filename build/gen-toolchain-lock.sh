#!/bin/bash
# Phase 7: regenerate toolchain.lock (run after any toolchain change).
set -u
cd /root/neutrino
OUT=/mnt/d/Projects/Code/NeutrinoOS/toolchain.lock

{
  echo "# NeutrinoOS toolchain lock (Phase 7)"
  echo "# Regenerate with: bash build/gen-toolchain-lock.sh"
  echo "# Format: name<TAB>version"
  BF=/root/neutrino/tools/bflat
  if [ -d "$BF" ]; then
    BF_COMMIT=$(git -C "$BF" log --oneline -1 2>/dev/null | awk '{print $1}')
    BF_ILC=$(grep -o '<LocalILCVersion>[^<]*' "$BF/src/bflat/bflat.csproj" 2>/dev/null | cut -d'>' -f2)
    BF_RT=$(grep -o '<RuntimeVersion>[^<]*' "$BF/src/bflat/bflat.csproj" 2>/dev/null | cut -d'>' -f2)
    printf 'bflat\t%s (local fork %s; ILCompiler %s; runtime %s)\n' "${BF_COMMIT:-local}" "${BF_ILC:-?}" "${BF_ILC:-?}" "${BF_RT:-?}"
  else
    printf 'bflat\t%s\n' "$(bflat --version 2>/dev/null | tail -1 | tr -d '\r' || echo unknown)"
  fi
  printf 'dotnet\t%s\n'   "$(dotnet --version 2>/dev/null | tail -1)"
  printf 'lld\t%s\n'      "$(ld.lld --version 2>/dev/null | head -1)"
  printf 'clang\t%s\n'    "$(clang --version 2>/dev/null | head -1)"
  printf 'make\t%s\n'     "$(make --version 2>/dev/null | head -1 | awk '{print $3}')"
  printf 'python3\t%s\n'  "$(python3 --version 2>/dev/null | awk '{print $2}')"
  printf 'qemu\t%s\n'     "$(qemu-system-x86_64 --version 2>/dev/null | head -1 | awk '{print $4}')"
  printf 'mtools\t%s\n'   "$(mformat --version 2>/dev/null | head -1 | awk '{print $4}')"
  printf 'gpg\t%s\n'      "$(gpg --version 2>/dev/null | head -1 | awk '{print $3}')"
} > "$OUT"

echo "== wrote $OUT:"
cat "$OUT"
