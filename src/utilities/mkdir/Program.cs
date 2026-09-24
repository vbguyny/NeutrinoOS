// NeutrinoOS Phase 5 utility: mkdir - create directories
//
// usage: mkdir [-p] dir...
//   -p   create missing parent directories as needed
//
// NeutrinoOS note: the underlying FAT bridge creates a single level;
// -p walks the path and creates each missing component in order.

using System;
using System.IO;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Mkdir;

/// <summary>The mkdir utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 when a directory could not be created.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        bool parents = false;
        var dirs = new System.Collections.Generic.List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: mkdir [-p] dir...",
                    "  -p   create missing parent directories as needed");
            }
            if (a == "-p")
                parents = true;
            else
                dirs.Add(a);
        }

        if (dirs.Count == 0)
            return Util.Fail("mkdir", "missing operand");

        int rc = 0;
        for (int i = 0; i < dirs.Count; i++)
        {
            string dir = dirs[i];
            try
            {
                if (Directory.Exists(dir))
                    continue;   // mkdir -p style: existing directories are fine

                if (!parents)
                {
                    Directory.CreateDirectory(dir);
                    continue;
                }

                // Create every missing component from the root down.
                string resolved = Path.GetFullPath(dir);
                string[] parts = Util.SplitList(resolved, '/');
                var built = new System.Text.StringBuilder();
                for (int p = 0; p < parts.Length; p++)
                {
                    built.Append('/');
                    built.Append(parts[p]);
                    string step = built.ToString();
                    if (!Directory.Exists(step))
                        Directory.CreateDirectory(step);
                }
            }
            catch (Exception)
            {
                rc = Util.Fail("mkdir", dir + ": cannot create directory");
            }
        }
        return rc;
    }
}
