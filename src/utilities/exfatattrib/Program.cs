// NeutrinoOS Phase 10 utility: exfatattrib - show/change exFAT attributes.
//
// usage: exfatattrib <device>                  - list files with attributes
//        exfatattrib <device> <spec> <path>    - change one file's attrs
//
// spec is a set of +/- toggles over the DOS attribute letters:
//   r = read-only, h = hidden, s = system, a = archive
//   e.g. "+rh" sets read-only and hidden; "-a" clears archive;
//        "+a-h" sets archive and clears hidden.
//
// The read-only attribute is enforced by the driver (writes to such
// files fail); hidden/system are stored only (there is no GUI).
// Changes rewrite the directory entry set and its checksum.

using System;
using NeutrinoOS.Utils;
using NeutrinoOS.DDK.Storage;
using NeutrinoOS.DDK.Storage.ExFat;

namespace NeutrinoOS.Utility.ExFatAttrib;

/// <summary>The exfatattrib utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 on failure.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        if (args.Length == 0 || args.Length > 3 || args[0] == "--help" || args[0] == "-h"
            || args.Length == 2)
        {
            return Util.Help(
                "usage: exfatattrib <device> [spec path]",
                "  list: exfatattrib hdb",
                "  spec: +/- toggles over attribute letters (r=read-only,",
                "        h=hidden, s=system, a=archive), e.g. \"+rh\" or \"-a\".",
                "  set:  exfatattrib hdb +rh important.doc");
        }

        var info = BlockDevices.Find(args[0]);
        if (info == null)
            return Util.Fail("exfatattrib", "no such device: " + args[0]);

        var dev = new KernelBlockDevice(info);

        if (args.Length == 1)
            return List(dev, info.Name);

        int rc = NeutrinoOS.DDK.Storage.ExFat.ExFatAttrib.Apply(dev, args[2], args[1]);
        if (rc == -2)
            return Util.Fail("exfatattrib", "bad attribute spec: " + args[1]);
        if (rc != 0)
            return Util.Fail("exfatattrib", args[2] + ": failed (rc=" + rc.ToString() + ")");

        Console.Write(args[2]);
        Console.Write(": attributes updated (");
        Console.Write(args[1]);
        Console.WriteLine(")");
        return 0;
    }

    /// <summary>Lists all root entries with their attribute letters.</summary>
    private static int List(IBlockDevice dev, string name)
    {
        var fs = new ExFatFileSystem();
        fs.Initialize();
        if (!fs.Probe(dev))
        {
            fs.Shutdown();
            return Util.Fail("exfatattrib", name + ": not an exFAT volume");
        }
        if (fs.Mount(dev, true) != FileResult.Success)
        {
            fs.Shutdown();
            return Util.Fail("exfatattrib", name + ": mount failed");
        }

        if (fs.OpenDirectory("/", out var dir) != FileResult.Success || dir == null)
        {
            fs.Unmount();
            fs.Shutdown();
            return Util.Fail("exfatattrib", "cannot open root directory");
        }

        FileInfo? entry;
        while ((entry = dir.ReadNext()) != null)
        {
            string letters = "";
            letters += (entry.Attributes & FileAttributes.ReadOnly) != 0 ? "r" : "-";
            letters += (entry.Attributes & FileAttributes.Hidden) != 0 ? "h" : "-";
            letters += (entry.Attributes & FileAttributes.System) != 0 ? "s" : "-";
            letters += (entry.Attributes & FileAttributes.Archive) != 0 ? "a" : "-";

            Console.Write(entry.IsDirectory ? "d" : "-");
            Console.Write(letters);
            Console.Write("  ");
            Console.Write(entry.Name);
            if (!entry.IsDirectory)
            {
                Console.Write("  ");
                Console.Write(entry.Size);
            }
            Console.WriteLine();
        }

        return 0;
    }
}
