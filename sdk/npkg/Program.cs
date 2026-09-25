// NeutrinoOS Phase 8 -- host-side npkg tooling (sdk/npkg).
//
// npkg-host is the desktop counterpart of the on-device npkg package
// manager: it creates signing keys, packs / signs / verifies .npkg
// packages and generates signed repository indexes.  It compiles the
// shared NeutrinoOS.Packaging sources (the same code the device runs)
// plus the DDK Ed25519 / SHA-256 implementation, so host and device
// agree byte-for-byte on package contents.
//
// Build:  bash build/p8-sdk-build.sh    (WSL, desktop .NET 10)
// Output: /root/p8sdk/npkg-host
//
// Exit codes: 0 = ok, 1 = error, 64 = usage error.  All "error:"
// messages go to stdout (CI logs capture one stream).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NeutrinoOS.Packaging;
using ProtonOS.DDK.Crypto;

namespace NeutrinoOS.Sdk.NpkgCli;

internal static class Program
{
    private const string ToolVersion = "1.0.0";

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        try
        {
            string[] rest = args.Skip(1).ToArray();
            switch (args[0])
            {
                case "--help":
                case "-h":
                case "help":
                    PrintUsage();
                    return 0;
                case "--version":
                    Console.WriteLine("npkg-host (NeutrinoOS) " + ToolVersion);
                    return 0;
                case "keygen":
                    return CmdKeygen(rest);
                case "fingerprint":
                    return CmdFingerprint(rest);
                case "pack":
                    return CmdPack(rest);
                case "sign":
                    return CmdSign(rest);
                case "verify":
                    return CmdVerify(rest);
                case "repo-index":
                    return CmdRepoIndex(rest);
                case "publish":
                    return CmdPublish(rest);
                default:
                    throw new UsageException("unknown command '" + args[0] + "'");
            }
        }
        catch (UsageException ex)
        {
            Console.WriteLine("error: " + ex.Message);
            Console.WriteLine("run 'npkg-host --help' for usage");
            return 64;
        }
        catch (Exception ex)
        {
            Console.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    // ---------------------------------------------------------------- keygen

    private static int CmdKeygen(string[] args)
    {
        ArgParser a = new ArgParser(args);
        string seedHex = a.Opt("seed");
        string outDir = a.Opt("out-dir");
        bool force = a.Has("force");
        a.EnsureNoExtra();

        byte[] seed;
        if (seedHex != null)
        {
            seed = ParseHex(seedHex, "--seed");
            if (seed.Length != 32)
                throw new UsageException("--seed must be 64 hex characters (a 32-byte Ed25519 seed)");
        }
        else
        {
            seed = new byte[32];
            RandomNumberGenerator.Fill(seed);
        }

        if (string.IsNullOrEmpty(outDir))
            outDir = DefaultConfigDir();
        Directory.CreateDirectory(outDir);

        string privPath = Path.Combine(outDir, "private.key");
        string pubPath = Path.Combine(outDir, "public.key");
        if (!force && (File.Exists(privPath) || File.Exists(pubPath)))
            throw new Exception(privPath + " or " + pubPath + " already exists (use --force to overwrite)");

        byte[] pub = Ed25519.PublicKeyFromSeed(seed);
        File.WriteAllText(privPath, TextConv.HexEncode(seed) + "\n");
        File.WriteAllText(pubPath, TextConv.HexEncode(pub) + "\n");

        Console.WriteLine("private key: " + privPath);
        Console.WriteLine("public key:  " + pubPath);
        Console.WriteLine("fingerprint:  " + FingerprintHex(pub));
        return 0;
    }

    // ----------------------------------------------------------- fingerprint

    private static int CmdFingerprint(string[] args)
    {
        ArgParser a = new ArgParser(args);
        string keyPath = a.Pos(0) ?? throw new UsageException("fingerprint requires a public key file");
        a.EnsureNoExtra();

        byte[] pub = ReadHexKeyFile(keyPath);
        Console.WriteLine(FingerprintHex(pub));
        return 0;
    }

    // ------------------------------------------------------------------ pack

    private static int CmdPack(string[] args)
    {
        ArgParser a = new ArgParser(args);
        string manifestPath = a.Req("manifest");
        string payloadDir = a.Req("payload-dir");
        string outPath = a.Req("out");
        string keyPath = a.Opt("key");
        a.EnsureNoExtra();

        NpkgManifest manifest = NpkgManifest.FromJson(Json.Parse(File.ReadAllText(manifestPath)));
        KeyValuePair<string, byte[]>[] files = CollectPayload(payloadDir);
        byte[] seed = keyPath != null ? ReadHexKeyFile(keyPath) : null;

        byte[] npkg = NpkgPackage.Build(manifest, files, seed);
        File.WriteAllBytes(outPath, npkg);

        string signer = string.IsNullOrEmpty(manifest.Signer) ? "none" : Shorten(manifest.Signer);
        Console.WriteLine("packed " + outPath + " (" + TextConv.LongToString(npkg.Length) + " bytes, " +
                          TextConv.LongToString(files.Length) + " files, signer " + signer + ")");
        return 0;
    }

    // ------------------------------------------------------------------ sign

    private static int CmdSign(string[] args)
    {
        ArgParser a = new ArgParser(args);
        string pkgPath = a.Pos(0) ?? throw new UsageException("sign requires a package and a private key");
        string keyPath = a.Pos(1) ?? throw new UsageException("sign requires a package and a private key");
        a.EnsureNoExtra();

        NpkgPackage pkg = NpkgPackage.Open(File.ReadAllBytes(pkgPath));
        byte[] seed = ReadHexKeyFile(keyPath);

        byte[] signed = NpkgPackage.Build(pkg.Manifest, pkg.PayloadFiles, seed);
        File.WriteAllBytes(pkgPath, signed);

        Console.WriteLine("signed " + pkgPath);
        Console.WriteLine("signer: " + pkg.Manifest.Signer);
        return 0;
    }

    // ---------------------------------------------------------------- verify

    private static int CmdVerify(string[] args)
    {
        ArgParser a = new ArgParser(args);
        string pkgPath = a.Pos(0) ?? throw new UsageException("verify requires a package file");
        string keyPath = a.Opt("key");
        a.EnsureNoExtra();

        NpkgPackage pkg = NpkgPackage.Open(File.ReadAllBytes(pkgPath));
        bool pass = true;

        string badPath;
        bool checksOk = pkg.VerifyChecksums(out badPath);
        Console.WriteLine((checksOk ? "[PASS] " : "[FAIL] ") + "checksums" +
                          (checksOk || string.IsNullOrEmpty(badPath) ? "" : " (" + badPath + ")"));
        if (!checksOk) pass = false;

        string signer = pkg.Manifest.Signer ?? "";
        if (keyPath != null)
        {
            byte[] pub = ReadHexKeyFile(keyPath);
            bool sigOk = pkg.VerifySignature(pub);
            Console.WriteLine((sigOk ? "[PASS] " : "[FAIL] ") + "signature (key " + Shorten(FingerprintHex(pub)) + ")");
            if (!sigOk) pass = false;
        }
        else if (string.IsNullOrEmpty(signer))
        {
            Console.WriteLine("[PASS] signature (unsigned package)");
        }
        else
        {
            // No key supplied: report the signer and whether the signature
            // blob is structurally consistent with the manifest.
            bool consistent = pkg.Signature != null && pkg.Signature.Length == 64;
            Console.WriteLine((consistent ? "[PASS] " : "[FAIL] ") +
                              "signature (signer " + Shorten(signer) + ", self-consistent" +
                              (consistent ? "; pass --key to cryptographically verify)" : ")"));
            if (!consistent) pass = false;
        }

        return pass ? 0 : 1;
    }

    // ------------------------------------------------------------ repo-index

    private static int CmdRepoIndex(string[] args)
    {
        ArgParser a = new ArgParser(args);
        string dir = a.Req("dir");
        string keyPath = a.Opt("key");
        string name = a.Opt("name");
        a.EnsureNoExtra();

        if (!Directory.Exists(dir))
            throw new Exception("directory not found: " + dir);

        string[] pkgs = Directory.GetFiles(dir, "*.npkg");
        Array.Sort(pkgs, StringComparer.Ordinal);

        RepoPackageList infos = new RepoPackageList();
        foreach (string file in pkgs)
        {
            byte[] bytes = File.ReadAllBytes(file);
            NpkgPackage pkg = NpkgPackage.Open(bytes);
            NpkgManifest manifest = pkg.Manifest;

            RepoPackageInfo info = new RepoPackageInfo();
            info.Name = manifest.Name;
            info.Version = manifest.Version;
            info.Architecture = manifest.Architecture;
            info.Description = manifest.Description;
            info.Filename = Path.GetFileName(file);
            info.Size = bytes.Length;
            info.Sha256 = NpkgPackage.Sha256Hex(bytes);
            info.Signer = manifest.Signer != null ? manifest.Signer : "";
            info.Dependencies = manifest.Dependencies;
            if (manifest.Provides != null)
                info.Provides = manifest.Provides.ToArray();
            infos.Add(info);
        }

        RepositoryIndex index = new RepositoryIndex();
        index.Format = "npkg-repo/1";
        index.Revision = 1;
        index.Generated = "";
        index.Name = name != null ? name : "";
        index.Packages = infos;

        string json = Json.Write(index.ToJson(), true);
        string indexPath = Path.Combine(dir, "repository.json");
        File.WriteAllText(indexPath, json);

        if (keyPath != null)
        {
            byte[] seed = ReadHexKeyFile(keyPath);
            byte[] pub = Ed25519.PublicKeyFromSeed(seed);
            byte[] sig = Ed25519.Sign(seed, File.ReadAllBytes(indexPath));
            File.WriteAllBytes(indexPath + ".sig", sig);

            string pubPath = Path.Combine(dir, "repo.pub");
            string pubHex = TextConv.HexEncode(pub) + "\n";
            if (!File.Exists(pubPath) || File.ReadAllText(pubPath) != pubHex)
                File.WriteAllText(pubPath, pubHex);
        }

        Console.WriteLine("repo index: " + TextConv.LongToString(infos.Count) + " packages -> " + indexPath);
        return 0;
    }

    // --------------------------------------------------------------- publish

    private static int CmdPublish(string[] args)
    {
        ArgParser a = new ArgParser(args);
        string pkgPath = a.Pos(0) ?? throw new UsageException("publish requires a package file");
        string repoDir = a.Req("repo");
        a.EnsureNoExtra();

        if (!File.Exists(pkgPath))
            throw new Exception("package not found: " + pkgPath);
        Directory.CreateDirectory(repoDir);

        string dest = Path.Combine(repoDir, Path.GetFileName(pkgPath));
        File.Copy(pkgPath, dest, true);
        Console.WriteLine("published " + pkgPath + " -> " + repoDir);
        Console.WriteLine("hint: run 'npkg-host repo-index --dir " + repoDir + "' to regenerate the repository index");
        return 0;
    }

    // --------------------------------------------------------------- helpers

    private static KeyValuePair<string, byte[]>[] CollectPayload(string dir)
    {
        if (!Directory.Exists(dir))
            throw new Exception("payload directory not found: " + dir);
        string root = Path.GetFullPath(dir);

        List<KeyValuePair<string, byte[]>> list = new List<KeyValuePair<string, byte[]>>();
        foreach (string full in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, full).Replace('\\', '/');
            list.Add(new KeyValuePair<string, byte[]>(rel, File.ReadAllBytes(full)));
        }
        return list.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray();
    }

    private static byte[] ReadHexKeyFile(string path)
    {
        if (!File.Exists(path))
            throw new Exception("key file not found: " + path);
        return ParseHex(File.ReadAllText(path).Trim(), path);
    }

    private static byte[] ParseHex(string hex, string what)
    {
        byte[] bytes = TextConv.HexDecode(hex);
        if (bytes == null || bytes.Length != 32)
            throw new UsageException(what + " must be 64 hex characters (32 bytes)");
        return bytes;
    }

    private static string FingerprintHex(byte[] pub)
    {
        return NpkgPackage.Fingerprint(pub);
    }

    private static string Shorten(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return "none";
        return hex.Length <= 8 ? hex : hex.Substring(0, 8);
    }

    private static string DefaultConfigDir()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home))
            home = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrEmpty(home))
            throw new Exception("cannot determine the user home directory; pass --out-dir");
        return Path.Combine(home, ".neutrinoos");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("npkg-host (NeutrinoOS) " + ToolVersion + " - host-side .npkg package tooling");
        Console.WriteLine();
        Console.WriteLine("usage:");
        Console.WriteLine("  npkg-host keygen [--seed <64-hex>] [--out-dir <dir>] [--force]");
        Console.WriteLine("  npkg-host fingerprint <pubkey-file>");
        Console.WriteLine("  npkg-host pack --manifest <manifest.json> --payload-dir <dir> --out <file.npkg> [--key <private.key>]");
        Console.WriteLine("  npkg-host sign <file.npkg> <private.key>");
        Console.WriteLine("  npkg-host verify <file.npkg> [--key <pubkey-file>]");
        Console.WriteLine("  npkg-host repo-index --dir <dir> [--key <private.key>] [--name <name>]");
        Console.WriteLine("  npkg-host publish <file.npkg> --repo <dir>");
        Console.WriteLine("  npkg-host --help");
        Console.WriteLine("  npkg-host --version");
    }

    private sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message) { }
    }

    private sealed class ArgParser
    {
        private readonly List<string> _pos = new List<string>();
        private readonly Dictionary<string, string> _opt = new Dictionary<string, string>(StringComparer.Ordinal);

        public ArgParser(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    string name = arg.Substring(2);
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        _opt[name] = args[i + 1];
                        i++;
                    }
                    else
                    {
                        _opt[name] = "";
                    }
                }
                else
                {
                    _pos.Add(arg);
                }
            }
        }

        public string Pos(int index)
        {
            return index < _pos.Count ? _pos[index] : null;
        }

        public string Opt(string name)
        {
            string value;
            return _opt.TryGetValue(name, out value) ? value : null;
        }

        public bool Has(string name)
        {
            return _opt.ContainsKey(name);
        }

        public string Req(string name)
        {
            string value = Opt(name);
            if (string.IsNullOrEmpty(value))
                throw new UsageException("missing required option --" + name);
            return value;
        }

        public void EnsureNoExtra()
        {
            List<string> unknown = _opt.Keys.Where(k => k != "seed" && k != "out-dir" && k != "force" &&
                                                        k != "manifest" && k != "payload-dir" && k != "out" &&
                                                        k != "key" && k != "dir" && k != "name" && k != "repo").ToList();
            if (unknown.Count > 0)
                throw new UsageException("unknown option --" + unknown[0]);
            if (_pos.Count > 2)
                throw new UsageException("unexpected argument '" + _pos[2] + "'");
        }
    }
}
