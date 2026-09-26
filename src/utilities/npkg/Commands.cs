// NeutrinoOS Phase 8 - npkg: command implementations
//
// Everything the CLI can do is implemented here and glued to the
// argument parser in Program.cs. Read-only commands (list/search/info/
// repo list) never touch the journal; mutating commands go through the
// Installer, which journals every file operation.

using System;
using System.Collections.Generic;
using System.IO;
using NeutrinoOS.Packaging;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Npkg;

/// <summary>Implementations of the npkg subcommands (see file header).</summary>
public static class Commands
{
    /// <summary>
    /// Loads every configured repository index. Unreachable repositories
    /// print a warning and are skipped, so a broken mirror does not block
    /// local installs.
    /// </summary>
    public static List<PackageSource> LoadSources()
    {
        var sources = new List<PackageSource>();
        List<RepoConfig> repos = RepoStore.LoadRepos();
        for (int i = 0; i < repos.Count; i++)
        {
            RepositoryIndex index;
            string error;
            if (RepoClient.TryFetchIndex(repos[i], out index, out error))
            {
                sources.Add(new PackageSource(repos[i], index));
            }
            else
            {
                Console.Error.WriteLine("neutrinoos: npkg: repository " + repos[i].Name + " unavailable: " + error);
            }
        }
        return sources;
    }

    /// <summary>Lists installed packages: name, version, arch, signer prefix, description.</summary>
    public static int List()
    {
        var db = new InstalledDatabase();
        db.Load();
        List<InstalledPackage> all = db.All();
        SortByName(all);

        if (all.Count == 0)
        {
            Console.WriteLine("no packages installed");
            return 0;
        }
        for (int i = 0; i < all.Count; i++)
        {
            InstalledPackage pkg = all[i];
            Console.WriteLine(Pad(pkg.Name, 22) + " " + Pad(pkg.Version, 12) + " " + Pad(pkg.Architecture, 8)
                + " " + Pad(ShortHex(pkg.Signer, 8), 8) + " "
                + (pkg.Description == null ? "" : pkg.Description));
        }
        return 0;
    }

    /// <summary>Searches all repository indexes by substring over name and description.</summary>
    public static int Search(string query)
    {
        List<PackageSource> sources = LoadSources();
        if (sources.Count == 0)
        {
            Console.Error.WriteLine("neutrinoos: npkg: no repositories configured (add one with 'npkg repo add')");
            return 1;
        }

        string needle = query.ToLower();
        int matches = 0;
        for (int s = 0; s < sources.Count; s++)
        {
            RepositoryIndex index = sources[s].Index;
            if (index == null || index.Packages == null)
                continue;
            foreach (RepoPackageInfo pkg in index.Packages)
            {
                string name = pkg.Name == null ? "" : pkg.Name;
                string description = pkg.Description == null ? "" : pkg.Description;
                if (Str.Contains(name.ToLower(), needle) || Str.Contains(description.ToLower(), needle))
                {
                    Console.WriteLine(Pad(name, 22) + " " + Pad(pkg.Version.ToString(), 12) + " ["
                        + sources[s].Repo.Name + "] " + description);
                    matches++;
                }
            }
        }

        if (matches == 0)
        {
            Console.WriteLine("no packages matching '" + query + "'");
            return 1;
        }
        return 0;
    }

    /// <summary>Shows installed package details, or the best repository candidate.</summary>
    public static int Info(string name)
    {
        var db = new InstalledDatabase();
        db.Load();

        InstalledPackage installed = db.Find(name);
        if (installed != null)
        {
            Console.WriteLine("name: " + installed.Name);
            Console.WriteLine("version: " + installed.Version);
            Console.WriteLine("architecture: " + installed.Architecture);
            Console.WriteLine("signer: " + ShowSigner(installed.Signer));
            Console.WriteLine("description: " + installed.Description);
            Console.WriteLine("install path: " + installed.InstallPath);
            Console.WriteLine("depends: " + MapText(installed.Dependencies));
            Console.WriteLine("provides: " + SeqText(installed.Provides));
            Console.WriteLine("entry points: " + MapText(installed.EntryPoints));
            Console.WriteLine("files (" + TextConv.LongToString(installed.Files.Count) + "):");
            for (int i = 0; i < installed.Files.Count; i++)
                Console.WriteLine("  " + installed.Files[i]);
            return 0;
        }

        List<PackageSource> sources = LoadSources();
        var resolver = new Resolver(sources, db);
        PlanItem best = resolver.FindBest(name, null, null);
        if (best == null)
        {
            Console.Error.WriteLine("neutrinoos: npkg: " + name + ": package not found");
            return 1;
        }

        Console.WriteLine("name: " + best.Info.Name);
        Console.WriteLine("version: " + best.Info.Version.ToString());
        Console.WriteLine("architecture: " + best.Info.Architecture);
        Console.WriteLine("repository: " + best.Repo.Name);
        Console.WriteLine("description: " + (best.Info.Description == null ? "" : best.Info.Description));
        Console.WriteLine("filename: " + best.Info.Filename);
        Console.WriteLine("sha256: " + best.Info.Sha256);
        Console.WriteLine("signer: " + ShowSigner(best.Info.Signer));
        Console.WriteLine("depends: " + MapText(best.Info.Dependencies));
        Console.WriteLine("provides: " + SeqText(best.Info.Provides));
        return 0;
    }

    /// <summary>
    /// Adds or updates a repository: fetches repo.pub, checks an optional
    /// pinned fingerprint, stores the key in trusted-keys/ and records the
    /// repository in repos.json (with an ordering priority).
    /// </summary>
    public static int RepoAdd(string name, string url, string fingerprintOption, int priority)
    {
        if (Str.Starts(url, "https://"))
        {
            Console.Error.WriteLine("neutrinoos: npkg: https:// repositories are not supported yet:");
            Console.Error.WriteLine("  the device has no TLS client in Phase 8 (see docs/PHASE8-ECOSYSTEM.md).");
            Console.Error.WriteLine("  Package integrity is still protected by Ed25519 signatures over http://.");
            return 1;
        }

        byte[] publicKeyFile;
        try
        {
            publicKeyFile = RepoClient.FetchUrl(Str.JoinPath(url, "repo.pub"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("neutrinoos: npkg: cannot fetch repository key: " + ex.Message);
            return 1;
        }

        // Normalize before fingerprinting: repo.pub may be raw 32-byte or
        // ASCII hex, and the pin recorded here must match what FetchIndex
        // later computes from the same file.
        byte[] publicKey = RepoClient.NormalizePublicKeyFile(publicKeyFile);
        if (publicKey == null)
        {
            Console.Error.WriteLine("neutrinoos: npkg: repo.pub is neither a 32-byte key nor 64 hex characters");
            return 1;
        }

        string fingerprint = NpkgPackage.Fingerprint(publicKey);
        if (fingerprintOption != null && fingerprintOption.Length > 0
            && !Str.EqualIgnoreCase(fingerprintOption, fingerprint))
        {
            Console.Error.WriteLine("neutrinoos: npkg: fingerprint mismatch: expected " + fingerprintOption
                + ", got " + fingerprint);
            return 1;
        }

        NpkgPaths.EnsureDirs();
        RepoStore.WriteKeyFile(name, publicKey);
        RepoStore.AddOrUpdate(name, url, fingerprint, priority);
        Console.WriteLine("added repository " + name + " (" + url + ")"
            + (priority >= 0 ? " priority " + TextConv.LongToString(priority) : "")
            + " fingerprint " + ShortHex(fingerprint, 8) + "...");
        if (fingerprintOption == null || fingerprintOption.Length == 0)
        {
            Console.WriteLine("warning: this repository is not pinned yet; verify the fingerprint and re-add with");
            Console.WriteLine("  npkg repo add " + name + " " + url + " --fingerprint " + fingerprint);
        }
        return 0;
    }

    /// <summary>Prints the configured repositories as a table.</summary>
    public static int RepoList()
    {
        List<RepoConfig> repos = RepoStore.LoadRepos();
        if (repos.Count == 0)
        {
            Console.WriteLine("no repositories configured (add one with 'npkg repo add')");
            return 0;
        }
        Console.WriteLine(Pad("NAME", 16) + " " + Pad("URL", 40) + " " + Pad("PRIO", 5) + " FINGERPRINT");
        for (int i = 0; i < repos.Count; i++)
        {
            RepoConfig repo = repos[i];
            string fingerprint = repo.Fingerprint == null || repo.Fingerprint.Length == 0
                ? "(unpinned)"
                : repo.Fingerprint;
            Console.WriteLine(Pad(repo.Name, 16) + " " + Pad(repo.Url, 40) + " "
                + Pad(TextConv.LongToString(repo.Priority), 5) + " " + fingerprint);
        }
        return 0;
    }

    /// <summary>Removes a repository entry; --key also deletes its trusted key file.</summary>
    public static int RepoRemove(string name, bool removeKey)
    {
        if (!RepoStore.RemoveRepo(name))
        {
            Console.Error.WriteLine("neutrinoos: npkg: repository not found: " + name);
            return 1;
        }
        Console.WriteLine("removed repository " + name);

        if (removeKey)
        {
            if (RepoStore.RemoveKeyFile(name))
                Console.WriteLine("removed trusted key " + name + ".pub");
            else
                Console.WriteLine("no trusted key file for " + name);
        }
        else
        {
            Console.WriteLine("note: trusted key " + name + ".pub kept (use --key to remove it)");
        }
        return 0;
    }

    /// <summary>
    /// Verifies a local .npkg file: payload checksums plus the signature
    /// against the trusted key ring. Exit code 0 only when the checksums
    /// pass and a trusted key signs the package.
    /// </summary>
    public static int VerifyFile(string path)
    {
        if (path == null || path.Length == 0 || !File.Exists(path))
        {
            Console.Error.WriteLine("neutrinoos: npkg: " + path + ": no such file");
            return 1;
        }

        NpkgPackage pkg;
        try
        {
            pkg = NpkgPackage.Open(File.ReadAllBytes(path));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("neutrinoos: npkg: " + path + ": " + ex.Message);
            return 1;
        }

        NpkgManifest manifest = pkg.Manifest;
        string manifestSigner = manifest != null && manifest.Signer != null ? manifest.Signer : "";
        Console.WriteLine("name: " + (manifest == null ? "" : manifest.Name));
        Console.WriteLine("version: " + (manifest == null ? "" : manifest.Version));
        Console.WriteLine("signer: " + ShowSigner(manifestSigner));

        string badPath;
        bool checksumsOk = pkg.VerifyChecksums(out badPath);
        Console.WriteLine("checksums: " + (checksumsOk
            ? "OK"
            : "FAIL" + (badPath != null && badPath.Length > 0 ? " (" + badPath + ")" : "")));

        string signerHex = null;
        List<TrustedKey> keys = RepoStore.LoadTrustedKeys();
        for (int i = 0; i < keys.Count; i++)
        {
            bool valid = false;
            try { valid = pkg.VerifySignature(keys[i].PublicKey); }
            catch (Exception) { valid = false; }
            if (valid)
            {
                signerHex = keys[i].Fingerprint;
                break;
            }
        }

        if (signerHex != null)
            Console.WriteLine("signature: OK (signer " + ShortHex(signerHex, 8) + ")");
        else
            Console.WriteLine("signature: UNTRUSTED (signer " + ShortHex(manifestSigner, 8) + ")");

        return checksumsOk && signerHex != null ? 0 : 1;
    }

    /// <summary>Installs a package (see Installer.Install).</summary>
    public static int Install(string spec, bool allowUntrusted, bool force)
    {
        var db = new InstalledDatabase();
        List<PackageSource> sources = LoadSources();
        return new Installer(db, sources).Install(spec, allowUntrusted, force);
    }

    /// <summary>Removes a package (see Installer.Remove).</summary>
    public static int Remove(string name, bool force)
    {
        var db = new InstalledDatabase();
        List<PackageSource> sources = LoadSources();
        return new Installer(db, sources).Remove(name, force);
    }

    /// <summary>Upgrades one or all packages (see Installer.Upgrade).</summary>
    public static int Upgrade(string nameOrNull)
    {
        var db = new InstalledDatabase();
        List<PackageSource> sources = LoadSources();
        return new Installer(db, sources).Upgrade(nameOrNull);
    }

    // ==================== formatting helpers ====================

    private static string Pad(string text, int width)
    {
        string value = text == null ? "" : text;
        if (value.Length >= width)
            return value;
        var sb = new System.Text.StringBuilder(width);
        sb.Append(value);
        for (int i = value.Length; i < width; i++)
            sb.Append(' ');
        return sb.ToString();
    }

    private static string ShortHex(string value, int digits)
    {
        if (value == null || value.Length == 0)
            return "-";
        if (value.Length <= digits)
            return value;
        return value.Substring(0, digits);
    }

    private static string ShowSigner(string signer)
    {
        if (signer == null || signer.Length == 0)
            return "(unsigned)";
        return signer;
    }

    private static string MapText(Dictionary<string, string> map)
    {
        if (map == null || map.Count == 0)
            return "(none)";
        var sb = new System.Text.StringBuilder();
        bool first = true;
        foreach (KeyValuePair<string, string> kv in map)
        {
            if (!first)
                sb.Append(", ");
            first = false;
            sb.Append(kv.Key);
            if (kv.Value != null && kv.Value.Length > 0)
            {
                sb.Append(" (");
                sb.Append(kv.Value);
                sb.Append(')');
            }
        }
        return sb.ToString();
    }

    private static string SeqText(System.Collections.IEnumerable items)
    {
        if (items == null)
            return "(none)";
        var sb = new System.Text.StringBuilder();
        bool first = true;
        foreach (object item in items)
        {
            if (!first)
                sb.Append(", ");
            first = false;
            string text = item as string;
            sb.Append(text == null ? "" : text);
        }
        if (first)
            return "(none)";
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
