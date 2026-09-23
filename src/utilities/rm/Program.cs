// NeutrinoOS Phase 5 utility: rm - remove files and directories
//
// usage: rm [-r] [-f] file...
//   -r   remove directories recursively (the korlib directory delete
//        is single-level, so recursion is implemented here)
//   -f   ignore missing files, never prompt

using System;
using System.IO;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Rm;

/// <summary>The rm utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 when something could not be removed.</summary>
    public static int Main(string[] args)
    {
        bool recursive = false;
        bool force = false;
        var targets = new System.Collections.Generic.List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: rm [-r] [-f] file...",
                    "  -r   remove directories and their contents recursively",
                    "  -f   ignore missing files");
            }
            if (a == "-r" || a == "-R")
                recursive = true;
            else if (a == "-f")
                force = true;
            else if (a == "-rf" || a == "-fr")
            {
                recursive = true;
                force = true;
            }
            else if (a.Length > 1 && a[0] == '-')
            {
                return Util.Fail("rm", a + ": unknown option");
            }
            else
            {
                targets.Add(a);
            }
        }

        if (targets.Count == 0)
            return Util.Fail("rm", "missing operand");

        int rc = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            string path = targets[i];
            try
            {
                if (Directory.Exists(path))
                {
                    if (!recursive)
                    {
                        rc = Util.Fail("rm", path + ": is a directory (use -r)");
                        continue;
                    }
                    RecursiveRemoveDirectory(path);
                    continue;
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                    continue;
                }

                if (!force)
                    rc = Util.Fail("rm", path + ": no such file");
            }
            catch (Exception)
            {
                rc = Util.Fail("rm", path + ": cannot remove");
            }
        }
        return rc;
    }

    /// <summary>
    /// Deletes a directory tree bottom-up (the bridge deletes only empty
    /// directories, so every level is emptied before its delete call).
    /// </summary>
    public static void RecursiveRemoveDirectory(string dir)
    {
        string[] files = Directory.GetFiles(dir);
        for (int i = 0; i < files.Length; i++)
            File.Delete(files[i]);

        string[] subdirs = Directory.GetDirectories(dir);
        for (int i = 0; i < subdirs.Length; i++)
            RecursiveRemoveDirectory(subdirs[i]);

        Directory.Delete(dir);
    }
}
