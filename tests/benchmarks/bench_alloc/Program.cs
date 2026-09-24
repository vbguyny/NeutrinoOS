// NeutrinoOS Phase 7 benchmark: allocation throughput (SOH + LOH)
// Run on device via `bench_alloc`; reports MB/s for small-object and
// large-object allocations. Measured with System.Diagnostics.Stopwatch
// (kernel HPET-backed clock).
using System;
using System.Diagnostics;

namespace NeutrinoOS.Benchmarks.Alloc;

/// <summary>Allocation throughput benchmark (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; prints SOH and LOH allocation throughput.</summary>
    public static int Main(string[] args)
    {
        Console.WriteLine("[bench] alloc throughput (SOH 256B x 20000, LOH 256KB x 32)");

        Stopwatch sw = Stopwatch.StartNew();
        int sum = 0;
        for (int i = 0; i < 20000; i++)
        {
            byte[] b = new byte[256];
            b[0] = (byte)i;
            sum += b[0];
        }
        long smallMs = sw.ElapsedMilliseconds;
        if (sum < 0)
            Console.WriteLine("(unreachable)");

        sw.Restart();
        long lohBytes = 0;
        for (int i = 0; i < 32; i++)
        {
            byte[] big = new byte[262144];
            big[0] = 1;
            lohBytes += big.Length;
        }
        long lohMs = sw.ElapsedMilliseconds;

        long smallKb = 20000L * 256 / 1024;      // 5000 KB
        long lohKb = lohBytes / 1024;            // 8192 KB
        Console.WriteLine("[bench] SOH: " + smallKb + " KB in " + smallMs + " ms -> "
            + (smallMs > 0 ? smallKb / smallMs : 0) + " MB/s");
        Console.WriteLine("[bench] LOH: " + lohKb + " KB in " + lohMs + " ms -> "
            + (lohMs > 0 ? lohKb / lohMs : 0) + " MB/s");
        return 0;
    }
}
