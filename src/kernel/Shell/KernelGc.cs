// NeutrinoOS kernel - Phase 5 shell: gc built-in helper
//
// Triggers a manual garbage collection and reports kernel GC statistics
// (heap size, object counts, collection count, collection duration).
// The gc command is a built-in because it inspects the kernel GC; the
// managed System.GC.Collect() path (korlib) is used by the collection
// itself so the shell and JIT-compiled code share one GC.

using System;
using ProtonOS.Memory;
using ProtonOS.Arch;

namespace ProtonOS.Shell;

/// <summary>Implements the gc built-in (see file header).</summary>
public static class KernelGc
{
    /// <summary>
    /// Runs a diagnostic garbage collection (full mark phase; the sweep
    /// and compaction stages are allocation-driven kernel steps and are
    /// deferred in this mode — see ProtonOS.Memory.GC.CollectMarkOnly)
    /// and prints statistics: SOH/LOH allocation before and after,
    /// objects marked, roots scanned and the wall-clock duration (APIC
    /// tick = 1 ms resolution).
    /// </summary>
    public static void ReportAndCollect()
    {
        GCHeap.GetStats(out ulong socBefore, out ulong objectsBefore, out ulong freeBefore);
        GCHeap.GetLOHStats(out ulong lohBefore, out ulong lohObjectsBefore);

        ulong startTick = APIC.TickCount;
        int marked = ProtonOS.Memory.GC.CollectMarkOnly();
        ulong elapsedMs = APIC.TickCount - startTick;

        GCHeap.GetStats(out ulong socAfter, out ulong objectsAfter, out ulong freeAfter);
        GCHeap.GetLOHStats(out ulong lohAfter, out ulong lohObjectsAfter);

        ProtonOS.Memory.GC.GetStats(out ulong collections, out ulong lastMarked, out ulong lastRoots);

        Console.WriteLine("[gc] NeutrinoOS garbage collection (mark phase) complete");
        Console.Write("[gc] heap before: ");
        Console.Write(FormatKb(socBefore + lohBefore));
        Console.Write(" (");
        Console.Write((long)(objectsBefore + lohObjectsBefore));
        Console.Write(" objects)   after: ");
        Console.Write(FormatKb(socAfter + lohAfter));
        Console.Write(" (");
        Console.Write((long)(objectsAfter + lohObjectsAfter));
        Console.WriteLine(" objects)");
        Console.Write("[gc] marked ");
        Console.Write(marked >= 0 ? (long)marked : 0L);
        Console.Write(" objects from ");
        Console.Write((long)lastRoots);
        Console.Write(" roots        duration: ");
        Console.Write((long)elapsedMs);
        Console.WriteLine(" ms");
        Console.Write("[gc] collections: ");
        Console.Write((long)collections);
        Console.Write("   free (SOH): ");
        Console.WriteLine(FormatKb(freeAfter));
        Console.WriteLine("[gc] note: sweep/compaction run as allocation-driven kernel steps;");
        Console.WriteLine("[gc]       manual collection is mark-only in Phase 5 (PHASE5-REPORT.md).");
    }

    /// <summary>Formats a byte count as "N.N KB".</summary>
    private static string FormatKb(ulong bytes)
    {
        ulong kb = bytes / 1024;
        ulong rem = (bytes % 1024) * 10 / 1024;
        return kb.ToString() + "." + rem.ToString() + " KB";
    }
}
