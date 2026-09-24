// NeutrinoOS Phase 5 utility: cp - copy files and directories
//
// usage: cp [-r] src dst
//   -r   copy directories recursively
//
// NeutrinoOS notes: copying a file over an existing file overwrites it
// (BCL File.Copy(overwrite: true) semantics); when dst is an existing
// directory, the source's base name is appended.

using System;
using System.IO;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Cp;

/// <summary>The cp utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 on failure.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        bool recursive = false;
        var operands = new System.Collections.Generic.List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: cp [-r] src dst",
                    "  -r   copy directories recursively",
                    "  When dst is an existing directory, src is copied into it.");
            }
            if (a == "-r" || a == "-R")
                recursive = true;
            else if (a.Length > 1 && a[0] == '-')
                return Util.Fail("cp", a + ": unknown option");
            else
                operands.Add(a);
        }

        if (operands.Count != 2)
            return Util.Fail("cp", "usage: cp [-r] src dst");

        string src = operands[0];
        string dst = operands[1];

        try
        {
            if (Directory.Exists(src))
            {
                if (!recursive)
                    return Util.Fail("cp", src + ": is a directory (use -r)");

                string target = Directory.Exists(dst) ? Path.Combine(dst, Path.GetFileName(src)) : dst;
                CopyDirectory(src, target);
                return 0;
            }

            if (!File.Exists(src))
                return Util.Fail("cp", src + ": no such file");

            string fileTarget = Directory.Exists(dst) ? Path.Combine(dst, Path.GetFileName(src)) : dst;
            File.Copy(src, fileTarget, true);
            return 0;
        }
        catch (Exception)
        {
            return Util.Fail("cp", src + " -> " + dst + ": copy failed");
        }
    }

    /// <summary>Recursively copies a directory tree (creates dst, then copies contents).</summary>
    public static void CopyDirectory(string src, string dst)
    {
        if (!Directory.Exists(dst))
            Directory.CreateDirectory(dst);

        string[] files = Directory.GetFiles(src);
        for (int i = 0; i < files.Length; i++)
        {
            string name = Path.GetFileName(files[i]);
            File.Copy(files[i], Path.Combine(dst, name), true);
        }

        string[] dirs = Directory.GetDirectories(src);
        for (int i = 0; i < dirs.Length; i++)
        {
            string name = Path.GetFileName(dirs[i]);
            CopyDirectory(dirs[i], Path.Combine(dst, name));
        }
    }
}
