#!/bin/bash
# One-off (temp): gate builtin/feature diagnostics behind JitTrace.
# Keeps error/warning lines on DebugConsole; converts informational
# output so normal builds are silent (TRACE=1 builds bring it back).
set -eu
cd /mnt/d/Projects/Code/NeutrinoOS

# GC.cs: keep Failed / WARNING / Not initialized / already in progress
sed -i '/Failed\|WARNING\|Not initialized\|already in progress/!s/DebugConsole\.Write/JitTrace.Write/g' src/kernel/Memory/GC.cs

# PowerManagement.cs: keep failure/limitation notices that explain why an
# action cannot work (Failed / not found / not initialized / unavailable /
# no ACPI S5); gate the informational ACPI/reboot/sleep lines.
sed -i '/Failed\|not found\|not initialized\|unavailable\|no ACPI S5/!s/DebugConsole\.Write/JitTrace.Write/g' src/kernel/Platform/PowerManagement.cs

# Scheduler.cs: keep failure lines
sed -i '/Failed\|WARNING/!s/DebugConsole\.Write/JitTrace.Write/g' src/kernel/Threading/Scheduler.cs

# CpuPower.cs: every line is a [cpupower] diagnostic
sed -i 's/DebugConsole\.Write/JitTrace.Write/g' src/kernel/Platform/CpuPower.cs

git diff --stat
echo "--- JitTrace counts ---"
grep -c JitTrace src/kernel/Memory/GC.cs src/kernel/Platform/PowerManagement.cs src/kernel/Threading/Scheduler.cs src/kernel/Platform/CpuPower.cs
echo "--- remaining DebugConsole in converted files ---"
grep -n "DebugConsole" src/kernel/Memory/GC.cs src/kernel/Platform/PowerManagement.cs src/kernel/Threading/Scheduler.cs src/kernel/Platform/CpuPower.cs
