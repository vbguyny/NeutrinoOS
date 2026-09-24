// NeutrinoOS Phase 7 benchmark: file I/O throughput (boot FAT32 volume)
// Writes 64 KB / 256 KB / 1 MB files in separate WriteAllBytes calls (whole
// open+write+close timed per call) so the per-cluster device cost can be
// separated from the fixed per-file metadata/writeback cost, then reads the
// 1 MB file back and verifies it.
using System;
using System.Diagnostics;
using System.IO;

namespace NeutrinoOS.Benchmarks.FileIo;

/// <summary>File I/O throughput benchmark (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; prints size-sweep write/read throughput.</summary>
    public static int Main(string[] args)
    {
        const int SizeBytes = 1024 * 1024;

        Console.WriteLine("[bench] file I/O throughput (boot FAT32 volume, size sweep)");

        byte[] data = new byte[SizeBytes];
        for (int i = 0; i < SizeBytes; i += 4096)
            data[i] = (byte)(i >> 12);

        int[] sizes = new int[] { 64 * 1024, 256 * 1024, 1024 * 1024 };
        string[] names = new string[] { "/b64.tmp", "/b256.tmp", "/bench.tmp" };
        long[] times = new long[3];

        for (int s = 0; s < 3; s++)
        {
            Stopwatch sw = Stopwatch.StartNew();
            File.WriteAllBytes(names[s], data.Length == sizes[s] ? data : SubArray(data, sizes[s]));
            times[s] = sw.ElapsedMilliseconds;
            Console.WriteLine("[bench] write " + (sizes[s] / 1024) + " KB in " + times[s] + " ms");
        }

        // --- Read back the large file (whole-file ReadAllBytes) ---
        Stopwatch swr = Stopwatch.StartNew();
        byte[] readBack = File.ReadAllBytes("/bench.tmp");
        long readMs = swr.ElapsedMilliseconds;

        bool ok = readBack.Length == SizeBytes;
        long writeMs = times[2];
        Console.WriteLine("[bench] write: 1024 KB in " + writeMs + " ms -> "
            + (writeMs > 0 ? 1024 / writeMs : 0) + " MB/s");
        Console.WriteLine("[bench] read:  1024 KB in " + readMs + " ms -> "
            + (readMs > 0 ? 1024 / readMs : 0) + " MB/s  (verify=" + (ok ? "ok" : "MISMATCH") + ")");
        return ok ? 0 : 1;
    }

    /// <summary>Copies the first count bytes of data into a new array.</summary>
    private static byte[] SubArray(byte[] data, int count)
    {
        byte[] result = new byte[count];
        for (int i = 0; i < count; i++)
            result[i] = data[i];
        return result;
    }
}
