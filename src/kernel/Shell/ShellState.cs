// NeutrinoOS kernel - Phase 5 shell: shared shell state
//
// Holds the pieces of shell state that the lexer, executor and
// built-ins all touch: last exit code, shell PID, aliases, the exit
// request flag, and command resolution through $PATH. Environment
// variables themselves live in korlib's System.Environment (shared with
// JIT-compiled applications).

using System;
using System.IO;

namespace ProtonOS.Shell;

/// <summary>
/// Global shell state (single interactive shell instance per console;
/// see docs/PHASE5-DESIGN.md for the single-console design note).
/// </summary>
public static class ShellState
{
    /// <summary>The exit code of the last executed pipeline ($?).</summary>
    public static int LastExit;

    /// <summary>The shell's PID ($$); initialized to the kernel thread id at startup.</summary>
    public static int ShellPid;

    /// <summary>Set by the exit built-in; the REPL loop stops when true.</summary>
    public static bool ExitRequested;

    /// <summary>Exit code for the exit built-in.</summary>
    public static int ExitCode;

    // ==================== Environment helpers ====================

    /// <summary>Returns an environment variable value, or "" when unset (for $VAR expansion).</summary>
    public static string GetVar(string name)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        return v ?? "";
    }

    /// <summary>Sets an environment variable (null/"" removes it).</summary>
    public static void SetVar(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value);
    }

    // ==================== Aliases ====================

    private const int MaxAliases = 16;
    private static readonly string?[] _aliasNames = new string?[MaxAliases];
    private static readonly string?[] _aliasValues = new string?[MaxAliases];
    private static int _aliasCount;

    /// <summary>
    /// Adds or replaces an alias. Returns false when the alias table is
    /// full (16 entries; the shell reports this to the user).
    /// </summary>
    public static bool SetAlias(string name, string value)
    {
        for (int i = 0; i < _aliasCount; i++)
        {
            if (_aliasNames[i] == name)
            {
                _aliasValues[i] = value;
                return true;
            }
        }
        if (_aliasCount >= MaxAliases)
            return false;
        _aliasNames[_aliasCount] = name;
        _aliasValues[_aliasCount] = value;
        _aliasCount++;
        return true;
    }

    /// <summary>Returns the alias value for <paramref name="name"/>, or null.</summary>
    public static string? GetAlias(string name)
    {
        for (int i = 0; i < _aliasCount; i++)
        {
            if (_aliasNames[i] == name)
                return _aliasValues[i];
        }
        return null;
    }

    /// <summary>Removes an alias; returns false when it did not exist.</summary>
    public static bool RemoveAlias(string name)
    {
        for (int i = 0; i < _aliasCount; i++)
        {
            if (_aliasNames[i] == name)
            {
                for (int k = i; k < _aliasCount - 1; k++)
                {
                    _aliasNames[k] = _aliasNames[k + 1];
                    _aliasValues[k] = _aliasValues[k + 1];
                }
                _aliasCount--;
                _aliasNames[_aliasCount] = null;
                _aliasValues[_aliasCount] = null;
                return true;
            }
        }
        return false;
    }

    /// <summary>Alias count (for the alias built-in listing).</summary>
    public static int AliasCount => _aliasCount;

    /// <summary>Returns alias name/value by index (0..AliasCount-1).</summary>
    public static void GetAliasAt(int index, out string? name, out string? value)
    {
        name = _aliasNames[index];
        value = _aliasValues[index];
    }

    // ==================== Command resolution ====================

    private static bool EndsWithDll(string name)
    {
        if (name.Length < 4)
            return false;
        string tail = name.Substring(name.Length - 4);
        return tail == ".dll" || tail == ".DLL";
    }

    /// <summary>
    /// Resolves a command name to a .dll path:
    ///   - names containing '/' or ending in .dll are used as explicit
    ///     paths (relative paths resolve against the current directory;
    ///     a missing .dll extension is tried as well),
    ///   - other names are searched in $PATH (default "/bin:/apps"),
    ///     trying &lt;dir&gt;/&lt;name&gt;.dll.
    /// Returns null when nothing matches.
    /// </summary>
    public static string? FindExecutable(string name)
    {
        if (name.IndexOf('/') >= 0 || EndsWithDll(name))
        {
            string path = Path.GetFullPath(name);
            if (File.Exists(path))
                return path;
            if (!EndsWithDll(path))
            {
                string withExt = path + ".dll";
                if (File.Exists(withExt))
                    return withExt;
            }
            return null;
        }

        string pathVar = GetVar("PATH");
        if (string.IsNullOrEmpty(pathVar))
            pathVar = "/bin:/apps";

        int start = 0;
        while (start <= pathVar.Length)
        {
            int sep = pathVar.IndexOf(':', start);
            string dir = sep < 0 ? pathVar.Substring(start) : pathVar.Substring(start, sep - start);
            if (dir.Length > 0)
            {
                string candidate = dir;
                if (candidate[candidate.Length - 1] != '/')
                    candidate += "/";
                candidate += name;
                candidate += ".dll";
                if (File.Exists(candidate))
                    return candidate;
            }
            if (sep < 0)
                break;
            start = sep + 1;
        }

        return null;
    }
}
