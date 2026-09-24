// NeutrinoOS Phase 5 utility: mount - list mounted filesystems
//
// usage: mount                     - list VFS mount points
//        mount device path         - not supported in Phase 5
//
// The mount table lives in the DDK VFS (ProtonOS.DDK.Storage.VFS), which
// the AHCI driver and procfs populate at boot; utilities share the same
// loaded DDK assembly, so this reads the live table directly.

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Storage;

namespace NeutrinoOS.Utility.Mount;

/// <summary>The mount utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 for unsupported forms.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: mount [device path]",
                "  With no arguments, lists the VFS mount table (path + mode).",
                "  Mounting additional filesystems is deferred to a later phase.");
        }

        if (args.Length == 2)
        {
            Console.Error.WriteLine("neutrinoos: mount: mounting " + args[0] + " at " + args[1] + " is not supported in Phase 5");
            Console.Error.WriteLine("  (the boot volume is mounted by the AHCI driver at boot;");
            Console.Error.WriteLine("   device-path mounting is deferred - see docs/PHASE5-UTILITIES.md)");
            return 1;
        }

        if (args.Length > 0)
            return Util.Fail("mount", "usage: mount [device path]");

        var mounts = VFS.MountPoints;
        Console.WriteLine("VFS mount points (" + mounts.Count + "):");
        if (mounts.Count == 0)
        {
            Console.WriteLine("  (none)");
        }
        else
        {
            for (int i = 0; i < mounts.Count; i++)
            {
                MountPoint mp = mounts[i];
                Console.Write("  ");
                Console.Write(mp.Path);
                Console.Write(mp.IsReadOnly ? "  (read-only)" : "  (read-write)");
                Console.WriteLine();
            }
        }

        // The boot (FAT32) volume is served by the AHCI driver's
        // on-demand helpers rather than the VFS table (the ext2 root
        // mount cannot apply to a FAT image, so /boot registration is
        // skipped at boot); report it so the listing matches `df`.
        Console.WriteLine();
        if (SysInfo.TryGetBootVolumeStats(out string label, out ulong totalBytes, out ulong freeBytes))
        {
            Console.Write("boot volume: ");
            Console.Write(label.Length > 0 ? label : "NEUTRINOOS");
            Console.Write(" (FAT32, ");
            Console.Write((long)(totalBytes / 1024));
            Console.Write(" KB total, ");
            Console.Write((long)(freeBytes / 1024));
            Console.WriteLine(" KB free) - AHCI boot disk, read-only");
        }
        else
        {
            Console.WriteLine("boot volume: not available (driver not bound)");
        }
        return 0;
    }
}
