// NeutrinoOS Phase 8 - npkg: install transaction journal
//
// Every state-changing command (install/remove/upgrade) wraps its file
// operations in a journal transaction so a crash or power loss cannot
// leave the system half-modified:
//
//   /var/lib/npkg/journal.log   append-only, one JSON object per line:
//       {"txn":"t7","op":"begin"}
//       {"txn":"t7","op":"add","path":"/bin/hello.dll"}
//       {"txn":"t7","op":"backup","path":"/bin/old","backup":"/var/lib/npkg/journal/t7/0"}
//       {"txn":"t7","op":"remove","path":"/bin/old"}
//       {"txn":"t7","op":"dir-remove","path":"/apps/old"}
//       {"txn":"t7","op":"commit"}       (or "rollback")
//
//   /var/lib/npkg/journal/<txn>/<n>   copies of overwritten files
//
// Journal.Recover() runs at the start of every mutating command: it
// finds the last transaction without a commit/rollback line and undoes
// it in reverse order (delete added files, restore backups, recreate
// removed directories), printing every action. Because the undo is
// idempotent, running recovery twice is harmless.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NeutrinoOS.Packaging;

namespace NeutrinoOS.Utility.Npkg;

/// <summary>One recorded file operation of the current transaction.</summary>
public sealed class JournalOp
{
    /// <summary>Operation name: add, backup, remove or dir-remove.</summary>
    public string Op;

    /// <summary>Absolute path the operation applied to.</summary>
    public string Path;

    /// <summary>Backup file the operation can be undone from (may be empty).</summary>
    public string Backup;
}

/// <summary>
/// Append-only transaction journal with crash recovery (see file header).
/// One process performs one transaction at a time; Begin establishes the
/// "current" transaction id used by the Note* helpers.
/// </summary>
public static class Journal
{
    private static string _currentTxn;

    /// <summary>Backup directory of a transaction.</summary>
    public static string BackupDir(string txnId)
    {
        return NpkgPaths.JournalDir + "/" + txnId;
    }

    /// <summary>Path of the n-th backup file of a transaction.</summary>
    public static string BackupPath(string txnId, int index)
    {
        return BackupDir(txnId) + "/" + TextConv.LongToString(index);
    }

    /// <summary>
    /// Computes the next transaction id ("t1", "t2", ...) by scanning the
    /// journal for the highest id seen so far. Ids are never reused, so a
    /// rolled-back transaction cannot be confused with a new one.
    /// </summary>
    public static string NextTxnId()
    {
        long max = 0;
        if (File.Exists(NpkgPaths.JournalLog))
        {
            string text = File.ReadAllText(NpkgPaths.JournalLog);
            int from = 0;
            while (true)
            {
                int marker = text.IndexOf("\"txn\":\"t", from);
                if (marker < 0)
                    break;
                int digits = marker + 9;
                int end = digits;
                while (end < text.Length && text[end] >= '0' && text[end] <= '9')
                    end++;
                if (end > digits)
                {
                    long value;
                    if (TextConv.TryParseLong(text.Substring(digits, end - digits), out value) && value > max)
                        max = value;
                }
                from = end > digits ? end : digits + 1;
            }
        }
        return "t" + TextConv.LongToString(max + 1);
    }

    /// <summary>Starts a transaction (creates its backup directory, journals "begin").</summary>
    public static void Begin(string txnId)
    {
        _currentTxn = txnId;
        NpkgPaths.EnsureDirChain(BackupDir(txnId));
        Append(Line(txnId, "begin", null, null));
    }

    /// <summary>Records that a file was newly created and should be deleted on rollback.</summary>
    public static void NoteFileAdd(string path)
    {
        Append(Line(_currentTxn, "add", path, null));
    }

    /// <summary>Records that a file was copied to a backup path before being overwritten.</summary>
    public static void NoteFileBackup(string path, string backupPath)
    {
        Append(Line(_currentTxn, "backup", path, backupPath));
    }

    /// <summary>Records that an existing file was deleted (restored from its backup on rollback).</summary>
    public static void NoteFileRemove(string path)
    {
        Append(Line(_currentTxn, "remove", path, null));
    }

    /// <summary>Records that an empty directory was deleted (recreated on rollback).</summary>
    public static void NoteDirRemove(string path)
    {
        Append(Line(_currentTxn, "dir-remove", path, null));
    }

    /// <summary>Marks the transaction committed; its backups become disposable.</summary>
    public static void Commit(string txnId)
    {
        Append(Line(txnId, "commit", null, null));
        _currentTxn = null;
        NpkgPaths.DeleteTree(BackupDir(txnId));
    }

    /// <summary>
    /// Marks a transaction as already rolled back (used after an in-memory
    /// rollback) so Journal.Recover does not try to undo it again.
    /// </summary>
    public static void MarkRolledBack(string txnId)
    {
        Append(Line(txnId, "rollback", null, null));
        _currentTxn = null;
        NpkgPaths.DeleteTree(BackupDir(txnId));
    }

    /// <summary>
    /// Rolls back the last transaction that never reached a commit or
    /// rollback marker, printing every action. Called before any new
    /// mutating command so the previous crash is undone first.
    /// </summary>
    public static void Recover()
    {
        if (!File.Exists(NpkgPaths.JournalLog))
            return;

        string text = File.ReadAllText(NpkgPaths.JournalLog);
        var order = new List<string>();
        var ops = new Dictionary<string, List<JournalOp>>();
        var terminal = new Dictionary<string, bool>();

        string[] lines = SplitLines(text);
        for (int i = 0; i < lines.Length; i++)
        {
            JsonObject obj = Jsn.ParseObject(lines[i]);
            if (obj == null)
                continue;
            string txn = Jsn.GetStr(obj, "txn");
            string op = Jsn.GetStr(obj, "op");
            if (txn.Length == 0 || op.Length == 0)
                continue;

            List<JournalOp> list;
            if (!ops.TryGetValue(txn, out list))
            {
                list = new List<JournalOp>();
                ops[txn] = list;
                order.Add(txn);
                terminal[txn] = false;
            }
            if (op == "commit" || op == "rollback")
            {
                terminal[txn] = true;
                continue;
            }
            var entry = new JournalOp();
            entry.Op = op;
            entry.Path = Jsn.GetStr(obj, "path");
            entry.Backup = Jsn.GetStr(obj, "backup");
            list.Add(entry);
        }

        // Housekeeping: drop backup directories of finished transactions
        // (Commit already deletes them; this catches a crash in between).
        for (int i = 0; i < order.Count; i++)
        {
            bool done;
            terminal.TryGetValue(order[i], out done);
            if (done)
                NpkgPaths.DeleteTree(BackupDir(order[i]));
        }

        // Find the last transaction without a terminal marker.
        string target = null;
        for (int i = order.Count - 1; i >= 0; i--)
        {
            bool done;
            terminal.TryGetValue(order[i], out done);
            if (!done)
            {
                target = order[i];
                break;
            }
        }
        if (target == null)
            return;

        List<JournalOp> actions = ops[target];
        int performed = 0;
        for (int i = actions.Count - 1; i >= 0; i--)
        {
            JournalOp op = actions[i];
            if (op.Op == "add")
            {
                performed += DeleteIfExists(op.Path) ? 1 : 0;
            }
            else if (op.Op == "backup")
            {
                performed += Restore(op.Path, op.Backup) ? 1 : 0;
            }
            else if (op.Op == "remove")
            {
                // Restore from the backup recorded earlier in this transaction.
                string backup = op.Backup;
                if (backup == null || backup.Length == 0)
                {
                    for (int k = i - 1; k >= 0; k--)
                    {
                        if (actions[k].Op == "backup" && actions[k].Path == op.Path)
                        {
                            backup = actions[k].Backup;
                            break;
                        }
                    }
                }
                performed += Restore(op.Path, backup) ? 1 : 0;
            }
            else if (op.Op == "dir-remove")
            {
                if (op.Path != null && op.Path.Length > 0 && !Directory.Exists(op.Path))
                {
                    NpkgPaths.EnsureDirChain(op.Path);
                    Console.WriteLine("npkg: recovery: recreated " + op.Path);
                    performed++;
                }
            }
        }

        Append(Line(target, "rollback", null, null));
        NpkgPaths.DeleteTree(BackupDir(target));
        Console.WriteLine("npkg: recovery: rolled back transaction " + target
            + " (" + TextConv.LongToString(performed) + " actions)");
    }

    private static bool DeleteIfExists(string path)
    {
        if (path == null || path.Length == 0 || !File.Exists(path))
            return false;
        File.Delete(path);
        Console.WriteLine("npkg: recovery: removed " + path);
        return true;
    }

    private static bool Restore(string path, string backup)
    {
        if (path == null || path.Length == 0 || backup == null || backup.Length == 0)
            return false;
        if (!File.Exists(backup))
            return false;
        NpkgPaths.EnsureDirChain(NpkgPaths.ParentOf(path));
        File.Copy(backup, path, true);
        Console.WriteLine("npkg: recovery: restored " + path);
        return true;
    }

    private static string Line(string txn, string op, string path, string backup)
    {
        var sb = new StringBuilder();
        sb.Append("{\"txn\":");
        sb.Append(Jsn.Quote(txn));
        sb.Append(",\"op\":");
        sb.Append(Jsn.Quote(op));
        if (path != null)
        {
            sb.Append(",\"path\":");
            sb.Append(Jsn.Quote(path));
        }
        if (backup != null)
        {
            sb.Append(",\"backup\":");
            sb.Append(Jsn.Quote(backup));
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void Append(string line)
    {
        // File.AppendAllText is outside the korlib subset; the FileStream
        // append mode rewrites the whole (small) journal file on close.
        using (var stream = new FileStream(NpkgPaths.JournalLog, FileMode.Append, FileAccess.Write))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    private static string[] SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == '\n')
            {
                if (i > start)
                {
                    string line = text.Substring(start, i - start);
                    if (line.Length > 0 && line[line.Length - 1] == '\r')
                        line = line.Substring(0, line.Length - 1);
                    if (line.Length > 0)
                        lines.Add(line);
                }
                start = i + 1;
            }
        }
        return lines.ToArray();
    }
}
