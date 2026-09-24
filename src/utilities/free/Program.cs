// NeutrinoOS Phase 5 utility: free - print memory usage
//
// usage: free
//   Physical memory (total/used/free pages) and the kernel GC heap
//   (allocated bytes, object counts, LOH) via the DDK memory exports.

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Kernel;

namespace NeutrinoOS.Utility.Free;

/// <summary>The free utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 when the kernel stats are unavailable.</summary>
    public static unsafe int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: free",
                "  Show physical memory (total/used/free) and GC heap statistics.");
        }
        if (args.Length > 0)
            return Util.Fail("free", "usage: free");

        MemoryStats stats;
        if (!Memory.GetMemoryStats(&stats))
            return Util.Fail("free", "memory statistics unavailable");

        ulong used = stats.TotalMemory - stats.FreeMemory;

        Console.WriteLine("             total        used        free");
        Console.Write("Mem:  ");
        Console.Write(Util.PadLeft((long)(stats.TotalMemory / 1024), 11));
        Console.Write(" KB ");
        Console.Write(Util.PadLeft((long)(used / 1024), 10));
        Console.Write(" KB ");
        Console.Write(Util.PadLeft((long)(stats.FreeMemory / 1024), 10));
        Console.WriteLine(" KB");

        Console.WriteLine();
        Console.Write("GC heap: ");
        Console.Write(Util.PadLeft((long)(stats.GCHeapAllocated / 1024), 8));
        Console.Write(" KB allocated (");
        Console.Write((long)stats.GCHeapObjects);
        Console.Write(" objects, ");
        Console.Write(Util.PadLeft((long)(stats.GCHeapFreeSpace / 1024), 6));
        Console.WriteLine(" KB free)");

        Console.Write("LOH:     ");
        Console.Write(Util.PadLeft((long)(stats.LOHAllocated / 1024), 8));
        Console.Write(" KB allocated (");
        Console.Write((long)stats.LOHObjects);
        Console.WriteLine(" objects)");

        Console.Write("Pages:   ");
        Console.Write((long)stats.TotalPages);
        Console.Write(" total, ");
        Console.Write((long)stats.FreePages);
        Console.WriteLine(" free");
        return 0;
    }
}
