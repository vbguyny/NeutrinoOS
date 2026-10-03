// NeutrinoOS Phase 5 utility: sort - sort lines of text
//
// usage: sort [-f] [-r] [-u] [file...]
//
//   -f   fold case (case-insensitive order)
//   -r   reverse the result
//   -u   keep only unique lines (after sorting)
//
// Reads the named files in order (or standard input when none are
// given, or for a "-" operand) and writes the sorted lines.

using System;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Sort;

/// <summary>The sort utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 when a file could not be read.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;

        bool foldCase = false;
        bool reverse = false;
        bool unique = false;
        var files = new System.Collections.Generic.List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: sort [-f] [-r] [-u] [file...]",
                    "  -f   fold case (case-insensitive order)",
                    "  -r   reverse the result",
                    "  -u   keep only unique lines",
                    "  With no file (or '-'), reads standard input.");
            }
            if (a == "-f")
                foldCase = true;
            else if (a == "-r")
                reverse = true;
            else if (a == "-u")
                unique = true;
            else if (a.Length > 1 && a[0] == '-')
                return Util.Fail("sort", a + ": unknown option");
            else
                files.Add(a);
        }

        var lines = new System.Collections.Generic.List<string>();
        if (files.Count == 0)
        {
            if (!Console.IsInputRedirected)
                return Util.Fail("sort", "missing file operand");
            AddLines(lines, Util.ReadAllStdin());
        }
        else
        {
            int rc = 0;
            for (int i = 0; i < files.Count; i++)
            {
                if (!Util.TryReadInput("sort", files[i], out string text))
                {
                    rc = 1;
                    continue;
                }
                AddLines(lines, text);
            }
            if (rc != 0)
                return rc;
        }

        string[] items = lines.ToArray();
        if (foldCase)
            Util.SortIgnoreCase(items);
        else
            Util.Sort(items);
        if (reverse)
            Util.Reverse(items);

        string previous = null;
        bool first = true;
        for (int i = 0; i < items.Length; i++)
        {
            if (unique && !first)
            {
                int c = foldCase
                    ? Util.CompareIgnoreCase(previous, items[i])
                    : Util.Compare(previous, items[i]);
                if (c == 0)
                    continue;
            }
            Console.WriteLine(items[i]);
            previous = items[i];
            first = false;
        }
        return 0;
    }

    /// <summary>Appends the lines of <paramref name="text"/> to the list.</summary>
    private static void AddLines(System.Collections.Generic.List<string> lines, string text)
    {
        string[] part = Util.SplitLines(text);
        for (int i = 0; i < part.Length; i++)
            lines.Add(part[i]);
    }
}
