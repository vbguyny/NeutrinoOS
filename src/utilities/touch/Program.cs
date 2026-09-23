// NeutrinoOS Phase 5 utility: touch - create empty files
//
// usage: touch file...
//
// NeutrinoOS note: the boot FAT volume has no timestamps, so touch only
// creates missing files (existing files are left unchanged).

using System;
using System.IO;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Touch;

/// <summary>The touch utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 when a file could not be created.</summary>
    public static int Main(string[] args)
    {
        if (args.Length == 0)
            return Util.Fail("touch", "missing operand");
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: touch file...",
                "  Create empty files (existing files are left unchanged;",
                "  the FAT boot volume has no timestamps).");
        }

        int rc = 0;
        for (int i = 0; i < args.Length; i++)
        {
            string path = args[i];
            try
            {
                if (Directory.Exists(path))
                {
                    rc = Util.Fail("touch", path + ": is a directory");
                    continue;
                }
                if (!File.Exists(path))
                    File.WriteAllText(path, "");
            }
            catch (Exception)
            {
                rc = Util.Fail("touch", path + ": cannot create");
            }
        }
        return rc;
    }
}
