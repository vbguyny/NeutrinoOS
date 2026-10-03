// NeutrinoOS Phase 5 utility: which - locate a command
//
// usage: which name...
//
// The shell runs commands from $PATH as <dir>/<name>.dll, built-ins
// first. This utility mirrors that lookup: it prints the first matching
// <dir>/<name>.dll, notes shell built-ins, and checks explicit paths
// (names containing '/' or ending in .dll) directly. Returns 1 when
// any name is not found.

using System;
using System.IO;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Which;

/// <summary>The which utility (see file header).</summary>
public static class Program
{
    // The shell's built-in commands (kept in sync with ShellBuiltins).
    private static readonly string[] Builtins =
    {
        "alias", "bg", "boottime", "cd", "cpupower", "exit", "export",
        "false", "fg", "gc", "gcstats", "help", "history", "jitstats",
        "jobs", "logout", "perf", "poweroff", "pwd", "reboot", "run",
        "source", "suspend", "true", "unalias", "unset", "usb", "version",
    };

    /// <summary>Entry point; returns 1 when a name is not found.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: which name...",
                "  Print the path a command would run from ($PATH lookup for",
                "  <dir>/<name>.dll), or note that it is a shell built-in.",
                "  A name containing '/' or ending in .dll is checked directly.");
        }
        if (args.Length == 0)
            return Util.Fail("which", "usage: which name...");

        int rc = 0;
        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            if (name.Length == 0)
                continue;

            if (HasSlash(name) || name.EndsWith(".dll"))
            {
                if (File.Exists(name))
                {
                    Console.WriteLine(name);
                }
                else
                {
                    Util.Fail("which", name + ": no such file");
                    rc = 1;
                }
                continue;
            }

            if (IsBuiltin(name))
            {
                Console.WriteLine(name + ": shell built-in");
                continue;
            }

            string found = FindInPath(name);
            if (found != null)
            {
                Console.WriteLine(found);
            }
            else
            {
                Util.Fail("which", name + ": not found in $PATH");
                rc = 1;
            }
        }
        return rc;
    }

    /// <summary>True when the name is one of the shell's built-in commands.</summary>
    private static bool IsBuiltin(string name)
    {
        for (int i = 0; i < Builtins.Length; i++)
        {
            if (Builtins[i] == name)
                return true;
        }
        return false;
    }

    /// <summary>Splits $PATH and returns the first <dir>/<name>.dll that exists.</summary>
    private static string FindInPath(string name)
    {
        string path = Util.GetEnvironment("PATH");
        string[] dirs = Util.SplitList(path, ':');
        for (int i = 0; i < dirs.Length; i++)
        {
            string dir = dirs[i];
            if (dir.Length == 0)
                continue;
            string candidate = dir[dir.Length - 1] == '/'
                ? dir + name + ".dll"
                : dir + "/" + name + ".dll";
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>True when the string contains a path separator.</summary>
    private static bool HasSlash(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '/' || s[i] == '\\')
                return true;
        }
        return false;
    }
}
