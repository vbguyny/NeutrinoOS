// NeutrinoOS utility: df - filesystem usage.
//
// usage: df [-T]
//   -T  accepted for POSIX compatibility; the type column is always
//       shown ("Filesystem Type ... Mounted on").
//
// Lists every mounted volume: the boot (FAT32) volume served by the
// AHCI driver, then each VFS mount point (exFAT sticks, procfs, ...)
// with size, used, free (KB) and use percentage.

using System;
using NeutrinoOS.Utils;
using NeutrinoOS.DDK.Kernel;
using NeutrinoOS.DDK.Storage;

namespace NeutrinoOS.Utility.Df;

/// <summary>The df utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 when no filesystem info is available.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        bool types = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-T")
                types = true;
            else if (args[i] == "--help" || args[i] == "-h")
            {
                return Util.Help(
                    "usage: df [-T]",
                    "  Show usage for the boot volume and every VFS mount",
                    "  (exFAT USB sticks, procfs, ...). -T is accepted;",
                    "  the filesystem type column is always shown.");
            }
            else
                return Util.Fail("df", "unknown option: " + args[i]);
        }
        _ = types;

        Console.WriteLine("Filesystem        Type   Size      Used     Avail  Use%  Mounted on");

        int rows = 0;

        // Boot (FAT32) volume: served by the AHCI driver's on-demand
        // helpers rather than the VFS table.
        if (SysInfo.TryGetBootVolumeStats(out string label, out ulong totalBytes, out ulong freeBytes))
        {
            if (label.Length == 0)
                label = "NEUTRINOOS";
            WriteRow(label, "fat32", totalBytes, freeBytes, "/boot");
            rows++;
        }

        // VFS mount points (exFAT, procfs, ...).
        var mounts = VFS.MountPoints;
        for (int i = 0; i < mounts.Count; i++)
        {
            MountPoint mp = mounts[i];
            string name = mp.FileSystem.VolumeLabel ?? "";
            if (name.Length == 0)
            {
                if (mp.Device != null)
                    name = "/dev/" + mp.Device.DeviceName;
                else
                    name = "-";
            }
            string type = mp.FileSystem.FilesystemName;
            WriteRow(name, type, mp.FileSystem.TotalBytes, mp.FileSystem.FreeBytes, mp.Path);
            rows++;
        }

        if (rows == 0)
        {
            return Util.Fail("df", "no filesystem statistics available (driver not bound?)");
        }

        Console.WriteLine();
        Console.Write("(");
        Console.Write(rows.ToString());
        Console.WriteLine(" filesystems; sizes in KB; df -T accepted)");
        return 0;
    }

    /// <summary>Writes one aligned usage row.</summary>
    private static void WriteRow(string name, string type, ulong totalBytes, ulong freeBytes, string mountPath)
    {
        ulong usedBytes = totalBytes >= freeBytes ? totalBytes - freeBytes : 0;
        int usedPercent = totalBytes == 0 ? 0 : (int)(usedBytes * 100 / totalBytes);

        var sb = new System.Text.StringBuilder();
        sb.Append(Fit(name, 16));
        sb.Append("  ");
        sb.Append(Fit(type, 5));
        sb.Append("  ");
        sb.Append(Util.PadLeft((long)(totalBytes / 1024), 8));
        sb.Append("  ");
        sb.Append(Util.PadLeft((long)(usedBytes / 1024), 8));
        sb.Append("  ");
        sb.Append(Util.PadLeft((long)(freeBytes / 1024), 8));
        sb.Append("  ");
        sb.Append(Util.PadLeft(usedPercent, 3));
        sb.Append("%  ");
        sb.Append(mountPath);
        Console.WriteLine(sb.ToString());
    }

    /// <summary>Pads or truncates to an exact width.</summary>
    private static string Fit(string s, int width)
    {
        if (s.Length > width)
            return s.Substring(0, width);
        var sb = new System.Text.StringBuilder(s);
        for (int i = s.Length; i < width; i++)
            sb.Append(' ');
        return sb.ToString();
    }
}
