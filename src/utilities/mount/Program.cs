// NeutrinoOS utility: mount - list/mount VFS filesystems.
//
// usage: mount                                  - list the VFS mount table
//        mount [-t exfat] [-o opts] <device> <path>
//   -t exfat    filesystem type (exFAT; the default and only type)
//   -o opts     comma-separated mount options:
//                 ro, rw, uid=, gid=, umask=, iocharset=utf8|ascii
//
// Device names come from the kernel block device registry
// (hda/hdb..., nvme0, sda...). The exFAT driver lives in the DDK, so
// mounts created here are visible to every utility and (through the
// kernel VFS exports) to the shell. USB sticks auto-mount under
// /mnt/usb/<device> - this command covers everything else.

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Storage;
using ProtonOS.DDK.Storage.ExFat;

namespace NeutrinoOS.Utility.Mount;

/// <summary>The mount utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 on failure.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: mount [-t exfat] [-o opts] <device> <path>",
                "       mount",
                "  With no arguments, lists the VFS mount table.",
                "  -o opts: ro, rw, uid=N, gid=N, umask=N, iocharset=utf8|ascii",
                "  Example: mount -o ro /dev/hdb /mnt/recovery");
        }

        // Parse optional flags, then device + path.
        string type = "exfat";
        string options = "";
        string device = "";
        string path = "";
        int pos = 0;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "-t" && i + 1 < args.Length)
                type = args[++i];
            else if (a == "-o" && i + 1 < args.Length)
                options = args[++i];
            else if (a.Length > 0 && a[0] == '-')
                return Util.Fail("mount", "unknown option: " + a);
            else if (pos == 0)
            {
                device = a;
                pos++;
            }
            else
            {
                path = a;
                pos++;
            }
        }

        if (pos == 0)
            return List();

        if (pos != 2)
            return Util.Fail("mount", "usage: mount [-t exfat] [-o opts] <device> <path>");

        if (!Streq(type, "exfat"))
            return Util.Fail("mount", "unsupported filesystem type: " + type + " (only exfat)");

        string devName = StripDevPrefix(device);
        var info = BlockDevices.Find(devName);
        if (info == null)
            return Util.Fail("mount", "no such device: " + device);

        var dev = new KernelBlockDevice(info);
        var fs = new ExFatFileSystem();
        fs.Initialize();

        string? optError = fs.SetMountOptions(options);
        if (optError != null)
        {
            fs.Shutdown();
            return Util.Fail("mount", "bad option: " + optError);
        }

        bool readOnly = HasToken(options, "ro");

        // Best-effort mount point creation (mkdir -p semantics).
        string parent = VFS.GetDirectory(path);
        if (parent.Length > 1 && parent != path)
            VFS.CreateDirectory(parent);
        VFS.CreateDirectory(path);

        var rc = VFS.Mount(path, fs, dev, readOnly);
        if (rc != FileResult.Success)
        {
            fs.Shutdown();
            return Util.Fail("mount", (path.Length == 0 ? "(empty)" : path) + ": " + ResultText(rc));
        }

        Console.Write("mounted ");
        Console.Write(devName);
        Console.Write(" (exFAT");
        if (fs.VolumeLabel != null && fs.VolumeLabel.Length > 0)
        {
            Console.Write(", label \"");
            Console.Write(fs.VolumeLabel);
            Console.Write("\"");
        }
        Console.Write(readOnly ? ", read-only" : ", read-write");
        Console.Write(", ");
        Console.Write((long)(fs.FreeBytes / 1024));
        Console.Write(" KB free) at ");
        Console.WriteLine(path);
        return 0;
    }

    /// <summary>Lists the VFS mount table and the boot volume.</summary>
    private static int List()
    {
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
                Console.Write("  ");
                Console.Write(mp.FileSystem.FilesystemName);
                if (mp.Device != null)
                {
                    Console.Write(" on /dev/");
                    Console.Write(mp.Device.DeviceName);
                }
                Console.Write(mp.IsReadOnly ? "  (read-only)" : "  (read-write)");
                Console.WriteLine();
            }
        }

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

    /// <summary>Strips a /dev/ prefix from a device argument.</summary>
    private static string StripDevPrefix(string device)
    {
        if (device.Length > 5 && device[0] == '/' && device[1] == 'd' && device[2] == 'e' && device[3] == 'v' && device[4] == '/')
            return device.Substring(5);
        return device;
    }

    /// <summary>Token containment test over a comma-separated option string.</summary>
    private static bool HasToken(string options, string token)
    {
        int start = 0;
        for (int i = 0; i <= options.Length; i++)
        {
            if (i == options.Length || options[i] == ',')
            {
                int len = i - start;
                if (len == token.Length && Matches(options, start, token))
                    return true;
                start = i + 1;
            }
        }
        return false;
    }

    /// <summary>Case-insensitive compare of s[start..start+token.Length] against token.</summary>
    private static bool Matches(string s, int start, string token)
    {
        for (int i = 0; i < token.Length; i++)
        {
            char a = s[start + i];
            char b = token[i];
            if (a >= 'A' && a <= 'Z')
                a = (char)(a + 32);
            if (a != b)
                return false;
        }
        return true;
    }

    /// <summary>Case-insensitive string equality (JIT-safe).</summary>
    private static bool Streq(string a, string b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            char x = a[i];
            char y = b[i];
            if (x >= 'A' && x <= 'Z')
                x = (char)(x + 32);
            if (y >= 'A' && y <= 'Z')
                y = (char)(y + 32);
            if (x != y)
                return false;
        }
        return true;
    }

    /// <summary>Human-readable text for a mount failure.</summary>
    private static string ResultText(FileResult rc)
    {
        if (rc == FileResult.AlreadyExists)
            return "already a mount point";
        if (rc == FileResult.NotFound)
            return "not found";
        if (rc == FileResult.AccessDenied)
            return "access denied";
        if (rc == FileResult.IoError)
            return "I/O error";
        if (rc == FileResult.NotSupported)
            return "not supported";
        return "mount failed (rc=" + ((int)rc).ToString() + ")";
    }
}
