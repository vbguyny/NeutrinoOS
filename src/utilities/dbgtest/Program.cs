// NeutrinoOS Phase 9 diagnostic utility: dbgtest - DDK Debug P/Invoke probe.
//
// Isolates whether Debug.WriteHex/WriteDecimal (Kernel_DebugWriteHex64 &
// friends) work from the JIT/utility context: the IPv6 RA path crashed at
// the first WriteHex call while string prints were fine.

using System;
using NeutrinoOS.Utils;
using NeutrinoOS.DDK.Kernel;

namespace NeutrinoOS.Utility.DbgTest;

/// <summary>Debug P/Invoke probe (see file header).</summary>
public static class Program
{
    /// <summary>Entry point.</summary>
    public static int Main(string[] args)
    {
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
            return Util.Help(
                "usage: dbgtest",
                "Probes the DDK Debug P/Invoke path (Debug.Write/WriteHex/",
                "WriteDecimal) from the JIT utility context.");
        Debug.Write("dbgtest: start\n");
        Debug.Write("dbgtest: hex64 -> ");
        Debug.WriteHex((ulong)0x1122334455667788UL);
        Debug.Write("\ndbgtest: hex64 ok\n");
        Debug.Write("dbgtest: hex32 -> ");
        Debug.WriteHex((uint)0xAABBCCDD);
        Debug.Write("\ndbgtest: hex32 ok\n");
        Debug.Write("dbgtest: hex16 -> ");
        Debug.WriteHex((ushort)0xBEEF);
        Debug.Write("\ndbgtest: hex16 ok\n");
        Debug.Write("dbgtest: decint -> ");
        Debug.WriteDecimal(42);
        Debug.Write("\ndbgtest: decint ok\n");
        Debug.Write("dbgtest: decu -> ");
        Debug.WriteDecimal((uint)1234);
        Debug.Write("\ndbgtest: decu ok\n");
        Debug.Write("dbgtest: dec64 -> ");
        Debug.WriteDecimal((ulong)99);
        Debug.Write("\ndbgtest: dec64 ok\n");
        Debug.Write("dbgtest: done\n");
        return 0;
    }
}
