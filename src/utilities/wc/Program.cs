// NeutrinoOS Phase 5 utility: wc - count lines, words and characters
//
// usage: wc [-l] [-w] [-c] [file...]
//   -l   count lines
//   -w   count words
//   -c   count characters (bytes of the UTF-8 read-back)
//   With none of the flags, all three are printed. With no file,
//   reads standard input.

using System;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Wc;

/// <summary>The wc utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 when a file could not be read.</summary>
    public static int Main(string[] args)
    {
        bool lines = false, words = false, chars = false;
        var files = new System.Collections.Generic.List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: wc [-l] [-w] [-c] [file...]",
                    "  -l   count lines",
                    "  -w   count words",
                    "  -c   count characters",
                    "  Default: all three. With no file, reads standard input.");
            }
            if (a == "-l")
                lines = true;
            else if (a == "-w")
                words = true;
            else if (a == "-c")
                chars = true;
            else if (a.Length > 1 && a[0] == '-')
                return Util.Fail("wc", a + ": unknown option");
            else
                files.Add(a);
        }

        if (!lines && !words && !chars)
        {
            lines = true;
            words = true;
            chars = true;
        }

        bool fromStdin = files.Count == 0;
        if (fromStdin)
            files.Add("-");

        int rc = 0;
        long totalLines = 0, totalWords = 0, totalChars = 0;

        for (int i = 0; i < files.Count; i++)
        {
            if (!Util.TryReadInput("wc", files[i], out string text))
            {
                rc = 1;
                continue;
            }

            long l = lines ? Util.CountLines(text) : 0;
            long w = words ? Util.CountWords(text) : 0;
            long c = chars ? text.Length : 0;
            totalLines += l;
            totalWords += w;
            totalChars += c;

            PrintCounts(l, w, c, lines, words, chars, fromStdin ? null : files[i]);
        }

        if (!fromStdin && files.Count > 1)
            PrintCounts(totalLines, totalWords, totalChars, lines, words, chars, "total");

        return rc;
    }

    private static void PrintCounts(long l, long w, long c, bool showL, bool showW, bool showC, string? name)
    {
        var sb = new System.Text.StringBuilder();
        if (showL)
            sb.Append(Util.PadLeft(l, 7));
        if (showW)
            sb.Append(Util.PadLeft(w, 7));
        if (showC)
            sb.Append(Util.PadLeft(c, 7));
        if (name != null)
        {
            sb.Append(' ');
            sb.Append(name);
        }
        Console.WriteLine(sb.ToString());
    }
}
