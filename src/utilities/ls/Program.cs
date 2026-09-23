// NeutrinoOS Phase 5 utility: ls - list directory contents
//
// usage: ls [-l] [-a] [dir...]
//   -l   long format (type, size, name)
//   -a   include . and .. entries
//
// NeutrinoOS notes: entries are listed one per line (no column
// packing), sorted ordinally; "type" is 'd' for directories, "-"
// otherwise; file sizes come from File.GetFileSize (the boot FAT
// volume has no timestamps, so ls -l does not show dates).

using System;
using System.IO;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Ls;

/// <summary>The ls utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 0 on success, 1 when a directory could not be listed.</summary>
    public static int Main(string[] args)
    {
        bool longFormat = false;
        bool showAll = false;
        var dirs = new System.Collections.Generic.List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: ls [-l] [-a] [dir...]",
                    "  -l   long format: type, size, name",
                    "  -a   include . and .. entries",
                    "  With no dir, lists the current directory.");
            }
            if (a == "-l")
                longFormat = true;
            else if (a == "-a")
                showAll = true;
            else if (a == "-la" || a == "-al")
            {
                longFormat = true;
                showAll = true;
            }
            else if (a.Length > 1 && a[0] == '-')
            {
                return Util.Fail("ls", a + ": unknown option");
            }
            else
            {
                dirs.Add(a);
            }
        }

        if (dirs.Count == 0)
            dirs.Add(Directory.GetCurrentDirectory());

        int rc = 0;
        for (int d = 0; d < dirs.Count; d++)
        {
            string dir = dirs[d];
            if (!Directory.Exists(dir))
            {
                rc = Util.Fail("ls", dir + ": no such directory");
                continue;
            }

            if (dirs.Count > 1)
                Console.WriteLine(dir + ":");

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(dir);
            }
            catch (Exception)
            {
                rc = Util.Fail("ls", dir + ": cannot list");
                continue;
            }

            Util.Sort(entries);

            if (showAll)
            {
                PrintEntry(".", null, true, longFormat);
                PrintEntry("..", null, true, longFormat);
            }

            for (int i = 0; i < entries.Length; i++)
            {
                string full = entries[i];
                string name = Path.GetFileName(full);
                bool isDir = Directory.Exists(full);
                PrintEntry(name, full, isDir, longFormat);
            }

            if (dirs.Count > 1)
                Console.WriteLine();
        }

        return rc;
    }

    private static void PrintEntry(string name, string? fullPath, bool isDir, bool longFormat)
    {
        if (!longFormat)
        {
            Console.WriteLine(name);
            return;
        }

        long size = 0;
        if (!isDir && fullPath != null)
        {
            // FileInfo.Length is the standard BCL surface (korlib file
            // sizes are exposed through it); missing files report 0.
            FileInfo info = new FileInfo(fullPath);
            if (info.Exists)
                size = info.Length;
        }

        Console.Write(isDir ? "d  " : "-  ");
        Console.Write(Util.PadLeft(size, 8));
        Console.Write("  ");
        Console.WriteLine(name);
    }
}
