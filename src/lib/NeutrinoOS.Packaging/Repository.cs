// NeutrinoOS Phase 8 - shared packaging library (NeutrinoOS.Packaging).
//
// Repository: the signed repository index model (repository.json) used by
// "npkg repo add/search/info" on the device and by the repo server / host
// tooling that generates the index. Writing sorts packages by name and
// version so the index (and therefore its Ed25519 signature) is stable.

using System;
using System.Collections.Generic;

namespace NeutrinoOS.Packaging
{
    /// <summary>
    /// Phase 8: one package entry of a repository index: identity, version,
    /// integrity data (filename/size/SHA-256) and the signer fingerprint.
    /// "File" is an alias of "Filename" kept for the host npkg CLI's object
    /// initializers.
    /// </summary>
    public sealed class RepoPackageInfo
    {
        /// <summary>Phase 8: package name.</summary>
        public string Name { get; set; }

        /// <summary>Phase 8: package version.</summary>
        public SemVersion Version { get; set; }

        /// <summary>Phase 8: architecture the package targets ("x86-64", "arm64", "any").</summary>
        public string Architecture { get; set; }

        /// <summary>Phase 8: package description (may be empty).</summary>
        public string Description { get; set; }

        /// <summary>Phase 8: file name of the .npkg within the repository (for example "hello-1.0.0.npkg").</summary>
        public string Filename { get; set; }

        /// <summary>Phase 8: size of the .npkg file in bytes.</summary>
        public long Size { get; set; }

        /// <summary>Phase 8: SHA-256 of the .npkg file as lowercase hex.</summary>
        public string Sha256 { get; set; }

        /// <summary>Phase 8: signer fingerprint (64 hex chars) or "".</summary>
        public string Signer { get; set; }

        /// <summary>Phase 8: package name to version-constraint text.</summary>
        public Dictionary<string, string> Dependencies { get; set; }

        /// <summary>Phase 8: capability tags provided by the package.</summary>
        public string[] Provides { get; set; }

        /// <summary>Phase 8: alias of Filename (host npkg CLI compatibility).</summary>
        public string File
        {
            get { return Filename; }
            set { Filename = value; }
        }

        /// <summary>Phase 8: creates an empty entry with initialized collections.</summary>
        public RepoPackageInfo()
        {
            Name = "";
            Version = new SemVersion();
            Architecture = "any";
            Description = "";
            Filename = "";
            Size = 0;
            Sha256 = "";
            Signer = "";
            Dependencies = new Dictionary<string, string>();
            Provides = new string[0];
        }

        /// <summary>
        /// Phase 8: serializes the entry (keys: name, version, architecture,
        /// description, filename, size, sha256, signer, dependencies,
        /// provides).
        /// </summary>
        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("name", Name == null ? "" : Name);
            o.Set("version", Version.ToString());
            o.Set("architecture", Architecture == null ? "any" : Architecture);
            o.Set("description", Description == null ? "" : Description);
            o.Set("filename", Filename == null ? "" : Filename);
            o.Set("size", Size);
            o.Set("sha256", Sha256 == null ? "" : Sha256);
            o.Set("signer", Signer == null ? "" : Signer);
            o.Set("dependencies", WriteStringMap(Dependencies));
            o.Set("provides", WriteStringArray(Provides));
            return o;
        }

        /// <summary>
        /// Phase 8: parses a repository entry; missing optional fields take
        /// their defaults. An invalid version raises FormatException.
        /// </summary>
        public static RepoPackageInfo FromJson(JsonObject o)
        {
            if (o == null)
                throw new FormatException("repository: package entry must be a JSON object");

            RepoPackageInfo info = new RepoPackageInfo();
            info.Name = o.GetString("name", "");
            ApplyVersion(info, o);
            info.Architecture = o.GetString("architecture", "any");
            info.Description = o.GetString("description", "");
            info.Filename = o.GetString("filename", "");
            info.Size = o.GetLong("size", 0);
            info.Sha256 = o.GetString("sha256", "");
            info.Signer = o.GetString("signer", "");
            info.Dependencies = ReadStringMap(o, "dependencies");
            info.Provides = ReadStringArray(o, "provides");
            return info;
        }

        /// <summary>
        /// Phase 8: convenience overload accepting the result of Json.Parse
        /// (statically typed object); throws FormatException when the value
        /// is not a JSON object.
        /// </summary>
        public static RepoPackageInfo FromJson(object value)
        {
            JsonObject o = value as JsonObject;
            if (o == null)
                throw new FormatException("repository: package entry must be a JSON object");
            return FromJson(o);
        }

        /// <summary>
        /// Phase 8: parses the optional "version" field into the entry. Kept
        /// as a small standalone method on purpose: the Tier-0 JIT mishandles
        /// the version parse + property assignment shape when it sits inside
        /// a large method body (observed on device as a page fault inside
        /// set_Version with garbage arguments).
        /// </summary>
        private static void ApplyVersion(RepoPackageInfo info, JsonObject o)
        {
            string versionText = o.GetString("version", null);
            if (versionText == null || versionText.Length == 0)
                return;

            SemVersion version;
            if (!SemVersion.TryParse(versionText, out version))
                throw new FormatException("repository: invalid version '" + versionText + "'");
            info.Version = version;
        }

        private static JsonObject WriteStringMap(Dictionary<string, string> map)
        {
            JsonObject o = new JsonObject();
            if (map != null)
            {
                foreach (KeyValuePair<string, string> pair in map)
                    o.Set(pair.Key, pair.Value);
            }
            return o;
        }

        private static JsonArray WriteStringArray(string[] values)
        {
            JsonArray array = new JsonArray();
            if (values != null)
            {
                for (int i = 0; i < values.Length; i++)
                    array.Add(values[i]);
            }
            return array;
        }

        private static Dictionary<string, string> ReadStringMap(JsonObject o, string key)
        {
            Dictionary<string, string> map = new Dictionary<string, string>();
            JsonObject sub = o.GetObject(key);
            if (sub == null)
                return map;
            string[] keys = sub.Keys();
            for (int i = 0; i < keys.Length; i++)
            {
                string s = sub.Get(keys[i]) as string;
                map[keys[i]] = s == null ? "" : s;
            }
            return map;
        }

        private static string[] ReadStringArray(JsonObject o, string key)
        {
            JsonArray array = o.GetArray(key);
            if (array == null)
                return new string[0];
            List<string> values = new List<string>();
            for (int i = 0; i < array.Count; i++)
            {
                string s = array.Get(i) as string;
                if (s != null)
                    values.Add(s);
            }
            return values.ToArray();
        }
    }

    /// <summary>
    /// Phase 8: the package list of a repository index. Derives from
    /// List&lt;RepoPackageInfo&gt; so callers can Add/Count/enumerate, and
    /// provides an implicit conversion from an entry array so
    /// "index.Packages = infos.ToArray()" (host npkg CLI) works as well as
    /// list-style access.
    /// </summary>
    public sealed class RepoPackageList : List<RepoPackageInfo>
    {
        /// <summary>Phase 8: creates an empty package list.</summary>
        public RepoPackageList()
        {
        }

        /// <summary>Phase 8: converts an entry array to a package list.</summary>
        public static implicit operator RepoPackageList(RepoPackageInfo[] items)
        {
            RepoPackageList list = new RepoPackageList();
            if (items != null)
            {
                for (int i = 0; i < items.Length; i++)
                    list.Add(items[i]);
            }
            return list;
        }
    }

    /// <summary>
    /// Phase 8: a repository index (repository.json) listing every package
    /// a repository offers. ToJson sorts packages by name then version so
    /// the serialized index - and its signature - is deterministic.
    /// </summary>
    public sealed class RepositoryIndex
    {
        /// <summary>Phase 8: index format identifier; always "npkg-repo/1".</summary>
        public string Format { get; set; }

        /// <summary>Phase 8: repository name (display only).</summary>
        public string Name { get; set; }

        /// <summary>Phase 8: revision counter; bumped by the repo server when the index changes.</summary>
        public int Revision { get; set; }

        /// <summary>Phase 8: generation timestamp/comment text (may be empty).</summary>
        public string Generated { get; set; }

        /// <summary>Phase 8: packages offered by the repository.</summary>
        public RepoPackageList Packages { get; set; }

        /// <summary>Phase 8: creates an empty index (format "npkg-repo/1").</summary>
        public RepositoryIndex()
        {
            Format = "npkg-repo/1";
            Name = "";
            Revision = 0;
            Generated = "";
            Packages = new RepoPackageList();
        }

        /// <summary>
        /// Phase 8: parses a repository index. A "format" field other than
        /// "npkg-repo/1" raises FormatException; missing optional fields
        /// take their defaults; packages keep their file order (ToJson
        /// re-sorts on write).
        /// </summary>
        public static RepositoryIndex FromJson(JsonObject o)
        {
            if (o == null)
                throw new FormatException("repository: a JSON object is required");

            RepositoryIndex index = new RepositoryIndex();
            index.Format = o.GetString("format", "npkg-repo/1");
            if (index.Format != "npkg-repo/1")
                throw new FormatException("repository: unsupported format '" + index.Format + "' (expected npkg-repo/1)");
            index.Name = o.GetString("name", "");
            index.Revision = (int)o.GetLong("revision", 0);
            index.Generated = o.GetString("generated", "");

            JsonArray packages = o.GetArray("packages");
            // Phase 8: resolve `index.Packages` once into a local and add
            // through it. A chained receiver expression
            // (index.Packages.Add(...)) keeps the property result pending on
            // the eval stack across the Add callvirt; the Tier-0 JIT's
            // callvirt argument setup only ever loaded the raw slots there,
            // so the add targeted a stale receiver and corrupted the list.
            // The local keeps a single, well-formed callvirt shape.
            RepoPackageList pkgs = index.Packages;
            if (packages != null)
            {
                // Phase 8: pre-size to the entry count to skip List growth.
                pkgs.Capacity = packages.Count + 8;
                for (int i = 0; i < packages.Count; i++)
                {
                    RepoPackageInfo info = packages.Get(i) as RepoPackageInfo;
                    if (info == null)
                    {
                        JsonObject entry = packages.Get(i) as JsonObject;
                        if (entry == null)
                            continue;
                        info = RepoPackageInfo.FromJson(entry);
                    }
                    pkgs.Add(info);
                }
            }
            return index;
        }

        /// <summary>
        /// Phase 8: convenience overload accepting the result of Json.Parse
        /// (statically typed object); throws FormatException when the value
        /// is not a JSON object.
        /// </summary>
        public static RepositoryIndex FromJson(object value)
        {
            JsonObject o = value as JsonObject;
            if (o == null)
                throw new FormatException("repository: a JSON object is required");
            return FromJson(o);
        }

        /// <summary>
        /// Phase 8: serializes the index (keys: format, name, revision,
        /// generated, packages) with packages sorted by name and then
        /// version.
        /// </summary>
        public JsonObject ToJson()
        {
            List<RepoPackageInfo> ordered = new List<RepoPackageInfo>();
            if (Packages != null)
            {
                for (int i = 0; i < Packages.Count; i++)
                    ordered.Add(Packages[i]);
            }
            SortPackages(ordered);

            JsonArray array = new JsonArray();
            for (int i = 0; i < ordered.Count; i++)
                array.Add(ordered[i].ToJson());

            JsonObject o = new JsonObject();
            o.Set("format", Format == null ? "npkg-repo/1" : Format);
            o.Set("name", Name == null ? "" : Name);
            o.Set("revision", (long)Revision);
            o.Set("generated", Generated == null ? "" : Generated);
            o.Set("packages", array);
            return o;
        }

        private static void SortPackages(List<RepoPackageInfo> packages)
        {
            // Insertion sort: small lists and a stable, dependency-free order.
            for (int i = 1; i < packages.Count; i++)
            {
                RepoPackageInfo current = packages[i];
                int j = i - 1;
                while (j >= 0 && ComparePackages(current, packages[j]) < 0)
                {
                    packages[j + 1] = packages[j];
                    j--;
                }
                packages[j + 1] = current;
            }
        }

        private static int ComparePackages(RepoPackageInfo a, RepoPackageInfo b)
        {
            int byName = CompareOrdinal(a.Name, b.Name);
            if (byName != 0)
                return byName;
            return a.Version.CompareTo(b.Version);
        }

        private static int CompareOrdinal(string a, string b)
        {
            if (a == null && b == null)
                return 0;
            if (a == null)
                return -1;
            if (b == null)
                return 1;
            int n = a.Length < b.Length ? a.Length : b.Length;
            for (int i = 0; i < n; i++)
            {
                if (a[i] != b[i])
                    return a[i] < b[i] ? -1 : 1;
            }
            if (a.Length == b.Length)
                return 0;
            return a.Length < b.Length ? -1 : 1;
        }
    }
}
