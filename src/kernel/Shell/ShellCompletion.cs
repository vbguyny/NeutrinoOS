// NeutrinoOS kernel - Phase 5 shell: tab completion
//
// The deferred TAB handler installed into the line discipline
// (LineDiscipline.TabCompleter). Runs in thread context from the read
// loop - never in the UART ISR - so it may read the boot FAT volume.
//
//   - First word of the line: complete command names - the shell
//     built-ins plus every <name>.dll in the $PATH directories
//     (default /bin:/apps; the .dll suffix is stripped, matching how
//     commands are typed).
//   - Later words: complete file/directory paths. The last '/'
//     separates the directory part (kept verbatim) from the name part
//     (matched as a prefix against the directory's entries).
//
// Return contract (see LineDiscipline.TabCompleter):
//   >= 0  new edit-buffer length (the caller echoes the appended chars),
//   -1    no change,
//   -2    candidates were printed with EchoCompletionLine; the caller
//         redraws the prompt + current line.

using System;
using System.IO;
using System.Runtime.InteropServices;
using ProtonOS.Platform;

namespace ProtonOS.Shell;

/// <summary>Tab completion for commands and paths (see file header).</summary>
public static class ShellCompletion
{
    /// <summary>Maximum candidates printed before the list is truncated.</summary>
    public const int MaxCandidates = 32;

    /// <summary>Command names that are shell built-ins (completed for the first word).</summary>
    private static readonly string[] _builtins =
    {
        "cd", "pwd", "exit", "logout", "export", "unset", "history",
        "alias", "unalias", "source", "jobs", "fg", "bg", "help",
        "run", "true", "false", "gc"
    };

    /// <summary>
    /// Completion entry point (line-discipline callback; returns -1/
    /// new length/-2 as in the file header).
    /// </summary>
    [UnmanagedCallersOnly]
    public static unsafe int TryComplete(char* buffer, int length)
    {
        // Materialize the edit buffer without the char* string ctor
        // (which is not reliable through the runtime bridge).
        var lineBuilder = new System.Text.StringBuilder();
        for (int i = 0; i < length; i++)
            lineBuilder.Append(buffer[i]);
        string line = lineBuilder.ToString();

        // Find the last whitespace: everything after it is the token
        // being completed; if the line ends with whitespace the token is
        // empty (complete ahead of a new word).
        int tokenStart = 0;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == ' ' || line[i] == '\t')
                tokenStart = i + 1;
        }

        bool firstWord = tokenStart == 0;

        string prefix = line.Substring(tokenStart);
        string dirPart;
        string namePart;
        if (firstWord)
        {
            dirPart = "";
            namePart = prefix;
        }
        else
        {
            int slash = prefix.LastIndexOf('/');
            if (slash >= 0)
            {
                dirPart = prefix.Substring(0, slash + 1);
                namePart = prefix.Substring(slash + 1);
            }
            else
            {
                dirPart = "";
                namePart = prefix;
            }
        }

        // Collect candidates.
        var candidates = new System.Collections.Generic.List<string>();

        if (firstWord)
        {
            // Built-ins first.
            for (int i = 0; i < _builtins.Length; i++)
            {
                if (StartsWithIgnoreCase(_builtins[i], namePart))
                    AddUnique(candidates, _builtins[i]);
            }

            // <name>.dll in each $PATH directory. FAT returns names in
            // upper case, so match case-insensitively and offer the
            // lower-case command name (executable lookup accepts either).
            string pathVar = ShellState.GetVar("PATH");
            if (string.IsNullOrEmpty(pathVar))
                pathVar = "/bin:/apps";
            string[] dirs = SplitList(pathVar, ':');
            for (int d = 0; d < dirs.Length; d++)
            {
                string dir = dirs[d];
                if (dir.Length == 0 || !Directory.Exists(dir))
                    continue;

                string[] files;
                try
                {
                    files = Directory.GetFiles(dir);
                }
                catch (Exception)
                {
                    continue;
                }

                for (int f = 0; f < files.Length; f++)
                {
                    string baseName = Path.GetFileName(files[f]);
                    if (!EndsWithIgnoreCase(baseName, ".dll"))
                        continue;
                    string command = baseName.Substring(0, baseName.Length - 4);
                    if (StartsWithIgnoreCase(command, namePart))
                        AddUnique(candidates, ToLowerString(command));
                }
            }
        }
        else
        {
            // Path completion in the directory named by dirPart.
            string dirResolved = dirPart.Length == 0 ? "." : dirPart;
            if (!Directory.Exists(dirResolved))
                return -1;

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(dirResolved);
            }
            catch (Exception)
            {
                return -1;
            }

            for (int i = 0; i < entries.Length; i++)
            {
                string baseName = Path.GetFileName(entries[i]);
                if (StartsWithIgnoreCase(baseName, namePart))
                {
                    bool isDir = Directory.Exists(entries[i]);
                    AddUnique(candidates, dirPart + baseName + (isDir ? "/" : ""));
                }
            }
        }

        if (candidates.Count == 0)
            return -1;

        // Order the list (insertion sort keeps determinism on FAT order).
        string[] ordered = candidates.ToArray();
        for (int i = 1; i < ordered.Length; i++)
        {
            string key = ordered[i];
            int j = i - 1;
            while (j >= 0 && Compare(ordered[j], key) > 0)
            {
                ordered[j + 1] = ordered[j];
                j--;
            }
            ordered[j + 1] = key;
        }

        // Single candidate: complete it fully (with a trailing space for
        // a command word, like a traditional shell).
        if (ordered.Length == 1)
        {
            int newLength = tokenStart + ordered[0].Length;
            if (firstWord)
                newLength++;
            if (newLength >= 255)
                return -1;
            for (int i = 0; i < ordered[0].Length; i++)
                buffer[tokenStart + i] = ordered[0][i];
            if (firstWord)
                buffer[newLength - 1] = ' ';
            return newLength;
        }

        // Several candidates: extend to the longest common prefix when it
        // goes beyond what the user typed; otherwise list them.
        int common = CommonPrefixLength(ordered);
        if (common > namePart.Length)
        {
            int newLength = tokenStart + common;
            if (newLength >= 255)
                return -1;
            for (int i = 0; i < common; i++)
                buffer[tokenStart + i] = ordered[0][i];
            return newLength;
        }

        // Print the candidate list (the caller redraws the line).
        LineDiscipline.EchoCompletionLine("");
        int shown = ordered.Length > MaxCandidates ? MaxCandidates : ordered.Length;
        for (int i = 0; i < shown; i++)
            LineDiscipline.EchoCompletionLine(ordered[i]);
        if (shown < ordered.Length)
        {
            var more = new System.Text.StringBuilder();
            more.Append("... (");
            more.Append(ordered.Length - shown);
            more.Append(" more)");
            LineDiscipline.EchoCompletionLine(more.ToString());
        }
        return -2;
    }

    private static void AddUnique(System.Collections.Generic.List<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
                return;
        }
        list.Add(value);
    }

    private static int CommonPrefixLength(string[] items)
    {
        if (items.Length == 0)
            return 0;
        string first = items[0];
        int len = first.Length;
        for (int i = 1; i < items.Length; i++)
        {
            string s = items[i];
            int n = s.Length < len ? s.Length : len;
            int j = 0;
            while (j < n && first[j] == s[j])
                j++;
            len = j;
        }
        return len;
    }

    private static bool StartsWith(string text, string prefix)
    {
        if (prefix.Length > text.Length)
            return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            if (text[i] != prefix[i])
                return false;
        }
        return true;
    }

    private static bool StartsWithIgnoreCase(string text, string prefix)
    {
        if (prefix.Length > text.Length)
            return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            if (ToLowerChar(text[i]) != ToLowerChar(prefix[i]))
                return false;
        }
        return true;
    }

    private static char ToLowerChar(char c)
    {
        if (c >= 'A' && c <= 'Z')
            return (char)(c + 32);
        return c;
    }

    private static string ToLowerString(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
            sb.Append(ToLowerChar(s[i]));
        return sb.ToString();
    }

    private static bool EndsWithIgnoreCase(string text, string suffix)
    {
        if (suffix.Length > text.Length)
            return false;
        int offset = text.Length - suffix.Length;
        for (int i = 0; i < suffix.Length; i++)
        {
            char a = text[offset + i];
            char b = suffix[i];
            if (a >= 'A' && a <= 'Z')
                a = (char)(a + 32);
            if (b >= 'A' && b <= 'Z')
                b = (char)(b + 32);
            if (a != b)
                return false;
        }
        return true;
    }

    private static int Compare(string a, string b)
    {
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

    private static string[] SplitList(string text, char separator)
    {
        var parts = new System.Collections.Generic.List<string>();
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == separator)
            {
                parts.Add(text.Substring(start, i - start));
                start = i + 1;
            }
        }
        return parts.ToArray();
    }
}
