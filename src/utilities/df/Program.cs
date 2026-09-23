// NeutrinoOS Phase 5 utility: df - filesystem usage
//
// usage: df
//   Shows the boot (FAT32) volume: label, total size, used and free
//   space (from the FAT driver's cluster accounting through
//   Kernel_GetBootVolumeStats) plus the active VFS mount points.

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Kernel;

namespace NeutrinoOS.Utility.Df;

/// <summary>The df utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 when the volume statistics are unavailable.</summary>
    public static int Main(string[] args)
    {
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: df",
                "  Show boot-volume usage (FAT32: label, size, used, free)");
        }
        if (args.Length > 0)
            return Util.Fail("df", "usage: df");

        if (!SysInfo.TryGetBootVolumeStats(out string label, out ulong totalBytes, out ulong freeBytes))
            return Util.Fail("df", "boot volume statistics unavailable (driver not bound?)");

        ulong usedBytes = totalBytes >= freeBytes ? totalBytes - freeBytes : 0;
        int usedPercent = totalBytes == 0 ? 0 : (int)(usedBytes * 100 / totalBytes);

        if (label.Length == 0)
            label = "NEUTRINOOS";

        Console.WriteLine("Filesystem        Size      Used     Avail  Use%  Mounted on");
        var sb = new System.Text.StringBuilder();
        sb.Append(PadRight(label, 12));
        sb.Append("  ");
        sb.Append(Util.PadLeft((long)(totalBytes / 1024), 8));
        sb.Append("  ");
        sb.Append(Util.PadLeft((long)(usedBytes / 1024), 8));
        sb.Append("  ");
        sb.Append(Util.PadLeft((long)(freeBytes / 1024), 8));
        sb.Append("  ");
        sb.Append(Util.PadLeft(usedPercent, 3));
        sb.Append("%  /boot (Ahci/Fat32)");
        Console.WriteLine(sb.ToString());

        Console.WriteLine();
        Console.WriteLine("(sizes in KB; the boot volume is mounted read-only by the");
        Console.WriteLine(" AHCI driver; mount lists the VFS mount table)");
        return 0;
    }

    private static string PadRight(string s, int width)
    {
        if (s.Length >= width)
            return s;
        var sb = new System.Text.StringBuilder(s);
        for (int i = s.Length; i < width; i++)
            sb.Append(' ');
        return sb.ToString();
    }
}
