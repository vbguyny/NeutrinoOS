// NeutrinoOS Phase 7 - Tier-0 JIT statistics
//
// Aggregates compile-time counters for the `jitstats` shell built-in:
// total/max/min compile time, method count, success count and the 16
// slowest top-level compilations (assembly id + method token). Timing
// uses the HPET nanosecond counter around CompileMethod; nested
// compilations are counted but not timed individually.

using System;

namespace ProtonOS.Profiling;

/// <summary>Tier-0 JIT compile-time statistics (see file header).</summary>
public static class JitStats
{
    /// <summary>One slowest-compilation record.</summary>
    public struct Entry
    {
        /// <summary>Assembly id of the compiled method.</summary>
        public uint AssemblyId;
        /// <summary>MethodDef token of the compiled method.</summary>
        public uint Token;
        /// <summary>Wall time of the compile in nanoseconds.</summary>
        public ulong Nanoseconds;
        /// <summary>Native code size in bytes (0 on failure).</summary>
        public int CodeSize;
        /// <summary>False when the compile failed.</summary>
        public bool Success;
    }

    /// <summary>Number of slowest compilations retained.</summary>
    public const int TopCount = 16;

    /// <summary>Total methods compiled (top-level plus nested).</summary>
    public static int Count;

    /// <summary>Total methods compiled at nesting level 1 (top-level).</summary>
    public static int TopLevelCount;

    /// <summary>Failed compilations.</summary>
    public static int FailedCount;

    /// <summary>Total nanoseconds spent in top-level compilations.</summary>
    public static ulong TotalNanoseconds;

    /// <summary>Slowest top-level compilation in nanoseconds.</summary>
    public static ulong MaxNanoseconds;

    /// <summary>Fastest top-level compilation in nanoseconds.</summary>
    public static ulong MinNanoseconds;

    /// <summary>Total native code bytes emitted for top-level compiles.</summary>
    public static ulong TotalCodeBytes;

    private static Entry[] _top;

    /// <summary>Lazily allocate the slowest-entries table (no static array initializers).</summary>
    private static void EnsureTop()
    {
        if (_top == null)
            _top = new Entry[TopCount];
    }

    /// <summary>Record one top-level compilation.</summary>
    public static void Record(uint assemblyId, uint token, ulong nanoseconds, bool success, int codeSize)
    {
        EnsureTop();
        TopLevelCount++;
        if (!success)
            FailedCount++;
        TotalNanoseconds += nanoseconds;
        if (nanoseconds > MaxNanoseconds)
            MaxNanoseconds = nanoseconds;
        if (MinNanoseconds == 0 || nanoseconds < MinNanoseconds)
            MinNanoseconds = nanoseconds;
        if (codeSize > 0)
            TotalCodeBytes += (ulong)codeSize;

        // Insertion into the slowest-16 list (descending; drop the tail).
        if (_top[TopCount - 1].Nanoseconds >= nanoseconds)
            return;
        int slot = TopCount - 1;
        while (slot > 0 && _top[slot - 1].Nanoseconds < nanoseconds)
            slot--;
        for (int i = TopCount - 1; i > slot; i--)
            _top[i] = _top[i - 1];
        _top[slot].AssemblyId = assemblyId;
        _top[slot].Token = token;
        _top[slot].Nanoseconds = nanoseconds;
        _top[slot].CodeSize = codeSize;
        _top[slot].Success = success;
    }

    /// <summary>Reset all counters.</summary>
    public static void Reset()
    {
        EnsureTop();
        Count = 0;
        TopLevelCount = 0;
        FailedCount = 0;
        TotalNanoseconds = 0;
        MaxNanoseconds = 0;
        MinNanoseconds = 0;
        TotalCodeBytes = 0;
        for (int i = 0; i < TopCount; i++)
        {
            _top[i].Nanoseconds = 0;
            _top[i].AssemblyId = 0;
            _top[i].Token = 0;
            _top[i].CodeSize = 0;
            _top[i].Success = false;
        }
    }

    /// <summary>Print the statistics to <paramref name="w"/>.</summary>
    public static void Format(System.IO.TextWriter w)
    {
        EnsureTop();
        w.WriteLine("[jitstats] NeutrinoOS Tier-0 JIT compile statistics");
        w.Write("[jitstats] methods compiled: ");
        w.Write((long)Count);
        w.Write(" (top-level: ");
        w.Write((long)TopLevelCount);
        w.Write(", failed: ");
        w.Write((long)FailedCount);
        w.WriteLine(")");

        w.Write("[jitstats] top-level wall time: ");
        w.Write((long)(TotalNanoseconds / 1_000_000));
        w.Write(" ms total, ");
        w.Write((long)(MaxNanoseconds / 1000));
        w.Write(" us max, ");
        w.Write((long)(MinNanoseconds / 1000));
        w.Write(" us min, ");
        w.Write(TopLevelCount > 0 ? (long)(TotalNanoseconds / (ulong)TopLevelCount / 1000) : 0L);
        w.WriteLine(" us avg");

        w.Write("[jitstats] native code emitted (top-level): ");
        w.Write((long)TotalCodeBytes);
        w.WriteLine(" bytes");

        w.WriteLine("[jitstats] slowest compilations:");
        int shown = 0;
        for (int i = 0; i < TopCount; i++)
        {
            if (_top[i].Nanoseconds == 0)
                break;
            w.Write("  ");
            w.Write((long)(_top[i].Nanoseconds / 1000));
            w.Write(" us  asm=");
            w.Write((long)_top[i].AssemblyId);
            w.Write(" tok=");
            w.Write("0x" + _top[i].Token.ToString("X8", null));
            w.Write("  code=");
            w.Write((long)_top[i].CodeSize);
            w.Write("B");
            w.WriteLine(_top[i].Success ? "" : "  (failed)");
            shown++;
        }
        if (shown == 0)
            w.WriteLine("  (none recorded - compile something first)");
    }
}
