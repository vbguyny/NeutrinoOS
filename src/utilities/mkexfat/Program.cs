// NeutrinoOS Phase 10 utility: mkexfat - format a block device as exFAT.
//
// usage: mkexfat [-L label] [-c cluster] [-s sector] [-f fats] [-r rev] <device>
//   -L label    volume label (max 11 characters)
//   -c cluster  cluster size: 4096/8192/.../32M, or 4K/32K suffixes
//   -s sector   sector size: 512 or 4096 (default 512)
//   -f fats     number of FATs: 1 or 2 (default 1)
//   -r rev      filesystem revision: 1.0 (default)
//
// Writes a complete spec-conformant volume (Task 5) and self-checks the
// result before returning. Device names come from the kernel block
// device registry (hda/hdb..., nvme0, sda...); see also `mount`.

using System;
using NeutrinoOS.Utils;
using NeutrinoOS.DDK.Storage;
using NeutrinoOS.DDK.Storage.ExFat;

namespace NeutrinoOS.Utility.MkExFat;

/// <summary>The mkexfat utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 on failure.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
        {
            return Util.Help(
                "usage: mkexfat [-L label] [-c cluster] [-s sector] [-f fats] <device>",
                "  -L label    volume label (max 11 characters)",
                "  -c cluster  cluster size in bytes (4K..32M, suffixes K/M)",
                "  -s sector   sector size: 512 or 4096 (default 512)",
                "  -f fats     number of FATs: 1 or 2 (default 1)",
                "  Devices: " + DeviceList());
        }

        var options = new ExFatFormatOptions();
        string device = "";
        int i = 0;
        while (i < args.Length)
        {
            string a = args[i];
            if (a == "-L" && i + 1 < args.Length)
            {
                options.Label = args[++i];
            }
            else if (a == "-c" && i + 1 < args.Length)
            {
                int v = ParseSize(args[++i]);
                if (v <= 0)
                    return Util.Fail("mkexfat", "bad cluster size: " + args[i]);
                options.ClusterSizeBytes = v;
            }
            else if (a == "-s" && i + 1 < args.Length)
            {
                int v = ParseSize(args[++i]);
                if (v != 512 && v != 4096)
                    return Util.Fail("mkexfat", "sector size must be 512 or 4096");
                options.SectorSizeBytes = v;
            }
            else if (a == "-f" && i + 1 < args.Length)
            {
                int v = ParseSize(args[++i]);
                if (v != 1 && v != 2)
                    return Util.Fail("mkexfat", "number of FATs must be 1 or 2");
                options.NumberOfFats = v;
            }
            else if (a == "-r" && i + 1 < args.Length)
            {
                string rev = args[++i];
                if (rev == "1.0" || rev == "1")
                    options.Revision = 0x0100;
                else
                    return Util.Fail("mkexfat", "only revision 1.0 is supported");
            }
            else if (a.Length > 0 && a[0] == '-')
            {
                return Util.Fail("mkexfat", "unknown option: " + a);
            }
            else
            {
                device = a;
            }
            i++;
        }

        if (device.Length == 0)
            return Util.Fail("mkexfat", "no device specified (devices: " + DeviceList() + ")");

        var info = BlockDevices.Find(device);
        if (info == null)
            return Util.Fail("mkexfat", "no such device: " + device + " (devices: " + DeviceList() + ")");

        Console.Write("formatting ");
        Console.Write(info.Name);
        Console.Write(" as exFAT");
        if (options.Label.Length > 0)
        {
            Console.Write(" label=\"");
            Console.Write(options.Label);
            Console.Write("\"");
        }
        Console.WriteLine(" ...");

        var dev = new KernelBlockDevice(info);
        int rc = ExFatFormatter.Format(dev, options);
        if (rc != 0)
            return Util.Fail("mkexfat", "format failed (rc=" + rc.ToString() + ")");

        Console.Write("formatted ");
        Console.Write(info.Name);
        Console.Write(" (exFAT, ");
        Console.Write((long)(info.SectorCount * info.SectorSize / 1024));
        Console.WriteLine(" KB, verified)");
        return 0;
    }

    /// <summary>Parses "4096", "4K", "32K", "1M" size forms.</summary>
    private static int ParseSize(string s)
    {
        if (s.Length == 0)
            return -1;
        int mult = 1;
        char last = s[s.Length - 1];
        if (last == 'K' || last == 'k')
        {
            mult = 1024;
            s = s.Substring(0, s.Length - 1);
        }
        else if (last == 'M' || last == 'm')
        {
            mult = 1024 * 1024;
            s = s.Substring(0, s.Length - 1);
        }
        int v = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9')
                return -1;
            v = v * 10 + (c - '0');
            if (v > 64 * 1024 * 1024)
                return -1;
        }
        return v * mult;
    }

    /// <summary>Comma-separated device names for messages.</summary>
    private static string DeviceList()
    {
        var all = BlockDevices.Enumerate();
        if (all.Length == 0)
            return "(none)";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < all.Length; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(all[i].Name);
        }
        return sb.ToString();
    }
}
