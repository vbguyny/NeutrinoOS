// NeutrinoOS Phase 8 - npkg: install / remove / upgrade engine
//
// Install flow (every file operation goes through the journal):
//   1. fetch the .npkg, verify the Ed25519 signature against every
//      trusted key, verify per-file SHA-256 checksums;
//   2. extract the payload into /var/lib/npkg/staging/<name>-<version>;
//   3. run pre-install, place the files, create /bin wrappers for the
//      entry points, run post-install;
//   4. record the package in installed.json (file + directory lists) and
//      commit the journal transaction.
// Any failure rolls everything back: added files are deleted, overwritten
// files are restored from journal backups, created directories are
// removed again and staging is discarded - the database stays untouched.
//
// Placement rules (manifest `provides` capability decides the kind):
//   utility      every payload file (flat) -> installPath (default /bin)
//                plus an executable wrapper /bin/<entry> per entry point
//   application  payload tree -> installPath (default /apps/<name>)
//                plus /bin/<entry> wrappers pointing into the tree
//   driver       payload tree + manifest.json -> /var/lib/npkg/drivers/<name>
//   library      payload tree -> installPath (default /lib)
//   other        payload files (flat) -> installPath (default /bin)
//
// Wrappers are two-line text files the shell's `run` builtin resolves:
//   run /apps/hello/hello.dll

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NeutrinoOS.Packaging;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Kernel;

namespace NeutrinoOS.Utility.Npkg;

/// <summary>Where one payload file is copied during installation.</summary>
public sealed class Placement
{
    /// <summary>Payload-relative source path (in the staging area).</summary>
    public string Source;

    /// <summary>Absolute destination path on the boot volume.</summary>
    public string Destination;

    /// <summary>Creates a placement pair.</summary>
    public Placement(string source, string destination)
    {
        Source = source;
        Destination = destination;
    }
}

/// <summary>Install/remove/upgrade engine with journal-backed rollback (see file header).</summary>
public sealed class Installer
{
    private readonly InstalledDatabase _db;
    private readonly List<PackageSource> _sources;

    /// <summary>Creates an installer over the given database and repository indexes.</summary>
    public Installer(InstalledDatabase db, List<PackageSource> sources)
    {
        _db = db;
        _sources = sources == null ? new List<PackageSource>() : sources;
    }

    /// <summary>
    /// Installs "name" or "name@version" with its dependencies. Returns 0
    /// on success, 1 on error (the message is printed to stderr).
    /// </summary>
    public int Install(string nameOrSpec, bool allowUntrusted, bool force)
    {
        try
        {
            NpkgPaths.EnsureDirs();
            Journal.Recover();
            _db.Load();

            string name;
            string version;
            SplitSpec(nameOrSpec, out name, out version);

            var resolver = new Resolver(_sources, _db, force);
            List<PlanItem> plan = resolver.Resolve(name, version);
            if (plan.Count == 0)
            {
                InstalledPackage installed = _db.Find(name);
                if (installed != null)
                    Console.WriteLine(name + " " + installed.Version + " is already installed");
                else
                    Console.WriteLine(name + " is already installed");
                return 0;
            }

            for (int i = 0; i < plan.Count; i++)
            {
                Console.WriteLine("install " + plan[i].Info.Name + " " + plan[i].Info.Version.ToString()
                    + " (" + plan[i].Repo.Name + ")");
            }
            for (int i = 0; i < plan.Count; i++)
                InstallOne(plan[i], allowUntrusted, null);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("neutrinoos: npkg: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Removes an installed package. Refuses (unless forced) while another
    /// installed package depends on it. Returns 0 on success, 1 on error.
    /// </summary>
    public int Remove(string name, bool force)
    {
        try
        {
            NpkgPaths.EnsureDirs();
            Journal.Recover();
            _db.Load();

            InstalledPackage record = _db.Find(name);
            if (record == null)
                throw new Exception(name + " is not installed");

            List<string> dependents = FindDependents(name);
            if (dependents.Count > 0)
            {
                if (!force)
                {
                    throw new Exception(name + " is required by " + JoinList(dependents)
                        + " (use --force to remove it anyway)");
                }
                Console.WriteLine("WARNING: " + name + " is required by " + JoinList(dependents));
            }

            RunScript(record.Scripts, "pre-remove", name);

            string txn = Journal.NextTxnId();
            Journal.Begin(txn);
            var backups = new List<string>();
            var removedDirs = new List<string>();
            try
            {
                for (int i = record.Files.Count - 1; i >= 0; i--)
                {
                    string file = record.Files[i];
                    if (file == null || file.Length == 0 || !File.Exists(file))
                        continue;
                    BackupFile(txn, file, backups);
                    Journal.NoteFileRemove(file);
                    File.Delete(file);
                }
                for (int i = record.Dirs.Count - 1; i >= 0; i--)
                {
                    string dir = record.Dirs[i];
                    if (dir == null || dir.Length == 0 || !Directory.Exists(dir))
                        continue;
                    Journal.NoteDirRemove(dir);
                    try
                    {
                        Directory.Delete(dir);      // succeeds only when empty
                        removedDirs.Add(dir);
                    }
                    catch (Exception) { }
                }

                RunScript(record.Scripts, "post-remove", name);

                if (File.Exists(NpkgPaths.Db))
                    BackupFile(txn, NpkgPaths.Db, backups);
                else
                    Journal.NoteFileAdd(NpkgPaths.Db);
                _db.Remove(name);
                _db.Save();

                Journal.Commit(txn);
                Console.WriteLine("removed " + name + " " + record.Version);
                return 0;
            }
            catch (Exception)
            {
                for (int i = backups.Count - 1; i >= 0; i--)
                {
                    string backupPath = Journal.BackupPath(txn, i);
                    if (!File.Exists(backupPath))
                        continue;
                    NpkgPaths.EnsureDirChain(NpkgPaths.ParentOf(backups[i]));
                    File.Copy(backupPath, backups[i], true);
                }
                for (int i = removedDirs.Count - 1; i >= 0; i--)
                    NpkgPaths.EnsureDirChain(removedDirs[i]);
                Journal.MarkRolledBack(txn);
                _db.Load();
                throw;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("neutrinoos: npkg: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Upgrades one installed package (or every installed package when
    /// nameOrNull is null) to the best repository version and prunes the
    /// old version's files. Returns 0 when nothing failed.
    /// </summary>
    public int Upgrade(string nameOrNull)
    {
        try
        {
            NpkgPaths.EnsureDirs();
            Journal.Recover();
            _db.Load();

            var targets = new List<InstalledPackage>();
            if (nameOrNull != null && nameOrNull.Length > 0)
            {
                InstalledPackage one = _db.Find(nameOrNull);
                if (one == null)
                    throw new Exception(nameOrNull + " is not installed");
                targets.Add(one);
            }
            else
            {
                targets = _db.All();
                SortByName(targets);
            }

            var resolver = new Resolver(_sources, _db, true);
            int failures = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                InstalledPackage current = targets[i];
                PlanItem best = resolver.FindBest(current.Name, null, null);
                if (best == null || Resolver.CompareSemVer(best.Info.Version, current.Version) <= 0)
                {
                    Console.WriteLine(current.Name + " " + current.Version + " already up to date");
                    continue;
                }
                Console.WriteLine(current.Name + " " + current.Version + " -> " + best.Info.Version.ToString());
                try
                {
                    InstallOne(best, false, current.Files);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("neutrinoos: npkg: " + current.Name + ": " + ex.Message);
                    failures++;
                }
            }
            return failures == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("neutrinoos: npkg: " + ex.Message);
            return 1;
        }
    }

    // ==================== install internals ====================

    private void InstallOne(PlanItem item, bool allowUntrusted, List<string> obsoleteFiles)
    {
        string name = item.Info.Name;
        string version = item.Info.Version.ToString();

        byte[] packageBytes = RepoClient.FetchPackage(item.Repo, item.Info);
        Console.WriteLine("fetched " + name);

        NpkgPackage pkg = NpkgPackage.Open(packageBytes);
        string signer = CheckSignature(pkg, name, allowUntrusted);
        Console.WriteLine("verified " + name);

        string badPath;
        if (!pkg.VerifyChecksums(out badPath))
        {
            throw new Exception("checksum verification failed for " + name
                + (badPath != null && badPath.Length > 0 ? " (" + badPath + ")" : ""));
        }

        NpkgManifest manifest = pkg.Manifest;
        if (manifest == null)
            throw new Exception("package " + name + " has no manifest");

        // 1. Extract the payload into the staging area.
        string stagingRoot = NpkgPaths.Staging + "/" + name + "-" + version;
        NpkgPaths.DeleteTree(stagingRoot);
        NpkgPaths.EnsureDirChain(stagingRoot);
        var payload = new List<string>();
        foreach (string archivePath in pkg.PayloadPaths())
        {
            string relative = PayloadKey(archivePath);
            string normalized = Norm(relative);
            if (!SafeRelative(normalized))
                throw new Exception("refusing unsafe payload path: " + relative);
            byte[] data = pkg.ReadPayloadFile(archivePath);
            if (data == null)
                throw new Exception("payload file " + relative + " is missing from the package");
            string staged = stagingRoot + "/" + normalized;
            NpkgPaths.EnsureDirChain(NpkgPaths.ParentOf(staged));
            File.WriteAllBytes(staged, data);
            payload.Add(normalized);
        }
        if (payload.Count == 0)
            throw new Exception("package " + name + " has an empty payload");

        // 2. Decide where everything goes.
        string kind = InstallKind(manifest);
        string installPath = DefaultInstallPath(kind, name, manifest);
        List<Placement> placements = ComputePlacements(kind, installPath, payload);

        // 3. Journaled placement + scripts + database update.
        string txn = Journal.NextTxnId();
        Journal.Begin(txn);
        var added = new List<string>();
        var backups = new List<string>();
        var createdDirs = new List<string>();
        try
        {
            RunScript(manifest.Scripts, "pre-install", name);

            for (int i = 0; i < placements.Count; i++)
            {
                Placement placement = placements[i];
                EnsureTracked(createdDirs, NpkgPaths.ParentOf(placement.Destination));
                PlaceBytes(txn, File.ReadAllBytes(stagingRoot + "/" + placement.Source),
                    placement.Destination, added, backups);
            }

            if (kind == "driver")
            {
                EnsureTracked(createdDirs, NpkgPaths.DriversDir + "/" + name);
                PlaceBytes(txn, pkg.ManifestBytes,
                    NpkgPaths.DriversDir + "/" + name + "/manifest.json", added, backups);
            }

            if (manifest.EntryPoints != null)
            {
                foreach (KeyValuePair<string, string> entry in manifest.EntryPoints)
                {
                    string target = FindPlaced(placements, entry.Value);
                    if (target == null)
                    {
                        string direct = entry.Value;
                        if (direct != null && direct.Length > 0 && direct[0] == '/')
                            target = direct;
                        else
                            throw new Exception("entry point '" + entry.Key + "' refers to payload file '"
                                + entry.Value + "' which the package does not contain");
                    }
                    EnsureTracked(createdDirs, "/bin");
                    PlaceBytes(txn, Encoding.UTF8.GetBytes("run " + target + "\n"),
                        "/bin/" + entry.Key, added, backups);
                }
            }

            if (obsoleteFiles != null)
            {
                for (int i = 0; i < obsoleteFiles.Count; i++)
                {
                    string old = obsoleteFiles[i];
                    if (old == null || old.Length == 0 || ListContains(added, old) || !File.Exists(old))
                        continue;
                    BackupFile(txn, old, backups);
                    Journal.NoteFileRemove(old);
                    File.Delete(old);
                }
            }

            RunScript(manifest.Scripts, "post-install", name);

            if (File.Exists(NpkgPaths.Db))
                BackupFile(txn, NpkgPaths.Db, backups);
            else
                Journal.NoteFileAdd(NpkgPaths.Db);
            InstalledPackage record = BuildRecord(name, version, manifest, signer, installPath, added, createdDirs);
            _db.Add(record);
            _db.Save();

            Journal.Commit(txn);
            NpkgPaths.DeleteTree(stagingRoot);
            Console.WriteLine("installed " + name + " " + version);
        }
        catch (Exception)
        {
            RollbackInMemory(txn, added, backups, createdDirs);
            Journal.MarkRolledBack(txn);
            NpkgPaths.DeleteTree(stagingRoot);
            _db.Load();
            throw;
        }
    }

    private static string CheckSignature(NpkgPackage pkg, string name, bool allowUntrusted)
    {
        List<TrustedKey> keys = RepoStore.LoadTrustedKeys();
        for (int i = 0; i < keys.Count; i++)
        {
            bool valid = false;
            try { valid = pkg.VerifySignature(keys[i].PublicKey); }
            catch (Exception) { valid = false; }
            if (valid)
                return keys[i].Fingerprint;
        }

        if (!allowUntrusted)
            throw new Exception("no trusted key signs " + name + " (use --allow-untrusted to install anyway)");

        Console.WriteLine("WARNING: " + name + " is not signed by a trusted key; installing anyway");
        NpkgManifest manifest = pkg.Manifest;
        if (manifest != null && manifest.Signer != null && manifest.Signer.Length > 0)
            return manifest.Signer;
        return "untrusted";
    }

    private static void RunScript(Dictionary<string, string> scripts, string key, string packageName)
    {
        if (scripts == null || scripts.Count == 0)
            return;
        string command;
        if (!scripts.TryGetValue(key, out command))
            return;
        if (command == null || command.Length == 0)
            return;

        Console.WriteLine(key + ": " + command);
        int exitCode;
        string output = ShellBridge.Exec(command, out exitCode);
        if (output != null && output.Length > 0)
            Console.Write(output);
        if (exitCode != 0)
        {
            throw new Exception(key + " script failed for " + packageName
                + " (exit " + TextConv.LongToString(exitCode) + ")");
        }
    }

    private static string InstallKind(NpkgManifest manifest)
    {
        if (manifest == null)
            return "other";
        if (Provides(manifest, "utility")) return "utility";
        if (Provides(manifest, "application") || Provides(manifest, "app")) return "application";
        if (Provides(manifest, "driver")) return "driver";
        if (Provides(manifest, "library")) return "library";
        return "other";
    }

    private static bool Provides(NpkgManifest manifest, string capability)
    {
        try
        {
            if (manifest.ProvidesCapability(capability))
                return true;
        }
        catch (Exception) { }
        if (manifest.Provides != null)
        {
            foreach (string provided in manifest.Provides)
            {
                if (Str.EqualIgnoreCase(provided, capability))
                    return true;
            }
        }
        return false;
    }

    private static string DefaultInstallPath(string kind, string name, NpkgManifest manifest)
    {
        if (manifest.InstallPath != null && manifest.InstallPath.Length > 0)
            return manifest.InstallPath;
        if (kind == "application") return "/apps/" + name;
        if (kind == "driver") return NpkgPaths.DriversDir + "/" + name;
        if (kind == "library") return "/lib";
        return "/bin";
    }

    private static List<Placement> ComputePlacements(string kind, string installPath, List<string> payload)
    {
        var result = new List<Placement>();
        for (int i = 0; i < payload.Count; i++)
        {
            string source = payload[i];
            string destination;
            if (kind == "application" || kind == "library" || kind == "driver")
                destination = installPath + "/" + source;       // keep the tree
            else
                destination = installPath + "/" + Str.LastSegment(source);  // flat
            result.Add(new Placement(source, destination));
        }
        return result;
    }

    private static string FindPlaced(List<Placement> placements, string payloadPath)
    {
        string wanted = Norm(payloadPath);
        for (int i = 0; i < placements.Count; i++)
        {
            if (placements[i].Source == wanted)
                return placements[i].Destination;
        }
        return null;
    }

    private static string Norm(string path)
    {
        if (path == null)
            return "";
        string result = path;
        while (Str.Starts(result, "./"))
            result = result.Substring(2);
        return result;
    }

    /// <summary>
    /// Strips the archive-level "payload/" prefix from a checksums.sha256
    /// path, yielding the payload-relative key used by the manifest (for
    /// example "payload/hello.dll" -> "hello.dll").
    /// </summary>
    private static string PayloadKey(string archivePath)
    {
        if (archivePath == null)
            return "";
        if (Str.Starts(archivePath, "payload/"))
            return archivePath.Substring(8);
        return archivePath;
    }

    private static bool SafeRelative(string path)
    {
        if (path == null || path.Length == 0)
            return false;
        if (path[0] == '/')
            return false;
        if (Str.Contains(path, ".."))
            return false;
        return true;
    }

    private static InstalledPackage BuildRecord(string name, string version, NpkgManifest manifest,
        string signer, string installPath, List<string> files, List<string> dirs)
    {
        var record = new InstalledPackage();
        record.Name = name;
        record.Version = version;
        record.Architecture = manifest.Architecture != null && manifest.Architecture.Length > 0
            ? manifest.Architecture
            : NpkgPaths.TargetArchitecture;
        record.Signer = signer == null ? "" : signer;
        record.Description = manifest.Description == null ? "" : manifest.Description;
        record.InstallPath = installPath;
        if (manifest.Provides != null)
        {
            foreach (string provided in manifest.Provides)
                record.Provides.Add(provided);
        }
        CopyMap(manifest.Dependencies, record.Dependencies);
        CopyMap(manifest.Scripts, record.Scripts);
        CopyMap(manifest.EntryPoints, record.EntryPoints);
        for (int i = 0; i < files.Count; i++)
            record.Files.Add(files[i]);
        for (int i = 0; i < dirs.Count; i++)
            record.Dirs.Add(dirs[i]);
        return record;
    }

    private static void CopyMap(Dictionary<string, string> from, Dictionary<string, string> to)
    {
        if (from == null)
            return;
        foreach (KeyValuePair<string, string> kv in from)
            to[kv.Key] = kv.Value;
    }

    // ==================== journaled file operations ====================

    private static void PlaceBytes(string txn, byte[] bytes, string destination,
        List<string> added, List<string> backups)
    {
        if (File.Exists(destination))
            BackupFile(txn, destination, backups);
        else
            Journal.NoteFileAdd(destination);
        File.WriteAllBytes(destination, bytes);
        added.Add(destination);
    }

    private static void BackupFile(string txn, string path, List<string> backups)
    {
        string backupPath = Journal.BackupPath(txn, backups.Count);
        NpkgPaths.EnsureDirChain(NpkgPaths.ParentOf(backupPath));
        File.Copy(path, backupPath, true);
        Journal.NoteFileBackup(path, backupPath);
        backups.Add(path);
    }

    private static void RollbackInMemory(string txn, List<string> added, List<string> backups,
        List<string> createdDirs)
    {
        for (int i = added.Count - 1; i >= 0; i--)
        {
            try
            {
                if (File.Exists(added[i]))
                    File.Delete(added[i]);
            }
            catch (Exception) { }
        }
        for (int i = backups.Count - 1; i >= 0; i--)
        {
            string backupPath = Journal.BackupPath(txn, i);
            if (!File.Exists(backupPath))
                continue;
            try
            {
                NpkgPaths.EnsureDirChain(NpkgPaths.ParentOf(backups[i]));
                File.Copy(backupPath, backups[i], true);
            }
            catch (Exception) { }
        }
        for (int i = createdDirs.Count - 1; i >= 0; i--)
        {
            try
            {
                if (Directory.Exists(createdDirs[i]))
                    Directory.Delete(createdDirs[i]);
            }
            catch (Exception) { }
        }
        Console.WriteLine("rollback: transaction " + txn + " undone");
    }

    private static void EnsureTracked(List<string> created, string dir)
    {
        if (dir == null || dir.Length == 0 || dir == "/")
            return;
        string[] parts = Util.SplitList(dir, '/');
        var built = new StringBuilder();
        for (int i = 0; i < parts.Length; i++)
        {
            built.Append('/');
            built.Append(parts[i]);
            string step = built.ToString();
            if (!Directory.Exists(step))
            {
                Directory.CreateDirectory(step);
                created.Add(step);
            }
        }
    }

    // ==================== dependency bookkeeping ====================

    private List<string> FindDependents(string name)
    {
        var result = new List<string>();
        List<InstalledPackage> installed = _db.All();
        for (int i = 0; i < installed.Count; i++)
        {
            InstalledPackage dependent = installed[i];
            if (dependent.Name == name || dependent.Dependencies == null)
                continue;
            foreach (KeyValuePair<string, string> dep in dependent.Dependencies)
            {
                if (dep.Key != name)
                    continue;
                if (!RemainingSatisfier(name, dep.Value, name, dependent.Name))
                    result.Add(dependent.Name + " (needs " + dep.Key + " " + dep.Value + ")");
            }
        }
        return result;
    }

    private bool RemainingSatisfier(string depName, string constraint, string removingName, string dependentName)
    {
        List<InstalledPackage> installed = _db.All();
        for (int i = 0; i < installed.Count; i++)
        {
            InstalledPackage candidate = installed[i];
            if (candidate.Name == removingName || candidate.Name == dependentName)
                continue;
            bool provides = candidate.Name == depName;
            if (!provides && candidate.Provides != null)
            {
                for (int p = 0; p < candidate.Provides.Count; p++)
                {
                    if (candidate.Provides[p] == depName)
                    {
                        provides = true;
                        break;
                    }
                }
            }
            if (provides && Resolver.VersionTextMatches(candidate.Version, constraint, null))
                return true;
        }
        return false;
    }

    private static void SplitSpec(string spec, out string name, out string version)
    {
        name = spec == null ? "" : spec;
        version = null;
        if (spec == null)
            return;
        int at = spec.IndexOf("@");
        if (at > 0)
        {
            name = spec.Substring(0, at);
            version = spec.Substring(at + 1);
            if (version.Length == 0)
                version = null;
        }
    }

    private static bool ListContains(List<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
                return true;
        }
        return false;
    }

    private static string JoinList(List<string> items)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(items[i]);
        }
        return sb.ToString();
    }

    private static void SortByName(List<InstalledPackage> items)
    {
        for (int i = 1; i < items.Count; i++)
        {
            InstalledPackage key = items[i];
            int j = i - 1;
            while (j >= 0 && Util.Compare(items[j].Name, key.Name) > 0)
            {
                items[j + 1] = items[j];
                j--;
            }
            items[j + 1] = key;
        }
    }
}
