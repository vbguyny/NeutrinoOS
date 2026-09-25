// NeutrinoOS Phase 8 - npkg: tiny string helpers + JSON text writer
//
// korlib does not implement string.StartsWith / string.Split /
// string.Trim and forbids System.Text.Json, so npkg carries its own
// replacements. Reading JSON uses NeutrinoOS.Packaging.Json; WRITING
// JSON is done here with a minimal hand-rolled writer so the on-disk
// formats (installed.json, journal.log, repos.json) stay byte-stable
// and free of any BCL serializer dependencies.

using System;
using System.Collections.Generic;
using System.Text;
using NeutrinoOS.Packaging;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Npkg;

/// <summary>
/// Small ordinal string helpers used across npkg (StartsWith, Split and
/// Trim are outside the korlib subset).
/// </summary>
public static class Str
{
    /// <summary>Ordinal prefix test ("StartsWith" is not in korlib).</summary>
    public static bool Starts(string text, string prefix)
    {
        if (text == null || prefix == null || prefix.Length > text.Length)
            return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            if (text[i] != prefix[i])
                return false;
        }
        return true;
    }

    /// <summary>Ordinal substring test built on string.IndexOf.</summary>
    public static bool Contains(string text, string needle)
    {
        if (text == null || needle == null || needle.Length == 0)
            return false;
        return text.IndexOf(needle) >= 0;
    }

    /// <summary>Splits on a single separator character (delegates to Util.SplitList).</summary>
    public static string[] Split(string text, char separator)
    {
        return Util.SplitList(text, separator);
    }

    /// <summary>Trims ASCII whitespace (space, tab, CR, LF) from both ends.</summary>
    public static string TrimAscii(string text)
    {
        if (text == null)
            return "";
        int start = 0;
        int end = text.Length;
        while (start < end && Util.IsWhitespace(text[start]))
            start++;
        while (end > start && Util.IsWhitespace(text[end - 1]))
            end--;
        return text.Substring(start, end - start);
    }

    /// <summary>Last path component ("/apps/hello/hello.dll" -> "hello.dll").</summary>
    public static string LastSegment(string path)
    {
        if (path == null || path.Length == 0)
            return "";
        int end = path.Length;
        while (end > 1 && path[end - 1] == '/')
            end--;
        int i = end;
        while (i > 0 && path[i - 1] != '/')
            i--;
        return path.Substring(i, end - i);
    }

    /// <summary>Parent component of a path ("/bin/x.dll" -> "/bin").</summary>
    public static string DirName(string path)
    {
        return NpkgPaths.ParentOf(path);
    }

    /// <summary>Joins two path fragments with exactly one '/' between them.</summary>
    public static string JoinPath(string left, string right)
    {
        if (left == null || left.Length == 0)
            return right == null ? "" : right;
        if (right == null || right.Length == 0)
            return left;
        if (left[left.Length - 1] == '/')
            return left + right;
        return left + "/" + right;
    }

    /// <summary>Case-insensitive ordinal equality (hex fingerprints and friends).</summary>
    public static bool EqualIgnoreCase(string a, string b)
    {
        if (a == null || b == null)
            return a == b;
        return a.ToLower() == b.ToLower();
    }

    /// <summary>
    /// True when every character is an ASCII hex digit (used to validate
    /// key files and fingerprints).
    /// </summary>
    public static bool IsHex(string text)
    {
        if (text == null || text.Length == 0)
            return false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex)
                return false;
        }
        return true;
    }
}

/// <summary>
/// Minimal JSON text writer + defensive readers on top of the packaging
/// Json DOM. The writer always emits compact JSON with a trailing
/// newline handled by the caller.
/// </summary>
public static class Jsn
{
    /// <summary>Quotes and escapes a string (or returns "" for null).</summary>
    public static string Quote(string value)
    {
        if (value == null)
            return "\"\"";
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '"')
                sb.Append("\\\"");
            else if (c == '\\')
                sb.Append("\\\\");
            else if (c == '\n')
                sb.Append("\\n");
            else if (c == '\r')
                sb.Append("\\r");
            else if (c == '\t')
                sb.Append("\\t");
            else if (c < ' ')
            {
                sb.Append("\\u");
                sb.Append(HexDigit((c >> 12) & 0xF));
                sb.Append(HexDigit((c >> 8) & 0xF));
                sb.Append(HexDigit((c >> 4) & 0xF));
                sb.Append(HexDigit(c & 0xF));
            }
            else
            {
                sb.Append(c);
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Formats a decimal number for JSON output.</summary>
    public static string Num(long value)
    {
        return TextConv.LongToString(value);
    }

    /// <summary>Parses a JSON object, returning null when the text is invalid.</summary>
    public static JsonObject ParseObject(string text)
    {
        if (text == null)
            return null;
        try
        {
            object value = Json.Parse(text);
            return value as JsonObject;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Reads a string member, "" when absent or of another type.</summary>
    public static string GetStr(JsonObject obj, string key)
    {
        if (obj == null || key == null || !obj.Has(key))
            return "";
        object value = obj.Get(key);
        string s = value as string;
        return s == null ? "" : s;
    }

    /// <summary>Reads a string array member, empty when absent.</summary>
    public static string[] StrArray(JsonObject obj, string key)
    {
        if (obj == null || key == null || !obj.Has(key))
            return new string[0];
        JsonArray arr = null;
        try { arr = obj.GetArray(key); } catch (Exception) { return new string[0]; }
        if (arr == null)
            return new string[0];
        var result = new List<string>();
        for (int i = 0; i < arr.Count; i++)
        {
            object value = arr.Get(i);
            string s = value as string;
            if (s != null)
                result.Add(s);
        }
        return result.ToArray();
    }

    /// <summary>Reads a string-to-string object member, empty when absent.</summary>
    public static Dictionary<string, string> DictOf(JsonObject obj, string key)
    {
        var map = new Dictionary<string, string>();
        if (obj == null || key == null || !obj.Has(key))
            return map;
        JsonObject sub = null;
        try { sub = obj.GetObject(key); } catch (Exception) { return map; }
        if (sub == null)
            return map;
        foreach (string k in sub.Keys())
        {
            if (k == null || k.Length == 0)
                continue;
            object value = sub.Get(k);
            string s = value as string;
            map[k] = s == null ? "" : s;
        }
        return map;
    }

    private static char HexDigit(int value)
    {
        const string digits = "0123456789abcdef";
        return digits[value & 0xF];
    }
}
