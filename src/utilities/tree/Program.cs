// NeutrinoOS Phase 5 utility: tree - list directories recursively
//
// usage: tree [directory]
//
// Prints the directory tree with |-- prefixes (directories first
// sorted together with files, case-insensitive), then a summary of
// how many directories and files were visited.

using System;
using System.IO;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Tree;

/// <summary>The tree utility (see file header).</summary>
public static class Program
{
    private static long _directories;
    private static long _files;

    /// <summary>Entry point; returns 1 when the directory does not exist.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;

        string root = ".";
        bool haveRoot = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: tree [directory]",
                    "  Recursively list a directory tree (default: current).");
            }
            if (a.Length > 0 && a[0] == '-')
                return Util.Fail("tree", a + ": unknown option");
            if (haveRoot)
                return Util.Fail("tree", "usage: tree [directory]");
            root = a;
            haveRoot = true;
        }

        if (!Directory.Exists(root))
            return Util.Fail("tree", root + ": no such directory");

        _directories = 0;
        _files = 0;
        Console.WriteLine(root);
        Walk(root, "");

        Console.WriteLine();
        string dirWord = _directories == 1
            ? "1 directory"
            : _directories.ToString() + " directories";
        string fileWord = _files == 1
            ? "1 file"
            : _files.ToString() + " files";
        Console.WriteLine(dirWord + ", " + fileWord);
        return 0;
    }

    /// <summary>Prints the entries of <paramref name="path"/> and recurses into subdirectories.</summary>
    private static void Walk(string path, string prefix)
    {
        string[] entries = ListEntries(path);
        for (int i = 0; i < entries.Length; i++)
        {
            string full = entries[i];
            bool isDir = Directory.Exists(full);
            Console.WriteLine(prefix + "|-- " + Path.GetFileName(full));
            if (isDir)
            {
                _directories++;
                Walk(full, prefix + "|   ");
            }
            else
            {
                _files++;
            }
        }
    }

    /// <summary>Returns subdirectories and files of a directory, sorted together.</summary>
    private static string[] ListEntries(string path)
    {
        var all = new System.Collections.Generic.List<string>();
        string[] dirs = Directory.GetDirectories(path);
        string[] files = Directory.GetFiles(path);
        for (int i = 0; i < dirs.Length; i++)
            all.Add(dirs[i]);
        for (int i = 0; i < files.Length; i++)
            all.Add(files[i]);
        string[] arr = all.ToArray();
        Util.SortIgnoreCase(arr);
        return arr;
    }
}
