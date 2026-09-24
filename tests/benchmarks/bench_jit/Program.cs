// NeutrinoOS Phase 7 benchmark: JIT compile throughput workload.
// Runs a method-heavy workload so the harness can diff `jitstats`
// around the run (methods compiled per second, native bytes emitted).
using System;
using System.Diagnostics;

namespace NeutrinoOS.Benchmarks.Jit;

/// <summary>JIT workload benchmark (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; runs a method/type-heavy workload for jitstats deltas.</summary>
    public static int Main(string[] args)
    {
        Console.WriteLine("[bench] jit workload (method-heavy warmup + compute)");
        Stopwatch sw = Stopwatch.StartNew();
        long acc = 0;
        for (int i = 0; i < 200000; i++)
        {
            acc += Step1(i) + Step2(i) + Step3(i & 3);
            if ((i & 0x3FFF) == 0)
                acc += StringWork(i);
        }
        long ms = sw.ElapsedMilliseconds;
        Console.WriteLine("[bench] jit workload done in " + ms + " ms (acc=" + (acc & 0xFFFF) + ")");
        Console.WriteLine("[bench] (harness computes jitstats deltas around this run)");
        return 0;
    }

    private static int Step1(int i) => (int)((uint)i * 2654435761u) & 0xFFFF;
    private static int Step2(int i) => (i ^ (i >> 7)) & 0xFFF;
    private static int Step3(int m) => m == 0 ? 1 : m == 1 ? 2 : m == 2 ? 3 : 4;

    private static int StringWork(int i)
    {
        string s = "iter-" + (i & 7).ToString();
        return s.Length;
    }
}
