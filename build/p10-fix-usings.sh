#!/bin/bash
# One-off (temp): add "using NeutrinoOS.Runtime;" (JitTrace) to files that
# were converted to JitTrace but did not import the runtime namespace.
set -eu
cd /mnt/d/Projects/Code/NeutrinoOS
for f in \
  src/kernel/Platform/PowerManagement.cs \
  src/kernel/Platform/CpuPower.cs \
  src/kernel/Threading/Scheduler.cs \
  src/kernel/Shell/KernelGc.cs \
  src/kernel/Shell/ShellBuiltins.cs \
  src/kernel/Usb/UsbCore.cs; do
  if ! grep -q 'using NeutrinoOS.Runtime;' "$f"; then
    sed -i '0,/^using /s/^using /using NeutrinoOS.Runtime;\nusing /' "$f"
    echo "added: $f"
  else
    echo "present: $f"
  fi
done
