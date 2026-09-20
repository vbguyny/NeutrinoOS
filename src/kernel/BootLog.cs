// NeutrinoOS boot progress status
// Prints timestamped "[Boot] t=<ms>ms <message>" lines so that a boot in
// progress is observable on the serial console. This matters on slow
// hypervisors (e.g. VirtualBox under NEM) where a full boot takes minutes
// and appears "stuck" without periodic status output.

using ProtonOS.Platform;
using ProtonOS.X64;

namespace ProtonOS;

public static class BootLog
{
    private static ulong _startNs;
    private static bool _started;

    /// <summary>
    /// Start the boot timer (no-op if already started or if the HPET is
    /// not initialized yet; Status() retries lazily).
    /// </summary>
    public static void Start()
    {
        if (_started || !HPET.IsInitialized)
            return;
        _startNs = HPET.TicksToNanoseconds(HPET.ReadCounter());
        _started = true;
    }

    /// <summary>Milliseconds elapsed since Start() (0 if not started).</summary>
    public static ulong ElapsedMs()
    {
        if (!_started)
            return 0;
        ulong now = HPET.TicksToNanoseconds(HPET.ReadCounter());
        return (now - _startNs) / 1_000_000;
    }

    /// <summary>Print "[Boot] t=&lt;ms&gt;ms &lt;message&gt;".</summary>
    public static void Status(string message)
    {
        Start(); // lazy start in case the HPET came up after first use
        DebugConsole.Write("[Boot] t=");
        DebugConsole.WriteDecimal(ElapsedMs());
        DebugConsole.Write("ms ");
        DebugConsole.WriteLine(message);
    }
}
