#!/bin/bash
# mreftool - resolve CLR MemberRef rows (including TypeSpec parents) to names.
# Used to turn the device JIT's "unresolved token 0x0A000xyz" lines into names.
#
#   bash build/mreftool/run.sh <dll>                  # every MemberRef
#   bash build/mreftool/run.sh <dll> IsMatch          # filtered by name
#   bash build/mreftool/run.sh <dll> --fields         # field refs only
#   bash build/mreftool/run.sh <dll> @EE              # raw parent/signature blobs for row 0xEE
#   bash build/mreftool/run.sh <dll> --typeref 3F     # decode a TypeRef row
#   bash build/mreftool/run.sh <dll> --typedefs Nullable   # list TypeDefs matching a substring
set -uo pipefail
export DOTNET_ROOT=/usr/share/dotnet
export PATH="/usr/share/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT=/tmp/mreftool
dotnet build "$HERE/mreftool.csproj" -c Release -o "$OUT" --nologo -v q 2>&1 | grep -E ' error ' | head -10
dotnet "$OUT/mreftool.dll" "$@"
