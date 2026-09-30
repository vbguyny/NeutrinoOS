// NeutrinoOS Phase 10 utility: exfatlabel - read or set a volume label.
//
// usage: exfatlabel <device>            - print the label
//        exfatlabel <device> <label>    - set the label (max 11 chars)
//        exfatlabel <device> ""         - clear the label

using System;
using NeutrinoOS.Utils;
using NeutrinoOS.DDK.Storage;
using NeutrinoOS.DDK.Storage.ExFat;

namespace NeutrinoOS.Utility.ExFatLabel;

/// <summary>The exfatlabel utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 on failure.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        if (args.Length == 0 || args.Length > 2 || args[0] == "--help" || args[0] == "-h")
        {
            return Util.Help(
                "usage: exfatlabel <device> [label]",
                "  Without a label argument, prints the current volume label.",
                "  With one, sets it (max 11 characters; \"\" clears it).");
        }

        var info = BlockDevices.Find(args[0]);
        if (info == null)
            return Util.Fail("exfatlabel", "no such device: " + args[0]);

        var dev = new KernelBlockDevice(info);
        if (args.Length == 1)
        {
            string? label = NeutrinoOS.DDK.Storage.ExFat.ExFatLabel.Get(dev);
            if (label == null)
                return Util.Fail("exfatlabel", info.Name + ": not an exFAT volume");
            Console.WriteLine(label);
            return 0;
        }

        int rc = NeutrinoOS.DDK.Storage.ExFat.ExFatLabel.Set(dev, args[1]);
        if (rc != 0)
            return Util.Fail("exfatlabel", "failed (rc=" + rc.ToString() + ")");
        Console.Write("label of ");
        Console.Write(info.Name);
        Console.Write(" set to \"");
        Console.Write(args[1]);
        Console.WriteLine("\"");
        return 0;
    }
}
