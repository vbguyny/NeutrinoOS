// NeutrinoOS Phase 5 - shared utility helpers
//
// This file is compiled into every /bin utility (each utility project
// includes it via <Compile Include="../Common/UtilCommon.cs" />), so the
// utilities stay standalone .NET 10 assemblies while sharing the small
// amount of argument-parsing / text-handling code they have in common.
//
// korlib constraints (NeutrinoOS's IL BCL subset) shape these helpers:
//   - string.Split is not implemented: Util.Split provides it.
//   - Array.Sort is not implemented: Util.Sort / Util.SortIgnoreCase
//     provide ordinal and case-insensitive-alphabetical sorts.
//   - int.Parse is not implemented: Util.TryParseInt parses manually.

using System;
using System.IO;
using System.Text;
using NeutrinoOS.DDK.Kernel;

namespace NeutrinoOS.Utils;

/// <summary>Helpers shared by the NeutrinoOS Phase 5 utility suite.</summary>
public static class Util
{
    /// <summary>
    /// Prints "<c>usage</c>" plus the option lines and returns 0 (the
    /// conventional --help exit code).
    /// </summary>
    public static int Help(string usage, params string[] lines)
    {
        Console.WriteLine(usage);
        for (int i = 0; i < lines.Length; i++)
            Console.WriteLine(lines[i]);
        return 0;
    }

    /// <summary>Writes an error to stderr prefixed with "neutrinoos:" and returns the exit code.</summary>
    public static int Fail(string program, string message, int exitCode = 1)
    {
        Console.Error.WriteLine("neutrinoos: " + program + ": " + message);
        return exitCode;
    }

    /// <summary>True when the argument is exactly "-" (stdin/stdout marker).</summary>
    public static bool IsDash(string arg) => arg == "-";

    /// <summary>Parses a decimal integer; no exceptions, no '+' sign.</summary>
    public static bool TryParseInt(string s, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(s))
            return false;

        int i = 0;
        bool negative = false;
        if (s[0] == '-')
        {
            negative = true;
            i = 1;
            if (s.Length == 1)
                return false;
        }

        int result = 0;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9')
                return false;
            if (result > 1000000000 / 10)
                return false;
            result = result * 10 + (c - '0');
        }

        value = negative ? -result : result;
        return true;
    }

    /// <summary>
    /// Splits on runs of whitespace (space, tab, CR, LF); no empty
    /// entries. NeutrinoOS replacement for string.Split.
    /// </summary>
    public static string[] SplitWhitespace(string text)
    {
        var parts = new System.Collections.Generic.List<string>();
        int i = 0;
        int n = text.Length;
        while (i < n)
        {
            while (i < n && IsWhitespace(text[i]))
                i++;
            if (i >= n)
                break;
            int start = i;
            while (i < n && !IsWhitespace(text[i]))
                i++;
            parts.Add(text.Substring(start, i - start));
        }
        return parts.ToArray();
    }

    /// <summary>
    /// Splits a "d1:d2:..." style list (used for $PATH handling).
    /// </summary>
    public static string[] SplitList(string text, char separator)
    {
        var parts = new System.Collections.Generic.List<string>();
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == separator)
            {
                if (i > start)
                    parts.Add(text.Substring(start, i - start));
                start = i + 1;
            }
        }
        return parts.ToArray();
    }

    /// <summary>True for space, tab, CR or LF.</summary>
    public static bool IsWhitespace(char c) => c == ' ' || c == '\t' || c == '\r' || c == '\n';

    /// <summary>Ordinal string comparison (negative/zero/positive).</summary>
    public static int Compare(string a, string b)
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

    /// <summary>Case-insensitive ordinal comparison (ASCII folding, no allocations).</summary>
    public static int CompareIgnoreCase(string a, string b)
    {
        int n = a.Length < b.Length ? a.Length : b.Length;
        for (int i = 0; i < n; i++)
        {
            char ca = a[i];
            char cb = b[i];
            if (ca >= 'a' && ca <= 'z') ca = (char)(ca - 32);
            if (cb >= 'a' && cb <= 'z') cb = (char)(cb - 32);
            if (ca != cb)
                return ca < cb ? -1 : 1;
        }
        if (a.Length == b.Length)
            return 0;
        return a.Length < b.Length ? -1 : 1;
    }

    /// <summary>
    /// In-place insertion sort for directory listings: alphabetical,
    /// case-insensitive (FAT mixes uppercase 8.3 names with preserved
    /// case), with a case-sensitive tiebreak so equally spelled names
    /// stay deterministic (uppercase first). Array.Sort is not in korlib.
    /// </summary>
    public static void SortIgnoreCase(string[] items)
    {
        for (int i = 1; i < items.Length; i++)
        {
            string key = items[i];
            int j = i - 1;
            while (j >= 0 && CompareListing(items[j], key) > 0)
            {
                items[j + 1] = items[j];
                j--;
            }
            items[j + 1] = key;
        }
    }

    /// <summary>Comparison behind SortIgnoreCase: fold case, then raw tiebreak.</summary>
    private static int CompareListing(string a, string b)
    {
        int c = CompareIgnoreCase(a, b);
        if (c != 0)
            return c;
        return Compare(a, b);
    }

    /// <summary>In-place insertion sort (ordinal) - Array.Sort is not in korlib.</summary>
    public static void Sort(string[] items)
    {
        for (int i = 1; i < items.Length; i++)
        {
            string key = items[i];
            int j = i - 1;
            while (j >= 0 && Compare(items[j], key) > 0)
            {
                items[j + 1] = items[j];
                j--;
            }
            items[j + 1] = key;
        }
    }

    /// <summary>
    /// Minimal glob matcher for find/ls patterns: '*' matches any run,
    /// '?' matches one character; everything else is literal.
    /// </summary>
    public static bool GlobMatch(string pattern, string text)
    {
        return GlobMatch(pattern, 0, text, 0);
    }

    private static bool GlobMatch(string p, int pi, string t, int ti)
    {
        while (pi < p.Length)
        {
            char pc = p[pi];
            if (pc == '*')
            {
                // Collapse consecutive stars.
                while (pi < p.Length && p[pi] == '*')
                    pi++;
                if (pi == p.Length)
                    return true;
                for (int k = ti; k <= t.Length; k++)
                {
                    if (GlobMatch(p, pi, t, k))
                        return true;
                }
                return false;
            }
            if (ti >= t.Length)
                return false;
            if (pc != '?' && pc != t[ti])
                return false;
            pi++;
            ti++;
        }
        return ti == t.Length;
    }

    /// <summary>Left-pads the decimal representation of a value to the given width.</summary>
    public static string PadLeft(long value, int width)
    {
        string s = value.ToString();
        if (s.Length >= width)
            return s;
        var sb = new StringBuilder();
        for (int i = s.Length; i < width; i++)
            sb.Append(' ');
        sb.Append(s);
        return sb.ToString();
    }

    /// <summary>Reads all remaining standard input as text.</summary>
    public static string ReadAllStdin()
    {
        return Console.In.ReadToEnd();
    }

    /// <summary>
    /// Reads a whole text file, or standard input when the path is "-".
    /// Returns false and writes an error on failure.
    /// </summary>
    public static bool TryReadInput(string program, string path, out string text)
    {
        text = "";
        if (path == "-")
        {
            text = ReadAllStdin();
            return true;
        }
        if (!File.Exists(path))
        {
            Fail(program, path + ": no such file");
            return false;
        }
        text = File.ReadAllText(path);
        return true;
    }

    /// <summary>Counts lines in text ("\n" separators; a trailing newline does not add a line).</summary>
    public static int CountLines(string text)
    {
        if (text.Length == 0)
            return 0;
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                count++;
        }
        if (text[text.Length - 1] != '\n')
            count++;
        return count;
    }

    /// <summary>Counts words (runs of non-whitespace).</summary>
    public static int CountWords(string text)
        => SplitWhitespace(text).Length;

    /// <summary>True when the path names the root or an empty path.</summary>
    public static bool IsRoot(string path) => path == "/" || string.IsNullOrEmpty(path);

    /// <summary>
    /// Reads environment variable <paramref name="name"/>; returns "" when
    /// unset. The shell and all utilities share one process-wide environment
    /// table (see the env utility); this wraps the SysInfo enumeration export.
    /// </summary>
    public static string GetEnvironment(string name)
    {
        int count = SysInfo.GetEnvironmentVariableCount();
        for (int i = 0; i < count; i++)
        {
            if (SysInfo.TryGetEnvironmentVariable(i, out string varName, out string varValue) &&
                varName == name)
            {
                return varValue;
            }
        }
        return "";
    }

    /// <summary>
    /// Splits text into lines on '\n', dropping a trailing '\r' from each
    /// line and ignoring a final newline (no dangling empty line).
    /// </summary>
    public static string[] SplitLines(string text)
    {
        var lines = new System.Collections.Generic.List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                int end = i;
                if (end > start && text[end - 1] == '\r')
                    end--;
                lines.Add(text.Substring(start, end - start));
                start = i + 1;
            }
        }
        if (start < text.Length)
        {
            int end = text.Length;
            if (end > start && text[end - 1] == '\r')
                end--;
            lines.Add(text.Substring(start, end - start));
        }
        return lines.ToArray();
    }

    /// <summary>Reverses an array in place.</summary>
    public static void Reverse(string[] items)
    {
        for (int i = 0, j = items.Length - 1; i < j; i++, j--)
        {
            string tmp = items[i];
            items[i] = items[j];
            items[j] = tmp;
        }
    }
}
