// NeutrinoOS Phase 5 utility: more - page through text files
//
// usage: more [-n lines] file...
//
//   -n lines   lines per screen (default: console height - 1)
//
// Keys: space/Enter show the next page, b goes back one page, q quits.
// The file is read whole (boot-volume files are small); with standard
// input redirected and no file arguments the input is printed as-is,
// because a pipe cannot deliver paging keystrokes.

using System;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.More;

/// <summary>The more utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 when a file could not be read.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;

        int pageSize = 0;
        var files = new System.Collections.Generic.List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: more [-n lines] file...",
                    "  -n lines   lines per screen (default: console height - 1)",
                    "",
                    "  space/Enter  next page",
                    "  b            previous page",
                    "  q            quit");
            }
            if (a == "-n")
            {
                if (i + 1 >= args.Length)
                    return Util.Fail("more", "-n: missing line count");
                if (!Util.TryParseInt(args[i + 1], out pageSize) || pageSize <= 0)
                    return Util.Fail("more", args[i + 1] + ": invalid line count");
                i++;
            }
            else if (a.Length > 1 && a[0] == '-')
            {
                return Util.Fail("more", a + ": unknown option");
            }
            else
            {
                files.Add(a);
            }
        }

        if (pageSize == 0)
        {
            pageSize = Console.WindowHeight - 1;
            if (pageSize < 5)
                pageSize = 5;
        }

        if (files.Count == 0)
        {
            if (!Console.IsInputRedirected)
                return Util.Fail("more", "missing file operand");
            Console.Write(Util.ReadAllStdin());
            return 0;
        }

        int rc = 0;
        for (int f = 0; f < files.Count; f++)
        {
            if (!Util.TryReadInput("more", files[f], out string text))
            {
                rc = 1;
                continue;
            }
            if (files.Count > 1)
                Console.WriteLine(":::::::: " + files[f] + " ::::::::");
            if (!Page(text, pageSize))
                break;      // user quit
        }
        return rc;
    }

    /// <summary>Prints <paramref name="text"/> in pages; false when the user quits.</summary>
    private static bool Page(string text, int pageSize)
    {
        string[] lines = Util.SplitLines(text);
        int line = 0;

        while (line < lines.Length)
        {
            int end = line + pageSize;
            if (end > lines.Length)
                end = lines.Length;

            for (int i = line; i < end; i++)
                Console.WriteLine(lines[i]);

            if (end >= lines.Length)
                return true;

            int percent = (int)((long)end * 100 / lines.Length);
            Console.Write("--More--(" + percent + "%)");

            var key = Console.ReadKey(true);
            char c = key.KeyChar;
            Console.WriteLine();

            if (c == 'q' || c == 'Q' || c == '\x03')
                return false;
            if (c == 'b' || c == 'B')
            {
                line = line - pageSize;
                if (line < 0)
                    line = 0;
            }
            else
            {
                line = end;
            }
        }
        return true;
    }
}
