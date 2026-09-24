// NeutrinoOS Phase 7 - kernel sampling profiler
//
// Samples the interrupted instruction pointer (RIP from the interrupt
// frame) on every LAPIC timer tick (1 ms) while enabled, into a fixed
// ring buffer. The `perf` shell built-in starts/stops/resets the
// sampler and prints the hottest 64-byte buckets with the nearest
// compiled-method symbol (Tier-0 JIT registry reverse lookup).
//
// The buffer is a plain managed ulong[] allocated by Start() (never
// from the ISR); Sample() runs in interrupt context and only writes to
// the preallocated buffer.

using System;
using System.IO;
using ProtonOS.Runtime.JIT;

namespace ProtonOS.Profiling;

/// <summary>Kernel sampling profiler (see file header).</summary>
public static class Profiler
{
    /// <summary>Ring-buffer capacity in samples (16384 ms = ~16 s at 1 kHz).</summary>
    public const int Capacity = 16384;

    /// <summary>Number of buckets reported by <see cref="Format"/>.</summary>
    public const int MaxTop = 16;

    /// <summary>Bucket size for grouping samples (64-byte code ranges).</summary>
    private const ulong BucketMask = ~63UL;

    private static ulong[] _samples;
    private static int _head;
    private static int _count;
    private static ulong _dropped;
    private static bool _enabled;
    private static ulong _startedMs;
    private static ulong _stoppedMs;

    /// <summary>True while the sampler is recording.</summary>
    public static bool Enabled => _enabled;

    /// <summary>Number of samples currently held in the ring.</summary>
    public static ulong SampleCount => (ulong)_count;

    /// <summary>Number of samples lost because the ring was full.</summary>
    public static ulong Dropped => _dropped;

    /// <summary>Start sampling (allocates the ring on first use).</summary>
    public static void Start()
    {
        if (_samples == null)
            _samples = new ulong[Capacity];
        _head = 0;
        _count = 0;
        _dropped = 0;
        _startedMs = UptimeMs();
        _enabled = true;
    }

    /// <summary>Stop sampling.</summary>
    public static void Stop()
    {
        _enabled = false;
        _stoppedMs = UptimeMs();
    }

    /// <summary>Drop all recorded samples without changing the running state.</summary>
    public static void Reset()
    {
        _head = 0;
        _count = 0;
        _dropped = 0;
    }

    /// <summary>
    /// Record one sample. Called from the LAPIC timer interrupt handler;
    /// must stay allocation-free and branch-simple.
    /// </summary>
    public static void Sample(ulong rip)
    {
        if (!_enabled || _samples == null)
            return;
        _samples[_head] = rip;
        _head++;
        if (_head >= Capacity)
            _head = 0;
        if (_count < Capacity)
            _count++;
        else
            _dropped++;
    }

    /// <summary>
    /// Print the recorded profile: totals plus the top 64-byte buckets,
    /// each annotated with the nearest Tier-0 JIT symbol when the
    /// registry can resolve one.
    /// </summary>
    public static void Format(TextWriter w)
    {
        w.WriteLine("[perf] NeutrinoOS kernel sampling profiler");
        w.Write("[perf] samples in ring: ");
        w.Write((long)_count);
        w.Write("   dropped: ");
        w.Write((long)_dropped);
        w.Write("   state: ");
        w.WriteLine(_enabled ? "running" : "stopped");
        w.Write("[perf] window: start=");
        w.Write((long)_startedMs);
        w.Write("ms  now=");
        w.Write((long)UptimeMs());
        w.WriteLine("ms");

        if (_count == 0)
        {
            w.WriteLine("[perf] no samples - run `perf start`, then workload, then `perf stop`");
            return;
        }

        // Histogram: keep the hottest MaxTop 64-byte buckets.
        ulong[] keys = new ulong[MaxTop];
        int[] counts = new int[MaxTop];
        int used = 0;
        for (int i = 0; i < _count; i++)
        {
            ulong rip = _samples[i];
            if (rip == 0)
                continue;
            ulong key = rip & BucketMask;
            int slot = -1;
            for (int k = 0; k < used; k++)
            {
                if (keys[k] == key)
                {
                    slot = k;
                    break;
                }
            }
            if (slot >= 0)
            {
                counts[slot]++;
            }
            else if (used < MaxTop)
            {
                keys[used] = key;
                counts[used] = 1;
                used++;
            }
            else
            {
                // Replace the current minimum when this bucket is hotter.
                int min = 0;
                for (int k = 1; k < MaxTop; k++)
                {
                    if (counts[k] < counts[min])
                        min = k;
                }
                if (counts[min] <= 1)
                {
                    keys[min] = key;
                    counts[min] = 1;
                }
            }
        }

        // Selection sort (descending) over at most 16 entries.
        for (int a = 0; a < used; a++)
        {
            int best = a;
            for (int b = a + 1; b < used; b++)
            {
                if (counts[b] > counts[best])
                    best = b;
            }
            if (best != a)
            {
                int tc = counts[a]; counts[a] = counts[best]; counts[best] = tc;
                ulong tk = keys[a]; keys[a] = keys[best]; keys[best] = tk;
            }
        }

        w.WriteLine("[perf] hottest code ranges (64-byte buckets):");
        for (int i = 0; i < used; i++)
        {
            int pct10 = counts[i] * 1000 / _count; // tenths of a percent
            w.Write("  ");
            w.Write((long)counts[i]);
            w.Write(" samples (");
            w.Write((long)(pct10 / 10));
            w.Write(".");
            w.Write((long)(pct10 % 10));
            w.Write("%)  ");
            w.Write(Hex(keys[i]));
            w.Write("  ");
            w.WriteLine(Symbol(keys[i]));
        }
    }

    /// <summary>Best-effort symbol for an address: JIT registry lookup.</summary>
    private static string Symbol(ulong address)
    {
        uint token;
        uint asmId;
        if (CompiledMethodRegistry.TryFindByAddress(address, out token, out asmId))
            return "(jit asm=" + Dec(asmId) + " tok=" + Hex(token) + ")";
        return "(aot/unknown - see tools/symlook.py)";
    }

    private static ulong UptimeMs()
    {
        // APIC tick count is 1 ms resolution; avoid HPET MMIO in Format loops.
        return ProtonOS.X64.APIC.TickCount;
    }

    private static string Hex(ulong value)
    {
        return "0x" + value.ToString("X16", null);
    }

    private static string Dec(uint value)
    {
        return value.ToString();
    }
}
