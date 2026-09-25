// NeutrinoOS Phase 8 - shared packaging library (NeutrinoOS.Packaging).
//
// Manifest: the .npkg manifest.json model (NpkgManifest / NpkgDriverInfo).
// Parsed with lenient defaults for missing optional fields, serialized in a
// stable field order so that manifest bytes - and therefore Ed25519
// signatures over them - are reproducible across host and device.

using System;
using System.Collections.Generic;

namespace NeutrinoOS.Packaging
{
    /// <summary>
    /// Phase 8: the optional "driver" section of a .npkg manifest,
    /// describing the device class a driver package supports, the PCI
    /// vendor/device IDs it matches, and its IDriver entry point assembly.
    /// </summary>
    public sealed class NpkgDriverInfo
    {
        /// <summary>Phase 8: device class the driver supports (for example "net", "block", "virtual").</summary>
        public string Class { get; set; }

        /// <summary>Phase 8: PCI vendor IDs the driver matches (hex strings; empty = any).</summary>
        public string[] VendorIds { get; set; }

        /// <summary>Phase 8: PCI device IDs the driver matches (hex strings; empty = any).</summary>
        public string[] DeviceIds { get; set; }

        /// <summary>Phase 8: payload-relative assembly with the driver entry point.</summary>
        public string EntryPoint { get; set; }

        /// <summary>Phase 8: creates an empty driver description (all fields defaulted).</summary>
        public NpkgDriverInfo()
        {
            Class = "";
            VendorIds = new string[0];
            DeviceIds = new string[0];
            EntryPoint = "";
        }

        /// <summary>
        /// Phase 8: serializes the driver section (keys: class, vendorIds,
        /// deviceIds, entryPoint, in that order).
        /// </summary>
        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("class", Class == null ? "" : Class);
            o.Set("vendorIds", ToJsonArray(VendorIds));
            o.Set("deviceIds", ToJsonArray(DeviceIds));
            o.Set("entryPoint", EntryPoint == null ? "" : EntryPoint);
            return o;
        }

        /// <summary>
        /// Phase 8: parses a driver section; missing fields fall back to
        /// their defaults, non-string array elements are skipped.
        /// </summary>
        public static NpkgDriverInfo FromJson(JsonObject o)
        {
            if (o == null)
                throw new FormatException("manifest: driver section must be a JSON object");
            NpkgDriverInfo info = new NpkgDriverInfo();
            info.Class = o.GetString("class", "");
            info.VendorIds = ReadStringArray(o, "vendorIds");
            info.DeviceIds = ReadStringArray(o, "deviceIds");
            info.EntryPoint = o.GetString("entryPoint", "");
            return info;
        }

        /// <summary>
        /// Phase 8: convenience overload accepting the result of Json.Parse
        /// (which is statically typed object); throws FormatException when
        /// the value is not a JSON object.
        /// </summary>
        public static NpkgDriverInfo FromJson(object value)
        {
            JsonObject o = value as JsonObject;
            if (o == null)
                throw new FormatException("manifest: driver section must be a JSON object");
            return FromJson(o);
        }

        private static JsonArray ToJsonArray(string[] values)
        {
            JsonArray array = new JsonArray();
            if (values != null)
            {
                for (int i = 0; i < values.Length; i++)
                    array.Add(values[i]);
            }
            return array;
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
    /// Phase 8: the parsed manifest.json of a .npkg package. Format is
    /// always "npkg/1". Unknown JSON fields are ignored on read; ToJson
    /// always writes every field in the documented stable order.
    /// </summary>
    public sealed class NpkgManifest
    {
        /// <summary>Phase 8: manifest format identifier; always "npkg/1".</summary>
        public string Format { get; set; }

        /// <summary>Phase 8: package name (for example "neutrinoos.utils.core").</summary>
        public string Name { get; set; }

        /// <summary>Phase 8: package semantic version.</summary>
        public SemVersion Version { get; set; }

        /// <summary>Phase 8: "x86-64", "arm64" or "any" (default "any").</summary>
        public string Architecture { get; set; }

        /// <summary>Phase 8: author name (may be empty).</summary>
        public string Author { get; set; }

        /// <summary>Phase 8: one-line description (may be empty).</summary>
        public string Description { get; set; }

        /// <summary>Phase 8: license identifier (may be empty).</summary>
        public string License { get; set; }

        /// <summary>Phase 8: project homepage URL (may be empty).</summary>
        public string Homepage { get; set; }

        /// <summary>Phase 8: signer fingerprint (64 hex chars of SHA-256 over the Ed25519 public key) or "".</summary>
        public string Signer { get; set; }

        /// <summary>Phase 8: installation root (default "/bin"); "/apps" and "/drivers" are the other conventions.</summary>
        public string InstallPath { get; set; }

        /// <summary>Phase 8: capability tags provided by the package ("utility", "application", "driver", ...).</summary>
        public List<string> Provides { get; set; }

        /// <summary>Phase 8: package name to version-constraint text (VersionConstraint grammar).</summary>
        public Dictionary<string, string> Dependencies { get; set; }

        /// <summary>Phase 8: entry point name to payload-relative assembly path (e.g. "hello" -> "hello.dll").</summary>
        public Dictionary<string, string> EntryPoints { get; set; }

        /// <summary>Phase 8: lifecycle scripts keyed by "pre-install"/"post-install"/"pre-remove"/"post-remove".</summary>
        public Dictionary<string, string> Scripts { get; set; }

        /// <summary>Phase 8: driver section of a driver package; null for non-driver packages.</summary>
        public NpkgDriverInfo Driver { get; set; }

        /// <summary>Phase 8: creates an empty manifest with all defaults (format "npkg/1", architecture "any", installPath "/bin").</summary>
        public NpkgManifest()
        {
            Format = "npkg/1";
            Name = "";
            Version = new SemVersion();
            Architecture = "any";
            Author = "";
            Description = "";
            License = "";
            Homepage = "";
            Signer = "";
            InstallPath = "/bin";
            Provides = new List<string>();
            Dependencies = new Dictionary<string, string>();
            EntryPoints = new Dictionary<string, string>();
            Scripts = new Dictionary<string, string>();
            Driver = null;
        }

        /// <summary>
        /// Phase 8: parses a manifest object. Missing optional fields take
        /// their defaults; a "format" field other than "npkg/1" and an
        /// invalid version raise FormatException.
        /// </summary>
        public static NpkgManifest FromJson(JsonObject o)
        {
            if (o == null)
                throw new FormatException("manifest: a JSON object is required");

            NpkgManifest manifest = new NpkgManifest();
            manifest.Format = o.GetString("format", "npkg/1");
            if (manifest.Format != "npkg/1")
                throw new FormatException("manifest: unsupported format '" + manifest.Format + "' (expected npkg/1)");

            manifest.Name = o.GetString("name", "");
            ApplyVersion(manifest, o);
            manifest.Architecture = o.GetString("architecture", "any");
            manifest.Author = o.GetString("author", "");
            manifest.Description = o.GetString("description", "");
            manifest.License = o.GetString("license", "");
            manifest.Homepage = o.GetString("homepage", "");
            manifest.Signer = o.GetString("signer", "");
            manifest.InstallPath = o.GetString("installPath", "/bin");
            manifest.Provides = ReadStringList(o, "provides");
            manifest.Dependencies = ReadStringMap(o, "dependencies");
            manifest.EntryPoints = ReadStringMap(o, "entryPoints");
            manifest.Scripts = ReadStringMap(o, "scripts");
            manifest.Driver = o.GetObject("driver") != null ? NpkgDriverInfo.FromJson(o.GetObject("driver")) : null;
            return manifest;
        }

        /// <summary>
        /// Phase 8: convenience overload accepting the result of Json.Parse
        /// (statically typed object); throws FormatException when the value
        /// is not a JSON object.
        /// </summary>
        public static NpkgManifest FromJson(object value)
        {
            JsonObject o = value as JsonObject;
            if (o == null)
                throw new FormatException("manifest: a JSON object is required");
            return FromJson(o);
        }

        /// <summary>
        /// Phase 8: parses the optional "version" field into the manifest.
        /// Kept as a small standalone method on purpose (see
        /// Repository.ApplyVersion: Tier-0 JIT hazard in large bodies).
        /// </summary>
        private static void ApplyVersion(NpkgManifest manifest, JsonObject o)
        {
            string versionText = o.GetString("version", null);
            if (versionText == null || versionText.Length == 0)
                return;

            SemVersion version;
            if (!SemVersion.TryParse(versionText, out version))
                throw new FormatException("manifest: invalid version '" + versionText + "'");
            manifest.Version = version;
        }

        /// <summary>
        /// Phase 8: serializes the manifest in the stable field order
        /// (format, name, version, architecture, author, description,
        /// license, homepage, signer, installPath, provides, dependencies,
        /// entryPoints, scripts, driver). The driver section is omitted when
        /// null.
        /// </summary>
        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("format", Format == null ? "npkg/1" : Format);
            o.Set("name", Name == null ? "" : Name);
            o.Set("version", Version.ToString());
            o.Set("architecture", Architecture == null ? "any" : Architecture);
            o.Set("author", Author == null ? "" : Author);
            o.Set("description", Description == null ? "" : Description);
            o.Set("license", License == null ? "" : License);
            o.Set("homepage", Homepage == null ? "" : Homepage);
            o.Set("signer", Signer == null ? "" : Signer);
            o.Set("installPath", InstallPath == null ? "/bin" : InstallPath);
            o.Set("provides", WriteStringList(Provides));
            o.Set("dependencies", WriteStringMap(Dependencies));
            o.Set("entryPoints", WriteStringMap(EntryPoints));
            o.Set("scripts", WriteStringMap(Scripts));
            if (Driver != null)
                o.Set("driver", Driver.ToJson());
            return o;
        }

        /// <summary>
        /// Phase 8: true when the package declares the given capability tag
        /// (exact match against "provides").
        /// </summary>
        public bool ProvidesCapability(string capability)
        {
            if (capability == null || Provides == null)
                return false;
            for (int i = 0; i < Provides.Count; i++)
            {
                if (Provides[i] == capability)
                    return true;
            }
            return false;
        }

        private static List<string> ReadStringList(JsonObject o, string key)
        {
            List<string> values = new List<string>();
            JsonArray array = o.GetArray(key);
            if (array == null)
                return values;
            for (int i = 0; i < array.Count; i++)
            {
                string s = array.Get(i) as string;
                if (s != null)
                    values.Add(s);
            }
            return values;
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

        private static JsonArray WriteStringList(List<string> values)
        {
            JsonArray array = new JsonArray();
            if (values != null)
            {
                for (int i = 0; i < values.Count; i++)
                    array.Add(values[i]);
            }
            return array;
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
    }
}
