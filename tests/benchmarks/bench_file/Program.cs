// NeutrinoOS Phase 7 benchmark: file I/O throughput (boot FAT32 volume)
// Writes a 1 MB file in one WriteAllBytes call, reads it back in one
// ReadAllBytes call and reports MB/s for both directions.
using System;
using System.Diagnostics;
using System.IO;

namespace NeutrinoOS.Benchmarks.FileIo;

/// <summary>File I/O throughput benchmark (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; prints write/read throughput for a 1 MB file.</summary>
    public static int Main(string[] args)
    {
        const int SizeBytes = 1024 * 1024;
        Console.WriteLine("[bench] file I/O throughput (1 MB on the boot FAT32 volume)");

        byte[] data = new byte[SizeBytes];
        for (int i = 0; i < SizeBytes; i += 4096)
            data[i] = (byte)(i >> 12);

        Stopwatch sw = Stopwatch.StartNew();
        File.WriteAllBytes("/bench.tmp", data);
        long writeMs = sw.ElapsedMilliseconds;

        sw.Restart();
        byte[] readBack = File.ReadAllBytes("/bench.tmp");
        long readMs = sw.ElapsedMilliseconds;

        bool ok = readBack.Length == SizeBytes;
        Console.WriteLine("[bench] write: 1024 KB in " + writeMs + " ms -> "
            + (writeMs > 0 ? 1024 / writeMs : 0) + " MB/s");
        Console.WriteLine("[bench] read:  1024 KB in " + readMs + " ms -> "
            + (readMs > 0 ? 1024 / readMs : 0) + " MB/s  (verify=" + (ok ? "ok" : "MISMATCH") + ")");
        return ok ? 0 : 1;
    }
}
