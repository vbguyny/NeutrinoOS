// NeutrinoOS Phase 5 utility: find - search for files
//
// usage: find [path] [-name pattern]
//   Recursively lists entries under path (default "/"). With
//   -name pattern, only entries whose base name matches the pattern
//   are printed. The pattern supports '*' and '?' wildcards.
//
// NeutrinoOS note: this is the minimal Phase 5 find (no -type, -size,
// -exec or expression grammar; documented in docs/PHASE5-UTILITIES.md).

using System;
using System.IO;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Find;

/// <summary>The find utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 0 on success.</summary>
    public static int Main(string[] args)
    {
        string path = "/";
        string pattern = null;

        int i = 0;
        if (args.Length > 0 && args[0] != "--help" && args[0] != "-h" && args[0].Length > 0 && args[0][0] != '-')
        {
            path = args[0];
            i = 1;
        }

        for (; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: find [path] [-name pattern]",
                    "  Recursively list entries under path (default /).",
                    "  pattern supports '*' and '?' wildcards.");
            }
            if (a == "-name")
            {
                if (i + 1 >= args.Length)
                    return Util.Fail("find", "-name requires a pattern");
                pattern = args[i + 1];
                i++;
            }
            else
            {
                return Util.Fail("find", a + ": unknown option");
            }
        }

        if (!Directory.Exists(path))
            return Util.Fail("find", path + ": no such directory");

        Walk(path, pattern);
        return 0;
    }

    private static void Walk(string dir, string pattern)
    {
        string[] entries = Directory.GetFileSystemEntries(dir);
        Util.Sort(entries);

        for (int i = 0; i < entries.Length; i++)
        {
            string full = entries[i];
            string name = Path.GetFileName(full);

            if (pattern == null || Util.GlobMatch(pattern, name))
                Console.WriteLine(full);

            if (Directory.Exists(full))
                Walk(full, pattern);
        }
    }
}
