// NeutrinoOS Phase 5 utility: umount - unmount a VFS filesystem
//
// usage: umount path
//   Unmounts the mount point at the given path through the live DDK VFS
//   table (mount lists the entries). The kernel's on-demand boot-volume
//   helpers used by `run` are independent of this table.

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Storage;

namespace NeutrinoOS.Utility.Umount;

/// <summary>The umount utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 when the path is not a mount point.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        if (args.Length != 1 || args[0] == "--help" || args[0] == "-h")
        {
            return Util.Help(
                "usage: umount path",
                "  Unmount the VFS mount point at path (mount lists them).");
        }

        string path = args[0];
        FileResult result = VFS.Unmount(path);
        if (result != FileResult.Success)
            return Util.Fail("umount", path + ": not a mount point");

        Console.WriteLine("unmounted " + path);
        return 0;
    }
}
