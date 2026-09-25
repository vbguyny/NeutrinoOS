// NeutrinoOS Phase 7 - virtual /dev character files
//
// Served by Platform.FileExports before the FAT driver is consulted:
//
//   /dev/profiler  read      text report from the kernel sampling
//                            profiler (see `perf`, PHASE7-PROFILING.md)
//   /dev/random    read      one-shot block of kernel entropy
//   /dev/urandom   read      same source as /dev/random
//
// The files are read-only and have no durable state; writes are
// rejected (FileBootWrite path is not intercepted).

using ProtonOS.Exports.DDK;
using ProtonOS.Profiling;

namespace ProtonOS.Platform;

/// <summary>Virtual /dev character files (see file header).</summary>
public static unsafe class VirtualDevices
{
    /// <summary>Bytes served by one /dev/random read.</summary>
    private const int RandomBlockSize = 4096;

    // /dev/profiler renders on demand; Size() caches the rendering so
    // the korlib ReadAllBytes sequence (size probe, then read) sees a
    // consistent snapshot.
    private static string _profilerText;

    // /dev/netstats follows the same render-cache pattern.
    private static string _netstatsText;

    /// <summary>True when the path names a virtual device this module serves.</summary>
    public static bool IsVirtual(char* path, int pathLen)
    {
        return Matches(path, pathLen, "/dev/profiler") ||
               Matches(path, pathLen, "/dev/random") ||
               Matches(path, pathLen, "/dev/urandom") ||
               Matches(path, pathLen, "/dev/netstats");
    }

    /// <summary>Existence probe used by FileBootExists.</summary>
    public static int Exists(char* path, int pathLen)
    {
        return IsVirtual(path, pathLen) ? 1 : 0;
    }

    /// <summary>Size probe used by FileBootSize (-1 = not served).</summary>
    public static int Size(char* path, int pathLen)
    {
        if (Matches(path, pathLen, "/dev/random") || Matches(path, pathLen, "/dev/urandom"))
            return RandomBlockSize;
        if (Matches(path, pathLen, "/dev/profiler"))
        {
            RenderProfiler();
            return _profilerText.Length;
        }
        if (Matches(path, pathLen, "/dev/netstats"))
        {
            RenderNetStats();
            return _netstatsText.Length;
        }
        return -1;
    }

    /// <summary>
    /// Read handler used by FileBootRead. Returns the number of bytes
    /// written to <paramref name="buffer"/>, or -1 when the path is not
    /// a virtual device.
    /// </summary>
    public static int Read(char* path, int pathLen, byte* buffer, int capacity)
    {
        if (buffer == null || capacity <= 0)
            return -1;

        if (Matches(path, pathLen, "/dev/random") || Matches(path, pathLen, "/dev/urandom"))
        {
            int n = capacity < RandomBlockSize ? capacity : RandomBlockSize;
            return EntropyExports.FillEntropy(buffer, n);
        }

        if (Matches(path, pathLen, "/dev/profiler"))
        {
            if (_profilerText == null)
                RenderProfiler();
            return CopyText(_profilerText, buffer, capacity);
        }

        if (Matches(path, pathLen, "/dev/netstats"))
        {
            if (_netstatsText == null)
                RenderNetStats();
            return CopyText(_netstatsText, buffer, capacity);
        }

        return -1;
    }

    private static int CopyText(string text, byte* buffer, int capacity)
    {
        int len = text.Length;
        if (len > capacity)
            len = capacity;
        for (int i = 0; i < len; i++)
        {
            buffer[i] = (byte)(text[i] & 0x7F);
        }
        return len;
    }

    private static void RenderProfiler()
    {
        System.IO.StringWriter sw = new System.IO.StringWriter();
        Profiler.Format(sw);
        _profilerText = sw.ToString();
    }

    private static void RenderNetStats()
    {
        ulong framesIn, framesOut, bytesIn, bytesOut, txErrors;
        Exports.DDK.NetworkExports.GetFrameStats(out framesIn, out framesOut,
                                                 out bytesIn, out bytesOut, out txErrors);
        System.IO.StringWriter sw = new System.IO.StringWriter();
        sw.WriteLine("[netstats] NeutrinoOS interface counters (virtio-net frames)");
        sw.Write("[netstats] frames in: ");
        sw.Write((long)framesIn);
        sw.Write("   frames out: ");
        sw.Write((long)framesOut);
        sw.Write("   tx errors: ");
        sw.WriteLine((long)txErrors);
        sw.Write("[netstats] bytes in: ");
        sw.Write((long)bytesIn);
        sw.Write("   bytes out: ");
        sw.WriteLine((long)bytesOut);
        sw.Write("[netstats] uptime ms: ");
        sw.WriteLine((long)ProtonOS.Arch.APIC.TickCount);
        sw.WriteLine("[netstats] protocol counters (TCP/UDP/ICMP): run `netstat -s`");
        _netstatsText = sw.ToString();
    }

    /// <summary>UTF-16 path compare (exact match).</summary>
    private static bool Matches(char* path, int pathLen, string candidate)
    {
        if (path == null || pathLen != candidate.Length)
            return false;
        for (int i = 0; i < pathLen; i++)
        {
            if (path[i] != candidate[i])
                return false;
        }
        return true;
    }
}
