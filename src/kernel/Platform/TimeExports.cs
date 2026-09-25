// NeutrinoOS Phase 7 - high-resolution time exports for the JIT world
//
// Backs korlib's System.Diagnostics.Stopwatch bridge (StopwatchBootNs
// in System.Diagnostics.Stopwatch maps to this export through the
// token registry). Returns the kernel uptime clock in nanoseconds
// (HPET-backed, monotonic).

using System.Runtime.InteropServices;
using ProtonOS.Arch;

namespace ProtonOS.Platform;

/// <summary>Phase 7 time exports (see file header).</summary>
public static class TimeExports
{
    /// <summary>Monotonic nanosecond timestamp for Stopwatch (JIT world).</summary>
    [UnmanagedCallersOnly(EntryPoint = "StopwatchBootNs")]
    public static ulong StopwatchBootNs()
    {
        return HPET.TicksToNanoseconds(HPET.ReadCounter());
    }
}
