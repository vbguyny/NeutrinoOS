// NeutrinoOS Phase 5 utility: head - print the first lines of files
//
// usage: head [-n N] [file...]
//   -n N   number of lines (default 10)
//   With no file, reads standard input.

using System;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Head;

/// <summary>The head utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 when a file could not be read.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        int count = 10;
        var files = new System.Collections.Generic.List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: head [-n N] [file...]",
                    "  -n N   print the first N lines (default 10)");
            }
            if (a == "-n")
            {
                if (i + 1 >= args.Length || !Util.TryParseInt(args[i + 1], out count) || count < 0)
                    return Util.Fail("head", "-n requires a number");
                i++;
            }
            else if (a.Length > 1 && a[0] == '-')
            {
                return Util.Fail("head", a + ": unknown option");
            }
            else
            {
                files.Add(a);
            }
        }

        if (files.Count == 0)
            files.Add("-");

        int rc = 0;
        for (int i = 0; i < files.Count; i++)
        {
            string path = files[i];
            if (!Util.TryReadInput("head", path, out string text))
            {
                rc = 1;
                continue;
            }

            int lines = 0;
            int start = 0;
            for (int p = 0; p <= text.Length && lines < count; p++)
            {
                if (p == text.Length || text[p] == '\n')
                {
                    if (p > start)
                    {
                        string line = text.Substring(start, p - start);
                        if (line.Length > 0 && line[line.Length - 1] == '\r')
                            line = line.Substring(0, line.Length - 1);
                        Console.WriteLine(line);
                    }
                    else if (p < text.Length)
                    {
                        Console.WriteLine();
                    }
                    lines++;
                    start = p + 1;
                }
            }
        }
        return rc;
    }
}
