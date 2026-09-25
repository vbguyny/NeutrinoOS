// NeutrinoOS Phase 8 - npkg: installed-package database
//
// The database lives at /var/lib/npkg/installed.json:
//
//   {"format":"npkg-db/1","packages":[
//      {"name":..,"version":..,"architecture":..,"signer":..,
//       "description":..,"installPath":..,
//       "provides":[..],"dependencies":{..},"scripts":{..},
//       "entryPoints":{..},"files":[..],"dirs":[..]}]}
//
// `files` lists every absolute path npkg created for the package
// (including the /bin wrapper scripts) and `dirs` lists every directory
// npkg had to create; removal uses both lists to undo an install.
// `description` and `entryPoints` are npkg extensions used by
// `npkg list` / `npkg info` (readers tolerate their absence).
//
// Saving is read-modify-write of one whole file: the new text goes to
// installed.json.tmp first and is then copied over the real file (the
// FAT bridge has no atomic rename), so a crash can never leave a
// half-written database behind.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NeutrinoOS.Packaging;

namespace NeutrinoOS.Utility.Npkg;

/// <summary>One entry of the npkg installed database.</summary>
public sealed class InstalledPackage
{
    /// <summary>Package name (unique key of the database).</summary>
    public string Name;

    /// <summary>Installed version (semantic version text).</summary>
    public string Version;

    /// <summary>Architecture the package was installed for.</summary>
    public string Architecture;

    /// <summary>Fingerprint (hex) of the trusted key that signed the package,
    /// or the manifest's signer field when it was installed untrusted.</summary>
    public string Signer;

    /// <summary>Manifest description (npkg extension, may be empty).</summary>
    public string Description;

    /// <summary>Capability tags the package provides ("utility", "driver", ...).</summary>
    public List<string> Provides;

    /// <summary>Dependency name -> version constraint.</summary>
    public Dictionary<string, string> Dependencies;

    /// <summary>Main installation directory chosen at install time.</summary>
    public string InstallPath;

    /// <summary>Absolute paths of every file npkg placed for the package.</summary>
    public List<string> Files;

    /// <summary>Directories npkg created for the package (removed when empty).</summary>
    public List<string> Dirs;

    /// <summary>Lifecycle scripts recorded from the manifest.</summary>
    public Dictionary<string, string> Scripts;

    /// <summary>Entry point name -> payload-relative assembly path.</summary>
    public Dictionary<string, string> EntryPoints;

    /// <summary>Creates an empty record with initialized collections.</summary>
    public InstalledPackage()
    {
        Name = "";
        Version = "";
        Architecture = "";
        Signer = "";
        Description = "";
        Provides = new List<string>();
        Dependencies = new Dictionary<string, string>();
        InstallPath = "";
        Files = new List<string>();
        Dirs = new List<string>();
        Scripts = new Dictionary<string, string>();
        EntryPoints = new Dictionary<string, string>();
    }
}

/// <summary>
/// In-memory view of /var/lib/npkg/installed.json with load/save and
/// name-keyed lookup. The database stays untouched until Save is called,
/// which is what makes install rollback (delete files, restore backups)
/// leave a consistent record behind.
/// </summary>
public sealed class InstalledDatabase
{
    private readonly Dictionary<string, InstalledPackage> _byName = new Dictionary<string, InstalledPackage>();

    /// <summary>Reloads the database from disk; a missing file means "no packages".</summary>
    public void Load()
    {
        _byName.Clear();
        if (!File.Exists(NpkgPaths.Db))
            return;

        JsonObject root = Jsn.ParseObject(File.ReadAllText(NpkgPaths.Db));
        if (root == null || !root.Has("packages"))
            return;

        JsonArray arr = null;
        try { arr = root.GetArray("packages"); } catch (Exception) { return; }
        if (arr == null)
            return;

        for (int i = 0; i < arr.Count; i++)
        {
            object value = arr.Get(i);
            JsonObject obj = value as JsonObject;
            if (obj == null)
                continue;
            InstalledPackage pkg = FromJson(obj);
            if (pkg.Name.Length > 0)
                _byName[pkg.Name] = pkg;
        }
    }

    /// <summary>Writes the database atomically-ish (temp file, then copy).</summary>
    public void Save()
    {
        var sb = new StringBuilder();
        sb.Append("{\"format\":\"npkg-db/1\",\"packages\":[");
        bool first = true;
        foreach (InstalledPackage pkg in _byName.Values)
        {
            if (!first)
                sb.Append(',');
            first = false;
            AppendPackage(sb, pkg);
        }
        sb.Append("]}\n");

        string temp = NpkgPaths.Db + ".tmp";
        File.WriteAllText(temp, sb.ToString());
        if (File.Exists(NpkgPaths.Db))
            File.Delete(NpkgPaths.Db);
        File.Copy(temp, NpkgPaths.Db, false);
        File.Delete(temp);
    }

    /// <summary>Looks a package up by name; null when not installed.</summary>
    public InstalledPackage Find(string name)
    {
        if (name == null)
            return null;
        InstalledPackage pkg;
        if (_byName.TryGetValue(name, out pkg))
            return pkg;
        return null;
    }

    /// <summary>Inserts or replaces the record for a package name.</summary>
    public void Add(InstalledPackage pkg)
    {
        if (pkg == null || pkg.Name == null || pkg.Name.Length == 0)
            return;
        _byName[pkg.Name] = pkg;
    }

    /// <summary>Removes a package record; false when it was not installed.</summary>
    public bool Remove(string name)
    {
        if (name == null)
            return false;
        return _byName.Remove(name);
    }

    /// <summary>All records, in dictionary order (callers sort for display).</summary>
    public List<InstalledPackage> All()
    {
        return new List<InstalledPackage>(_byName.Values);
    }

    private static InstalledPackage FromJson(JsonObject obj)
    {
        var pkg = new InstalledPackage();
        pkg.Name = Jsn.GetStr(obj, "name");
        pkg.Version = Jsn.GetStr(obj, "version");
        pkg.Architecture = Jsn.GetStr(obj, "architecture");
        pkg.Signer = Jsn.GetStr(obj, "signer");
        pkg.Description = Jsn.GetStr(obj, "description");
        pkg.InstallPath = Jsn.GetStr(obj, "installPath");

        string[] provides = Jsn.StrArray(obj, "provides");
        for (int i = 0; i < provides.Length; i++)
            pkg.Provides.Add(provides[i]);

        pkg.Dependencies = Jsn.DictOf(obj, "dependencies");
        pkg.Scripts = Jsn.DictOf(obj, "scripts");
        pkg.EntryPoints = Jsn.DictOf(obj, "entryPoints");

        string[] files = Jsn.StrArray(obj, "files");
        for (int i = 0; i < files.Length; i++)
            pkg.Files.Add(files[i]);

        string[] dirs = Jsn.StrArray(obj, "dirs");
        for (int i = 0; i < dirs.Length; i++)
            pkg.Dirs.Add(dirs[i]);

        return pkg;
    }

    private static void AppendPackage(StringBuilder sb, InstalledPackage pkg)
    {
        sb.Append('{');
        sb.Append("\"name\":");
        sb.Append(Jsn.Quote(pkg.Name));
        sb.Append(",\"version\":");
        sb.Append(Jsn.Quote(pkg.Version));
        sb.Append(",\"architecture\":");
        sb.Append(Jsn.Quote(pkg.Architecture));
        sb.Append(",\"signer\":");
        sb.Append(Jsn.Quote(pkg.Signer));
        sb.Append(",\"description\":");
        sb.Append(Jsn.Quote(pkg.Description));
        sb.Append(",\"installPath\":");
        sb.Append(Jsn.Quote(pkg.InstallPath));
        sb.Append(",\"provides\":");
        AppendStringArray(sb, pkg.Provides);
        sb.Append(",\"dependencies\":");
        AppendStringMap(sb, pkg.Dependencies);
        sb.Append(",\"scripts\":");
        AppendStringMap(sb, pkg.Scripts);
        sb.Append(",\"entryPoints\":");
        AppendStringMap(sb, pkg.EntryPoints);
        sb.Append(",\"files\":");
        AppendStringArray(sb, pkg.Files);
        sb.Append(",\"dirs\":");
        AppendStringArray(sb, pkg.Dirs);
        sb.Append('}');
    }

    private static void AppendStringArray(StringBuilder sb, List<string> items)
    {
        sb.Append('[');
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(Jsn.Quote(items[i]));
        }
        sb.Append(']');
    }

    private static void AppendStringMap(StringBuilder sb, Dictionary<string, string> map)
    {
        sb.Append('{');
        bool first = true;
        foreach (KeyValuePair<string, string> kv in map)
        {
            if (!first)
                sb.Append(',');
            first = false;
            sb.Append(Jsn.Quote(kv.Key));
            sb.Append(':');
            sb.Append(Jsn.Quote(kv.Value));
        }
        sb.Append('}');
    }
}
