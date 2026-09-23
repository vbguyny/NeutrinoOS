// NeutrinoOS Phase 5 utility: mv - move / rename files and directories
//
// usage: mv src dst
//
// NeutrinoOS note: the FAT bridge has no rename operation, so moving is
// copy + delete (korlib File.Move); directories are copied recursively
// and then removed.

using System;
using System.IO;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Mv;

/// <summary>The mv utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 on failure.</summary>
    public static int Main(string[] args)
    {
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: mv src dst",
                "  Move or rename a file or directory (copy + delete on FAT).",
                "  When dst is an existing directory, src is moved into it.");
        }

        if (args.Length != 2)
            return Util.Fail("mv", "usage: mv src dst");

        string src = args[0];
        string dst = args[1];

        try
        {
            if (Directory.Exists(src))
            {
                string target = Directory.Exists(dst) ? Path.Combine(dst, Path.GetFileName(src)) : dst;
                CopyTree(src, target);
                DeleteTree(src);
                return 0;
            }

            if (!File.Exists(src))
                return Util.Fail("mv", src + ": no such file");

            string fileTarget = Directory.Exists(dst) ? Path.Combine(dst, Path.GetFileName(src)) : dst;
            File.Move(src, fileTarget);
            return 0;
        }
        catch (Exception)
        {
            return Util.Fail("mv", src + " -> " + dst + ": move failed");
        }
    }

    private static void CopyTree(string src, string dst)
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
            CopyTree(dirs[i], Path.Combine(dst, name));
        }
    }

    private static void DeleteTree(string dir)
    {
        string[] files = Directory.GetFiles(dir);
        for (int i = 0; i < files.Length; i++)
            File.Delete(files[i]);

        string[] subdirs = Directory.GetDirectories(dir);
        for (int i = 0; i < subdirs.Length; i++)
            DeleteTree(subdirs[i]);

        Directory.Delete(dir);
    }
}
