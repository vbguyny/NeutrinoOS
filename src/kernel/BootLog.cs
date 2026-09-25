// NeutrinoOS boot progress status
// Prints timestamped "[Boot] t=<ms>ms <message>" lines so that a boot in
// progress is observable on the serial console. This matters on slow
// hypervisors (e.g. VirtualBox under NEM) where a full boot takes minutes
// and appears "stuck" without periodic status output.

using ProtonOS.Platform;
using ProtonOS.Arch;

namespace ProtonOS;

public static class BootLog
{
    private static ulong _startNs;
    private static bool _started;

    /// <summary>Maximum number of recorded boot stages.</summary>
    private const int MaxStages = 32;
    private static string[] _stageNames;
    private static ulong[] _stageMs;
    private static int _stageCount;

    /// <summary>
    /// Lazily allocate the stage tables (static array initializers are
    /// avoided: bflat's TypePreinit pass rejects field initializers in
    /// the kernel assembly).
    /// </summary>
    private static void EnsureStages()
    {
        if (_stageNames == null)
        {
            _stageNames = new string[MaxStages];
            _stageMs = new ulong[MaxStages];
        }
    }

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

    /// <summary>
    /// Record a boot stage with its timestamp (visible through the
    /// `boottime` shell built-in) and print the usual status line.
    /// </summary>
    public static void Stage(string name)
    {
        Start();
        EnsureStages();
        if (_stageCount < MaxStages)
        {
            _stageNames[_stageCount] = name;
            _stageMs[_stageCount] = ElapsedMs();
            _stageCount++;
        }
        Status(name);
    }

    /// <summary>Print "[Boot] t=&lt;ms&gt;ms &lt;message&gt;" (and record it as a stage).</summary>
    public static void Status(string message)
    {
        // Every status line during boot is a stage marker; fold it into
        // the timeline so `boottime` can reproduce the full sequence.
        Start();
        EnsureStages();
        if (_stageCount < MaxStages && !IsStageRecorded(message))
        {
            _stageNames[_stageCount] = message;
            _stageMs[_stageCount] = ElapsedMs();
            _stageCount++;
        }
        DebugConsole.Write("[Boot] t=");
        DebugConsole.WriteDecimal(ElapsedMs());
        DebugConsole.Write("ms ");
        DebugConsole.WriteLine(message);
    }

    private static bool IsStageRecorded(string message)
    {
        for (int i = 0; i < _stageCount; i++)
        {
            if (_stageNames[i] == message)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Print the recorded boot timeline (one line per stage) followed by
    /// the total time from boot-timer start to the last stage.
    /// </summary>
    public static void FormatTimeline(System.IO.TextWriter w)
    {
        EnsureStages();
        if (_stageCount == 0)
        {
            w.WriteLine("[boottime] no stages recorded");
            return;
        }
        w.WriteLine("[boottime] NeutrinoOS boot timeline (ms since boot timer start)");
        for (int i = 0; i < _stageCount; i++)
        {
            w.Write("  ");
            w.Write((long)_stageMs[i]);
            w.Write(" ms  ");
            w.WriteLine(_stageNames[i]);
        }
        w.Write("[boottime] last stage at ");
        w.Write((long)_stageMs[_stageCount - 1]);
        w.WriteLine(" ms");
    }
}
