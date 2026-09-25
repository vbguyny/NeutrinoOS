// NeutrinoOS Phase 8 - npkg: on-disk layout + directory helpers
//
// npkg keeps all of its state under /var/lib/npkg (installed database,
// transaction journal, staging area for extracted payloads) and its
// configuration under /etc/npkg (repository list + trusted signing
// keys). The boot volume is a FAT32 volume with a bridge that only
// creates ONE directory level per call, so every directory helper here
// walks the path component by component (see docs/PHASE8).
//
// Utility assemblies resolve their dependencies via $PATH (/bin:/apps);
// module code never uses Path.Combine (not in korlib), so paths are
// concatenated with '/' by the callers.

using System;
using System.Collections.Generic;
using System.IO;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Npkg;

/// <summary>
/// Well-known npkg filesystem locations plus the small directory and
/// file helpers the whole utility shares.
/// </summary>
public static class NpkgPaths
{
    /// <summary>Root of the npkg state tree ("/var/lib/npkg").</summary>
    public const string Root = "/var/lib/npkg";

    /// <summary>Installed-package database ("/var/lib/npkg/installed.json").</summary>
    public const string Db = Root + "/installed.json";

    /// <summary>Per-transaction backup directory ("/var/lib/npkg/journal").</summary>
    public const string JournalDir = Root + "/journal";

    /// <summary>Append-only transaction log ("/var/lib/npkg/journal.log").</summary>
    public const string JournalLog = Root + "/journal.log";

    /// <summary>Payload extraction area ("/var/lib/npkg/staging").</summary>
    public const string Staging = Root + "/staging";

    /// <summary>Download cache directory ("/var/lib/npkg/cache").</summary>
    public const string Cache = Root + "/cache";

    /// <summary>Installed driver packages ("/var/lib/npkg/drivers").</summary>
    public const string DriversDir = Root + "/drivers";

    /// <summary>npkg configuration directory ("/etc/npkg").</summary>
    public const string EtcDir = "/etc/npkg";

    /// <summary>Trusted signing keys ("/etc/npkg/trusted-keys").</summary>
    public const string TrustedKeys = EtcDir + "/trusted-keys";

    /// <summary>Repository configuration ("/etc/npkg/repos.json").</summary>
    public const string ReposFile = EtcDir + "/repos.json";

    /// <summary>Architecture packages are installed for on this build.</summary>
    public const string TargetArchitecture = "x86-64";

    /// <summary>Architecture value matching every target.</summary>
    public const string AnyArchitecture = "any";

    /// <summary>
    /// Creates the complete npkg directory tree (parents first, one level
    /// per bridge call). Idempotent; call before any command that writes.
    /// </summary>
    public static void EnsureDirs()
    {
        EnsureDirChain(Root);
        EnsureDirChain(Staging);
        EnsureDirChain(Cache);
        EnsureDirChain(JournalDir);
        EnsureDirChain(DriversDir);
        EnsureDirChain(TrustedKeys);
    }

    /// <summary>
    /// Creates every missing component of an absolute directory path from
    /// the root down (the FAT bridge creates a single level per call).
    /// </summary>
    public static void EnsureDirChain(string path)
    {
        if (path == null || path.Length == 0 || path == "/")
            return;

        string[] parts = Util.SplitList(path, '/');
        var built = new System.Text.StringBuilder();
        for (int i = 0; i < parts.Length; i++)
        {
            built.Append('/');
            built.Append(parts[i]);
            string step = built.ToString();
            if (!Directory.Exists(step))
                Directory.CreateDirectory(step);
        }
    }

    /// <summary>
    /// Copies a whole file (used for journal backups and restores; there
    /// is no atomic rename on the FAT bridge).
    /// </summary>
    public static void CopyFile(string source, string destination)
    {
        File.Copy(source, destination, true);
    }

    /// <summary>
    /// Deletes a directory tree bottom-up: every level is emptied of files
    /// and already-deleted subdirectories before its delete call, because
    /// the kernel deletes empty directories only. Silent when the root
    /// does not exist.
    /// </summary>
    public static void DeleteTree(string root)
    {
        if (root == null || root.Length == 0 || !Directory.Exists(root))
            return;

        var order = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            string dir = queue.Dequeue();
            order.Add(dir);
            string[] subdirs = Directory.GetDirectories(dir);
            for (int i = 0; i < subdirs.Length; i++)
                queue.Enqueue(subdirs[i]);
        }

        for (int i = order.Count - 1; i >= 0; i--)
        {
            string dir = order[i];
            string[] files = Directory.GetFiles(dir);
            for (int f = 0; f < files.Length; f++)
            {
                try { File.Delete(files[f]); } catch (Exception) { }
            }
            try { Directory.Delete(dir); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Returns the parent directory of a path ("/bin/x.dll" -> "/bin");
    /// "/" when there is no parent.
    /// </summary>
    public static string ParentOf(string path)
    {
        if (path == null || path.Length == 0)
            return "/";

        int end = path.Length;
        while (end > 1 && path[end - 1] == '/')
            end--;
        int i = end;
        while (i > 0 && path[i - 1] != '/')
            i--;
        if (i <= 1)
            return "/";
        return path.Substring(0, i - 1);
    }
}
