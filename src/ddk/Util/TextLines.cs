// ProtonOS DDK - Text line helpers (Phase 6)
//
// File.ReadAllLines / WriteAllLines have no proven path in the guest
// JIT world, so user files use ReadAllText/WriteAllText plus these
// small manual helpers.

using System;

namespace ProtonOS.DDK.Util;

/// <summary>Line splitting/joining without String.Split (JIT-safe).</summary>
public static class TextLines
{
    /// <summary>Split text into lines (handles \n and \r\n; no empty tail).</summary>
    public static string[] Split(string text)
    {
        if (text == null || text.Length == 0)
            return new string[0];

        var lines = new string[16];
        int count = 0;
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == '\n')
            {
                int end = i;
                if (end > start && text[end - 1] == '\r')
                    end--;
                if (count == lines.Length)
                {
                    var bigger = new string[lines.Length * 2];
                    for (int k = 0; k < count; k++)
                        bigger[k] = lines[k];
                    lines = bigger;
                }
                lines[count++] = text.Substring(start, end - start);
                start = i + 1;
            }
        }

        var result = new string[count];
        for (int i = 0; i < count; i++)
            result[i] = lines[i];
        return result;
    }

    /// <summary>Join lines with '\n' (trailing newline included).</summary>
    public static string Join(string[] lines)
    {
        string s = "";
        for (int i = 0; i < lines.Length; i++)
        {
            s += lines[i];
            s += "\n";
        }
        return s;
    }
}
