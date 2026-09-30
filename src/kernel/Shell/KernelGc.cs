// NeutrinoOS kernel - Phase 5 shell: gc built-in helper
//
// Triggers a manual garbage collection and reports kernel GC statistics
// (heap size, object counts, collection count, collection duration).
// The gc command is a built-in because it inspects the kernel GC; the
// managed System.GC.Collect() path (korlib) is used by the collection
// itself so the shell and JIT-compiled code share one GC.

using NeutrinoOS.Runtime;
using System;
using NeutrinoOS.Memory;
using NeutrinoOS.Arch;

namespace NeutrinoOS.Shell;

/// <summary>Implements the gc built-in (see file header).</summary>
public static class KernelGc
{
    /// <summary>
    /// Runs a diagnostic garbage collection (full mark phase; the sweep
    /// and compaction stages are allocation-driven kernel steps and are
    /// deferred in this mode — see NeutrinoOS.Memory.GC.CollectMarkOnly)
    /// and prints statistics: SOH/LOH allocation before and after,
    /// objects marked, roots scanned and the wall-clock duration (APIC
    /// tick = 1 ms resolution).
    /// </summary>
    public static void ReportAndCollect()
    {
        GCHeap.GetStats(out ulong socBefore, out ulong objectsBefore, out ulong freeBefore);
        GCHeap.GetLOHStats(out ulong lohBefore, out ulong lohObjectsBefore);

        ulong startTick = APIC.TickCount;
        int marked = NeutrinoOS.Memory.GC.CollectMarkOnly();
        ulong elapsedMs = APIC.TickCount - startTick;

        GCHeap.GetStats(out ulong socAfter, out ulong objectsAfter, out ulong freeAfter);
        GCHeap.GetLOHStats(out ulong lohAfter, out ulong lohObjectsAfter);

        NeutrinoOS.Memory.GC.GetStats(out ulong collections, out ulong lastMarked, out ulong lastRoots);

        JitTrace.WriteLine("[gc] NeutrinoOS garbage collection (mark phase) complete");
        JitTrace.Write("[gc] heap before: ");
        JitTrace.Write(FormatKb(socBefore + lohBefore));
        JitTrace.Write(" (");
        JitTrace.Write((long)(objectsBefore + lohObjectsBefore));
        JitTrace.Write(" objects)   after: ");
        JitTrace.Write(FormatKb(socAfter + lohAfter));
        JitTrace.Write(" (");
        JitTrace.Write((long)(objectsAfter + lohObjectsAfter));
        JitTrace.WriteLine(" objects)");
        JitTrace.Write("[gc] marked ");
        JitTrace.Write(marked >= 0 ? (long)marked : 0L);
        JitTrace.Write(" objects from ");
        JitTrace.Write((long)lastRoots);
        JitTrace.Write(" roots        duration: ");
        JitTrace.Write((long)elapsedMs);
        JitTrace.WriteLine(" ms");
        JitTrace.Write("[gc] collections: ");
        JitTrace.Write((long)collections);
        JitTrace.Write("   free (SOH): ");
        JitTrace.WriteLine(FormatKb(freeAfter));
        JitTrace.WriteLine("[gc] note: sweep/compaction run as allocation-driven kernel steps;");
        JitTrace.WriteLine("[gc]       manual collection is mark-only in Phase 5 (PHASE5-REPORT.md).");
    }

    /// <summary>Formats a byte count as "N.N KB".</summary>
    private static string FormatKb(ulong bytes)
    {
        ulong kb = bytes / 1024;
        ulong rem = (bytes % 1024) * 10 / 1024;
        return kb.ToString() + "." + rem.ToString() + " KB";
    }
}
