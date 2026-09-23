// NeutrinoOS Phase 5 - shared utility helpers
//
// This file is compiled into every /bin utility (each utility project
// includes it via <Compile Include="../Common/UtilCommon.cs" />), so the
// utilities stay standalone .NET 10 assemblies while sharing the small
// amount of argument-parsing / text-handling code they have in common.
//
// korlib constraints (NeutrinoOS's IL BCL subset) shape these helpers:
//   - string.Split is not implemented: Util.Split provides it.
//   - Array.Sort is not implemented: Util.Sort provides an ordinal sort.
//   - int.Parse is not implemented: Util.TryParseInt parses manually.

using System;
using System.IO;
using System.Text;

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

    /// <summary>Case-insensitive ordinal comparison.</summary>
    public static int CompareIgnoreCase(string a, string b)
        => Compare(a.ToLower(), b.ToLower());

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
}
