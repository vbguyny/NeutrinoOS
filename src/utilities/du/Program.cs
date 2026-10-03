// NeutrinoOS Phase 5 utility: du - disk usage of files and directories
//
// usage: du [-h] path...
//
//   -h   human-readable sizes (B/K/M/G)
//
// Prints the total size of each named file or directory (recursively
// summed), one line per operand.

using System;
using System.IO;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Du;

/// <summary>The du utility (see file header).</summary>
public static class Program
{
    private static bool _human;

    /// <summary>Entry point; returns 1 when a path does not exist.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;

        var paths = new System.Collections.Generic.List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help")
            {
                return Util.Help(
                    "usage: du [-h] path...",
                    "  -h   human-readable sizes (B/K/M/G)",
                    "  Print the total size of each file or directory.");
            }
            if (a == "-h" || a == "--human-readable")
            {
                _human = true;
            }
            else if (a.Length > 1 && a[0] == '-')
            {
                return Util.Fail("du", a + ": unknown option");
            }
            else
            {
                paths.Add(a);
            }
        }

        if (paths.Count == 0)
            return Util.Fail("du", "missing path operand (use '.')");

        int rc = 0;
        for (int i = 0; i < paths.Count; i++)
        {
            string path = paths[i];
            long size;
            if (File.Exists(path))
            {
                try
                {
                    size = new FileInfo(path).Length;
                }
                catch (Exception)
                {
                    rc = Util.Fail("du", path + ": read failed");
                    continue;
                }
            }
            else if (Directory.Exists(path))
            {
                size = SumDirectory(path);
            }
            else
            {
                rc = Util.Fail("du", path + ": no such file or directory");
                continue;
            }

            Console.WriteLine(FormatSize(size) + "\t" + path);
        }
        return rc;
    }

    /// <summary>Recursively sums the sizes of the files under a directory.</summary>
    private static long SumDirectory(string dir)
    {
        long total = 0;
        try
        {
            string[] files = Directory.GetFiles(dir);
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    total += new FileInfo(files[i]).Length;
                }
                catch (Exception)
                {
                    // Unreadable file: skip it (matches du's per-file skip).
                }
            }
            string[] subs = Directory.GetDirectories(dir);
            for (int i = 0; i < subs.Length; i++)
                total += SumDirectory(subs[i]);
        }
        catch (Exception)
        {
            // Unreadable directory: report what was counted so far.
        }
        return total;
    }

    /// <summary>Formats a byte count, optionally in human units.</summary>
    private static string FormatSize(long bytes)
    {
        if (!_human)
            return bytes.ToString();
        if (bytes < 1024)
            return bytes.ToString() + " B";
        if (bytes < 1024L * 1024)
            return OneDecimal(bytes, 1024L) + " K";
        if (bytes < 1024L * 1024 * 1024)
            return OneDecimal(bytes, 1024L * 1024) + " M";
        return OneDecimal(bytes, 1024L * 1024 * 1024) + " G";
    }

    /// <summary>Formats bytes / unit with one decimal place.</summary>
    private static string OneDecimal(long bytes, long unit)
    {
        long tenths = bytes * 10 / unit;
        return (tenths / 10).ToString() + "." + (tenths % 10).ToString();
    }
}
