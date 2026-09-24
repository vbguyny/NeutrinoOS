// NeutrinoOS Phase 5 utility: grep - print lines matching a pattern
//
// usage: grep [-i] [-v] pattern [file...]
//   -i   case-insensitive match
//   -v   invert: print lines that do NOT match
//
// NeutrinoOS note: the Phase 5 regex scope is a literal substring match;
// a full regex engine is deferred to a later phase (documented in
// docs/PHASE5-UTILITIES.md). Exit codes follow grep convention:
// 0 = at least one line selected, 1 = none, 2 = error.

using System;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Grep;

/// <summary>The grep utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; see the exit-code convention in the file header.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        bool ignoreCase = false;
        bool invert = false;
        var operands = new System.Collections.Generic.List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: grep [-i] [-v] pattern [file...]",
                    "  -i   case-insensitive",
                    "  -v   print non-matching lines",
                    "  Pattern is a literal substring (no regex in Phase 5).",
                    "  Exit: 0 matched, 1 no match, 2 error.");
            }
            if (a == "-i")
                ignoreCase = true;
            else if (a == "-v")
                invert = true;
            else if (a == "-iv" || a == "-vi")
            {
                ignoreCase = true;
                invert = true;
            }
            else if (a.Length > 1 && a[0] == '-')
                return Util.Fail("grep", a + ": unknown option", 2);
            else
                operands.Add(a);
        }

        if (operands.Count == 0)
            return Util.Fail("grep", "missing pattern", 2);

        string pattern = operands[0];
        bool fromStdin = operands.Count == 1;
        if (fromStdin)
            operands.Add("-");

        if (ignoreCase)
            pattern = pattern.ToLower();

        bool selectedAny = false;
        string[] files = operands.ToArray();
        bool multipleFiles = files.Length > 2;

        for (int i = 1; i < files.Length; i++)
        {
            if (!Util.TryReadInput("grep", files[i], out string text))
                return 2;

            string[] lines = SplitLines(text);
            for (int l = 0; l < lines.Length; l++)
            {
                string line = lines[l];
                string haystack = ignoreCase ? line.ToLower() : line;
                bool match = haystack.IndexOf(pattern) >= 0;
                if (match != invert)
                {
                    if (multipleFiles && !fromStdin)
                    {
                        Console.Write(files[i]);
                        Console.Write(':');
                    }
                    Console.WriteLine(line);
                    selectedAny = true;
                }
            }
        }

        return selectedAny ? 0 : 1;
    }

    private static string[] SplitLines(string text)
    {
        var lines = new System.Collections.Generic.List<string>();
        int start = 0;
        for (int p = 0; p <= text.Length; p++)
        {
            if (p == text.Length || text[p] == '\n')
            {
                if (p > start)
                {
                    string line = text.Substring(start, p - start);
                    if (line.Length > 0 && line[line.Length - 1] == '\r')
                        line = line.Substring(0, line.Length - 1);
                    lines.Add(line);
                }
                else if (p < text.Length)
                {
                    lines.Add("");
                }
                start = p + 1;
            }
        }
        return lines.ToArray();
    }
}
