// NeutrinoOS Phase 10 utility: fsck.exfat - check and repair exFAT.
//
// usage: fsck.exfat [-y] [-n] <device>
//   -y  repair automatically (fixable errors)
//   -n  check only (default; never writes)
//
// Runs the DDK check/repair engine: boot regions, up-case checksum,
// entry sets, chains, allocation bitmap, VolumeDirty/PercentInUse.
// Exit code 0 = clean or fully repaired, 1 = problems remain.

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Storage;
using ProtonOS.DDK.Storage.ExFat;

namespace NeutrinoOS.Utility.FsckExFat;

/// <summary>The fsck.exfat utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 when unrepaired problems remain.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;

        bool repair = false;
        string device = "";
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "-y")
                repair = true;
            else if (a == "-n")
                repair = false;
            else if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: fsck.exfat [-y] <device>",
                    "  -y  repair automatically (boot regions, checksums, bitmap,",
                    "      entry sets, lengths); without it the volume is only read.",
                    "  Example: fsck.exfat -y hdb");
            }
            else if (a.Length > 0 && a[0] == '-')
                return Util.Fail("fsck.exfat", "unknown option: " + a);
            else
                device = a;
        }

        if (device.Length == 0)
            return Util.Fail("fsck.exfat", "no device specified");

        var info = BlockDevices.Find(device);
        if (info == null)
            return Util.Fail("fsck.exfat", "no such device: " + device);

        Console.Write("fsck.exfat: checking ");
        Console.WriteLine(info.Name);
        if (repair)
            Console.WriteLine("mode: repair (-y)");

        var dev = new KernelBlockDevice(info);
        var result = new ExFatFsckResult();
        int rc = ExFatFsck.Run(dev, repair, result);

        Console.Write(result.Log.ToString());
        Console.Write("fsck.exfat: ");
        Console.Write(result.Errors.ToString());
        Console.Write(" error(s), ");
        Console.Write(result.Repaired.ToString());
        Console.Write(" repaired, ");
        Console.Write(result.Unrepaired.ToString());
        Console.WriteLine(" remaining");
        Console.WriteLine(rc == 0 ? "fsck.exfat: clean" : "fsck.exfat: PROBLEMS REMAIN");
        return rc;
    }
}
