// NeutrinoOS Phase 8 - npkg: repositories + trusted keys
//
// /etc/npkg/repos.json
//   {"repos":[{"name":"main","url":"http://host/repo","fingerprint":"<hex64>"}]}
//
// /etc/npkg/trusted-keys/<repo>.pub
//   either the raw 32 Ed25519 public-key bytes or the key as 64 hex
//   characters of text (npkg repo add writes the hex form).
//
// Only keys in trusted-keys/ make a package signature "trusted"; the
// fingerprint recorded in repos.json is an additional pin that a
// repository's repo.pub must match, catching key substitution.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NeutrinoOS.Packaging;
using ProtonOS.DDK.Crypto;

namespace NeutrinoOS.Utility.Npkg;

/// <summary>One configured repository.</summary>
public sealed class RepoConfig
{
    /// <summary>Short repository name (also the trusted key file name).</summary>
    public string Name;

    /// <summary>Base URL: http://host[:port]/path or file:///path or /path.</summary>
    public string Url;

    /// <summary>Pinned fingerprint (hex) of the repository's signing key; may be empty.</summary>
    public string Fingerprint;

    /// <summary>Creates an empty repository record.</summary>
    public RepoConfig()
    {
        Name = "";
        Url = "";
        Fingerprint = "";
    }
}

/// <summary>One trusted Ed25519 public key loaded from trusted-keys/.</summary>
public sealed class TrustedKey
{
    /// <summary>File name (without ".pub") the key was loaded from.</summary>
    public string Name;

    /// <summary>Raw 32-byte Ed25519 public key.</summary>
    public byte[] PublicKey;

    /// <summary>Key fingerprint (hex) as produced by NpkgPackage.Fingerprint.</summary>
    public string Fingerprint;
}

/// <summary>Reader/writer for the repository list and the trusted key ring.</summary>
public static class RepoStore
{
    /// <summary>Loads repos.json; a missing file yields an empty list.</summary>
    public static List<RepoConfig> LoadRepos()
    {
        var repos = new List<RepoConfig>();
        if (!File.Exists(NpkgPaths.ReposFile))
            return repos;

        JsonObject root = Jsn.ParseObject(File.ReadAllText(NpkgPaths.ReposFile));
        if (root == null || !root.Has("repos"))
            return repos;

        JsonArray arr = null;
        try { arr = root.GetArray("repos"); } catch (Exception) { return repos; }
        if (arr == null)
            return repos;

        for (int i = 0; i < arr.Count; i++)
        {
            object value = arr.Get(i);
            JsonObject obj = value as JsonObject;
            if (obj == null)
                continue;
            var repo = new RepoConfig();
            repo.Name = Jsn.GetStr(obj, "name");
            repo.Url = Jsn.GetStr(obj, "url");
            repo.Fingerprint = Jsn.GetStr(obj, "fingerprint");
            if (repo.Name.Length > 0 && repo.Url.Length > 0)
                repos.Add(repo);
        }
        return repos;
    }

    /// <summary>Writes repos.json (temp file, then copy).</summary>
    public static void SaveRepos(List<RepoConfig> repos)
    {
        var sb = new StringBuilder();
        sb.Append("{\"repos\":[");
        for (int i = 0; i < repos.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append("{\"name\":");
            sb.Append(Jsn.Quote(repos[i].Name));
            sb.Append(",\"url\":");
            sb.Append(Jsn.Quote(repos[i].Url));
            sb.Append(",\"fingerprint\":");
            sb.Append(Jsn.Quote(repos[i].Fingerprint));
            sb.Append('}');
        }
        sb.Append("]}\n");

        NpkgPaths.EnsureDirChain(NpkgPaths.EtcDir);
        string temp = NpkgPaths.ReposFile + ".tmp";
        File.WriteAllText(temp, sb.ToString());
        if (File.Exists(NpkgPaths.ReposFile))
            File.Delete(NpkgPaths.ReposFile);
        File.Copy(temp, NpkgPaths.ReposFile, false);
        File.Delete(temp);
    }

    /// <summary>Adds or updates a repository entry (matched by name).</summary>
    public static void AddOrUpdate(string name, string url, string fingerprint)
    {
        List<RepoConfig> repos = LoadRepos();
        bool replaced = false;
        for (int i = 0; i < repos.Count; i++)
        {
            if (repos[i].Name == name)
            {
                repos[i].Url = url;
                repos[i].Fingerprint = fingerprint;
                replaced = true;
                break;
            }
        }
        if (!replaced)
        {
            var repo = new RepoConfig();
            repo.Name = name;
            repo.Url = url;
            repo.Fingerprint = fingerprint;
            repos.Add(repo);
        }
        SaveRepos(repos);
    }

    /// <summary>Removes a repository entry; false when the name is unknown.</summary>
    public static bool RemoveRepo(string name)
    {
        List<RepoConfig> repos = LoadRepos();
        var kept = new List<RepoConfig>();
        bool removed = false;
        for (int i = 0; i < repos.Count; i++)
        {
            if (repos[i].Name == name)
                removed = true;
            else
                kept.Add(repos[i]);
        }
        if (removed)
            SaveRepos(kept);
        return removed;
    }

    /// <summary>
    /// Loads every key in trusted-keys/; accepts raw 32-byte files and
    /// 64-hex-character text files, skipping anything else.
    /// </summary>
    public static List<TrustedKey> LoadTrustedKeys()
    {
        var keys = new List<TrustedKey>();
        if (!Directory.Exists(NpkgPaths.TrustedKeys))
            return keys;

        string[] files = Directory.GetFiles(NpkgPaths.TrustedKeys);
        for (int i = 0; i < files.Length; i++)
        {
            string file = files[i];
            byte[] raw;
            try { raw = File.ReadAllBytes(file); } catch (Exception) { continue; }

            byte[] pub = null;
            if (raw.Length == Ed25519.PublicKeySize)
            {
                pub = raw;
            }
            else
            {
                string text = Str.TrimAscii(AsciiOf(raw));
                if (text.Length == Ed25519.PublicKeySize * 2 && Str.IsHex(text))
                {
                    try { pub = TextConv.HexDecode(text); } catch (Exception) { pub = null; }
                }
            }
            if (pub == null || pub.Length != Ed25519.PublicKeySize)
                continue;

            var key = new TrustedKey();
            key.Name = Str.LastSegment(file);
            if (key.Name.Length > 4 && Str.EqualIgnoreCase(key.Name.Substring(key.Name.Length - 4), ".pub"))
                key.Name = key.Name.Substring(0, key.Name.Length - 4);
            key.PublicKey = pub;
            key.Fingerprint = NpkgPackage.Fingerprint(pub);
            keys.Add(key);
        }
        return keys;
    }

    /// <summary>True when the fingerprint belongs to a trusted key.</summary>
    public static bool IsTrusted(string signerFingerprint)
    {
        if (signerFingerprint == null || signerFingerprint.Length == 0)
            return false;
        List<TrustedKey> keys = LoadTrustedKeys();
        for (int i = 0; i < keys.Count; i++)
        {
            if (Str.EqualIgnoreCase(keys[i].Fingerprint, signerFingerprint))
                return true;
        }
        return false;
    }

    /// <summary>Writes (or overwrites) a trusted key file as 64-hex text.</summary>
    public static void WriteKeyFile(string repoName, byte[] publicKey)
    {
        NpkgPaths.EnsureDirChain(NpkgPaths.TrustedKeys);
        File.WriteAllText(NpkgPaths.TrustedKeys + "/" + repoName + ".pub",
            TextConv.HexEncode(publicKey) + "\n");
    }

    /// <summary>Deletes a trusted key file; false when it did not exist.</summary>
    public static bool RemoveKeyFile(string repoName)
    {
        string path = NpkgPaths.TrustedKeys + "/" + repoName + ".pub";
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    private static string AsciiOf(byte[] bytes)
    {
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
            chars[i] = bytes[i] < 128 ? (char)bytes[i] : '?';
        return new string(chars);
    }
}
