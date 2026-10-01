// NeutrinoOS utility: startup - manage the app that runs at startup.
//
// usage: startup [--list] | [--set <command...>] | [--clear]
//
// The startup app is a shell command line executed by the shell during
// boot (after the profile scripts, before the first prompt). It is stored
// as the "startup.app=" key of /etc/boot.params, which ShellInit applies;
// use `run <path.dll>` as the command to run an assembly, e.g.:
//   startup --set run /apps/hello.dll

using System;
using System.IO;
using System.Text;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Startup;

/// <summary>The startup utility (see file header).</summary>
public static class Program
{
    private const string ParamsFile = "/etc/boot.params";
    private const string StartupKey = "startup.app";

    /// <summary>Entry point.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;

        if (args.Length == 0 || args[0] == "--list" || args[0] == "-l")
            return List();

        if (args[0] == "--help" || args[0] == "-h")
        {
            return Util.Help(
                "usage: startup [--list | --set <command...> | --clear]",
                "  Manages the app that runs at startup: a shell command line the",
                "  shell executes during boot (stored as startup.app= in",
                "  /etc/boot.params; use `run <path.dll>` to run an assembly).",
                "  --list          show the defined startup app (default action)",
                "  --set <cmd...>  define it, e.g. startup --set run /apps/hello.dll",
                "  --clear         remove the definition");
        }

        if (args[0] == "--set")
        {
            if (args.Length < 2)
                return Util.Fail("startup", "--set needs a command (e.g. startup --set run /apps/hello.dll)");

            var sb = new StringBuilder();
            for (int i = 1; i < args.Length; i++)
            {
                if (i > 1)
                    sb.Append(' ');
                sb.Append(args[i]);
            }
            string cmd = sb.ToString();

            if (!WriteSetting(cmd))
                return Util.Fail("startup", "could not write " + ParamsFile);
            Console.WriteLine("startup: set: " + cmd);
            Console.WriteLine("  (runs at the next boot; `startup --list` shows it)");
            return 0;
        }

        if (args[0] == "--clear")
        {
            if (!WriteSetting(null))
                return Util.Fail("startup", "could not write " + ParamsFile);
            Console.WriteLine("startup: cleared (no app runs at startup)");
            return 0;
        }

        return Util.Fail("startup", "unknown option: " + args[0] + " (see `startup --help`)");
    }

    private static int List()
    {
        string cmd;
        if (!ReadSetting(out cmd))
            return Util.Fail("startup", "could not read " + ParamsFile);
        if (cmd == null)
            Console.WriteLine("startup: no startup app defined");
        else
            Console.WriteLine("startup app: " + cmd);
        return 0;
    }

    /// <summary>
    /// Reads the startup.app value from /etc/boot.params. Returns false on
    /// IO error; <paramref name="cmd"/> is null when the key is absent.
    /// Deliberately allocation-light char scanning (korlib/JIT-safe: no
    /// String.Split/IndexOf(char,int)/Trim).
    /// </summary>
    private static bool ReadSetting(out string cmd)
    {
        cmd = null;
        if (!File.Exists(ParamsFile))
            return true;

        string text;
        try
        {
            text = File.ReadAllText(ParamsFile);
        }
        catch (Exception)
        {
            return false;
        }

        int i = 0;
        while (i < text.Length)
        {
            int end = i;
            while (end < text.Length && text[end] != '\n' && text[end] != '\r')
                end++;

            int s = i;
            int e = end;
            while (s < e && (text[s] == ' ' || text[s] == '\t'))
                s++;
            while (e > s && (text[e - 1] == ' ' || text[e - 1] == '\t'))
                e--;

            if (e > s && text[s] != '#')
            {
                int eq = -1;
                for (int j = s; j < e; j++)
                {
                    if (text[j] == '=')
                    {
                        eq = j;
                        break;
                    }
                }
                int ke = eq;
                while (ke > s && (text[ke - 1] == ' ' || text[ke - 1] == '\t'))
                    ke--;
                if (eq > s && KeyMatches(text, s, ke))
                {
                    int vs = eq + 1;
                    while (vs < e && (text[vs] == ' ' || text[vs] == '\t'))
                        vs++;
                    string value = text.Substring(vs, e - vs);
                    if (value.Length > 0)
                        cmd = value;
                }
            }

            i = end;
            while (i < text.Length && (text[i] == '\r' || text[i] == '\n'))
                i++;
        }
        return true;
    }

    /// <summary>
    /// Rewrites /etc/boot.params with the startup.app line replaced by
    /// <paramref name="cmd"/> (null removes it), preserving every other
    /// line as-is. Creates the file when missing.
    /// </summary>
    private static bool WriteSetting(string cmd)
    {
        string text = "";
        if (File.Exists(ParamsFile))
        {
            try
            {
                text = File.ReadAllText(ParamsFile);
            }
            catch (Exception)
            {
                return false;
            }
        }

        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            int end = i;
            while (end < text.Length && text[end] != '\n' && text[end] != '\r')
                end++;

            bool isStartup = false;
            int s = i;
            int e = end;
            while (s < e && (text[s] == ' ' || text[s] == '\t'))
                s++;
            while (e > s && (text[e - 1] == ' ' || text[e - 1] == '\t'))
                e--;
            if (e > s && text[s] != '#')
            {
                int eq = -1;
                for (int j = s; j < e; j++)
                {
                    if (text[j] == '=')
                    {
                        eq = j;
                        break;
                    }
                }
                int ke = eq;
                while (ke > s && (text[ke - 1] == ' ' || text[ke - 1] == '\t'))
                    ke--;
                if (eq > s && KeyMatches(text, s, ke))
                    isStartup = true;
            }

            if (!isStartup)
            {
                for (int j = i; j < end; j++)
                    sb.Append(text[j]);
                sb.Append('\n');
            }

            i = end;
            while (i < text.Length && (text[i] == '\r' || text[i] == '\n'))
                i++;
        }

        if (cmd != null)
        {
            sb.Append(StartupKey);
            sb.Append('=');
            sb.Append(cmd);
            sb.Append('\n');
        }

        try
        {
            File.WriteAllText(ParamsFile, sb.ToString());
        }
        catch (Exception)
        {
            return false;
        }
        return true;
    }

    /// <summary>True when text[start..end) equals "startup.app" (no alloc).</summary>
    private static bool KeyMatches(string text, int start, int end)
    {
        if (end - start != StartupKey.Length)
            return false;
        for (int i = 0; i < StartupKey.Length; i++)
        {
            if (text[start + i] != StartupKey[i])
                return false;
        }
        return true;
    }
}
