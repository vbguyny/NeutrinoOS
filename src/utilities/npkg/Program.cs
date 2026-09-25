// NeutrinoOS Phase 8 - npkg: package manager utility
//
// npkg installs, verifies, upgrades and removes .npkg packages from
// repositories serving a repository.json index (+ Ed25519 signature and
// SHA-256 package hashes). Packages are ZIP containers holding a
// manifest, per-file checksums, a signature and the payload described
// in docs/PHASE8; installed utilities land in /bin and are resolved by
// the shell like every other NeutrinoOS tool.
//
// exit codes: 0 ok, 1 command error, 64 usage error.

using System;
using System.Text;

namespace NeutrinoOS.Utility.Npkg;

/// <summary>Entry point + argument dispatch for npkg (see file header).</summary>
public static class Program
{
    /// <summary>Default entry point: dispatch the subcommand, return its exit code.</summary>
    public static int Main(string[] args)
    {
        if (HasVersionFlag(args))
        {
            Console.WriteLine("npkg (NeutrinoOS) 1.0.0");
            ProtonOS.DDK.Util.VersionFlag.Handle(args);
            return 0;
        }
        if (args == null || args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        string command = args[0];
        string[] rest = Tail(args, 1);
        switch (command)
        {
            case "help":
            case "--help":
            case "-h":
                PrintUsage();
                return 0;
            case "list":
                return DispatchList(rest);
            case "search":
                return DispatchSearch(rest);
            case "info":
                return DispatchInfo(rest);
            case "install":
                return DispatchInstall(rest);
            case "remove":
                return DispatchRemove(rest);
            case "upgrade":
                return DispatchUpgrade(rest);
            case "repo":
                return DispatchRepo(rest);
            case "verify":
                return DispatchVerify(rest);
            default:
                Console.Error.WriteLine("neutrinoos: npkg: " + command + ": unknown command");
                Console.Error.WriteLine("usage: npkg <command> [options]  (npkg help for details)");
                return 64;
        }
    }

    private static int DispatchList(string[] args)
    {
        if (args.Length > 0)
            return Usage("list takes no arguments");
        return Commands.List();
    }

    private static int DispatchSearch(string[] args)
    {
        if (args.Length == 0)
            return Usage("search needs a query");
        var sb = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0)
                sb.Append(' ');
            sb.Append(args[i]);
        }
        return Commands.Search(sb.ToString());
    }

    private static int DispatchInfo(string[] args)
    {
        if (args.Length != 1)
            return Usage("info needs exactly one package name");
        return Commands.Info(args[0]);
    }

    private static int DispatchInstall(string[] args)
    {
        string spec = null;
        bool force = false;
        bool allowUntrusted = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--force")
                force = true;
            else if (a == "--allow-untrusted")
                allowUntrusted = true;
            else if (a.Length > 0 && a[0] == '-')
                return Usage("unknown option: " + a);
            else if (spec == null)
                spec = a;
            else
                return Usage("too many arguments");
        }
        if (spec == null)
            return Usage("install needs a package name");
        return Commands.Install(spec, allowUntrusted, force);
    }

    private static int DispatchRemove(string[] args)
    {
        string name = null;
        bool force = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--force")
                force = true;
            else if (a.Length > 0 && a[0] == '-')
                return Usage("unknown option: " + a);
            else if (name == null)
                name = a;
            else
                return Usage("too many arguments");
        }
        if (name == null)
            return Usage("remove needs a package name");
        return Commands.Remove(name, force);
    }

    private static int DispatchUpgrade(string[] args)
    {
        string name = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.Length > 0 && a[0] == '-')
                return Usage("unknown option: " + a);
            if (name == null)
                name = a;
            else
                return Usage("too many arguments");
        }
        return Commands.Upgrade(name);
    }

    private static int DispatchRepo(string[] args)
    {
        if (args.Length == 0)
            return Usage("repo needs a subcommand (add, list, remove)");

        string sub = args[0];
        string[] rest = Tail(args, 1);
        if (sub == "list")
        {
            if (rest.Length > 0)
                return Usage("repo list takes no arguments");
            return Commands.RepoList();
        }
        if (sub == "add")
        {
            string name = null;
            string url = null;
            string fingerprint = null;
            for (int i = 0; i < rest.Length; i++)
            {
                string a = rest[i];
                if (a == "--fingerprint")
                {
                    if (i + 1 >= rest.Length)
                        return Usage("--fingerprint needs a hex value");
                    fingerprint = rest[++i];
                }
                else if (a.Length > 0 && a[0] == '-')
                {
                    return Usage("unknown option: " + a);
                }
                else if (name == null)
                {
                    name = a;
                }
                else if (url == null)
                {
                    url = a;
                }
                else
                {
                    return Usage("too many arguments");
                }
            }
            if (name == null || url == null)
                return Usage("repo add needs a name and a URL");
            return Commands.RepoAdd(name, url, fingerprint);
        }
        if (sub == "remove")
        {
            string name = null;
            bool removeKey = false;
            for (int i = 0; i < rest.Length; i++)
            {
                string a = rest[i];
                if (a == "--key")
                    removeKey = true;
                else if (a.Length > 0 && a[0] == '-')
                    return Usage("unknown option: " + a);
                else if (name == null)
                    name = a;
                else
                    return Usage("too many arguments");
            }
            if (name == null)
                return Usage("repo remove needs a repository name");
            return Commands.RepoRemove(name, removeKey);
        }
        return Usage("unknown repo subcommand: " + sub);
    }

    private static int DispatchVerify(string[] args)
    {
        if (args.Length != 1)
            return Usage("verify needs exactly one .npkg file");
        return Commands.VerifyFile(args[0]);
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine("neutrinoos: npkg: " + message);
        Console.Error.WriteLine("usage: npkg <command> [options]  (npkg help for details)");
        return 64;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("NeutrinoOS package manager (npkg 1.0.0)");
        Console.WriteLine();
        Console.WriteLine("usage: npkg <command> [options]");
        Console.WriteLine();
        Console.WriteLine("commands:");
        Console.WriteLine("  list                                  list installed packages");
        Console.WriteLine("  search <query>                        search all repository indexes");
        Console.WriteLine("  info <name>                           show an installed or repository package");
        Console.WriteLine("  install <name>[@<version>] [options]  install (or reinstall) a package");
        Console.WriteLine("      --force                           replace an installed version on conflict");
        Console.WriteLine("      --allow-untrusted                 install without a trusted signature");
        Console.WriteLine("  remove <name> [--force]               uninstall a package");
        Console.WriteLine("  upgrade [<name>]                      upgrade one or all installed packages");
        Console.WriteLine("  repo add <name> <url> [--fingerprint <hex64>]");
        Console.WriteLine("                                        add/update a repository and trust its key");
        Console.WriteLine("  repo list                             list configured repositories");
        Console.WriteLine("  repo remove <name> [--key]            remove a repository (--key drops the key)");
        Console.WriteLine("  verify <file.npkg>                    verify checksums and signature of a file");
        Console.WriteLine("  help                                  show this text");
        Console.WriteLine();
        Console.WriteLine("state:   /var/lib/npkg/installed.json (journal: /var/lib/npkg/journal.log)");
        Console.WriteLine("config:  /etc/npkg/repos.json, trusted keys in /etc/npkg/trusted-keys/");
        Console.WriteLine("exit:    0 ok, 1 error, 64 usage error");
    }

    private static bool HasVersionFlag(string[] args)
    {
        if (args == null)
            return false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--version" || args[i] == "-V")
                return true;
        }
        return false;
    }

    private static string[] Tail(string[] args, int count)
    {
        if (args == null || args.Length <= count)
            return new string[0];
        var result = new string[args.Length - count];
        for (int i = count; i < args.Length; i++)
            result[i - count] = args[i];
        return result;
    }
}
