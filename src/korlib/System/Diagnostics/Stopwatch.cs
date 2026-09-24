// NeutrinoOS korlib - System.Diagnostics.Stopwatch
//
// Phase 7: high-resolution stopwatch for benchmarks and applications.
// The timestamp source is the kernel uptime clock in nanoseconds
// (HPET-backed, monotonic) reached through the kernel bridge method
// StopwatchBootNs, which is bound to the kernel export of the same
// name by the token registry in the AOT kernel.
//
// BCL semantics kept: Start/Stop/Reset/Restart, IsRunning, Elapsed in
// ticks (100 ns), ElapsedMilliseconds, ElapsedMicroseconds (Phase 7
// extension), GetTimestamp/Frequency/IsHighResolution statics.

using System.Runtime.InteropServices;

namespace System.Diagnostics
{
    /// <summary>
    /// Provides a high-resolution, monotonic stopwatch (see the file header).
    /// </summary>
    public class Stopwatch
    {
        // ==================== Kernel bridge ====================
#if KORLIB_IL
        // IL metadata stubs. The AOT kernel provides the real
        // implementation (StopwatchBootNs export); JIT-compiled code
        // resolves this token to the native address.
        private static ulong StopwatchBootNs() => throw new PlatformNotSupportedException();
#else
        [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong StopwatchBootNs();
#endif

        /// <summary>Nanoseconds per elapsed tick (100 ns per BCL tick).</summary>
        private const ulong NanosecondsPerTick = 100;

        private ulong _startNs;
        private ulong _elapsedNs;
        private bool _running;

        /// <summary>Starts (or resumes) measuring elapsed time.</summary>
        public void Start()
        {
            if (_running)
                return;
            _startNs = StopwatchBootNs();
            _running = true;
        }

        /// <summary>Stops measuring elapsed time.</summary>
        public void Stop()
        {
            if (!_running)
                return;
            _elapsedNs += StopwatchBootNs() - _startNs;
            _running = false;
        }

        /// <summary>Stops and zeroes the elapsed time.</summary>
        public void Reset()
        {
            _elapsedNs = 0;
            _startNs = StopwatchBootNs();
        }

        /// <summary>Stops, zeroes and restarts measuring elapsed time.</summary>
        public void Restart()
        {
            _elapsedNs = 0;
            _startNs = StopwatchBootNs();
            _running = true;
        }

        /// <summary>True while the stopwatch is running.</summary>
        public bool IsRunning => _running;

        /// <summary>Total elapsed nanoseconds (Phase 7 extension).</summary>
        public ulong ElapsedNanoseconds
        {
            get
            {
                ulong total = _elapsedNs;
                if (_running)
                    total += StopwatchBootNs() - _startNs;
                return total;
            }
        }

        /// <summary>Elapsed time in BCL ticks (100 ns).</summary>
        public long ElapsedTicks => (long)(ElapsedNanoseconds / NanosecondsPerTick);

        /// <summary>Elapsed time in whole milliseconds.</summary>
        public long ElapsedMilliseconds => (long)(ElapsedNanoseconds / 1_000_000);

        /// <summary>Elapsed time in whole microseconds (Phase 7 extension).</summary>
        public long ElapsedMicroseconds => (long)(ElapsedNanoseconds / 1_000);

        /// <summary>Elapsed time as a TimeSpan.</summary>
        public TimeSpan Elapsed => TimeSpan.FromTicks(ElapsedTicks);

        /// <summary>Creates and starts a new stopwatch.</summary>
        public static Stopwatch StartNew()
        {
            Stopwatch sw = new Stopwatch();
            sw.Start();
            return sw;
        }

        /// <summary>Raw monotonic timestamp in nanoseconds.</summary>
        public static long GetTimestamp() => (long)StopwatchBootNs();

        /// <summary>Timestamp units per second (nanoseconds).</summary>
        public static readonly long Frequency = 1_000_000_000;

        /// <summary>Always true on NeutrinoOS (nanosecond clock).</summary>
        public static bool IsHighResolution => true;
    }
}
