// NeutrinoOS Phase 5 utility: cat - concatenate and print files
//
// usage: cat [file...]
//   With no file (or "-"), reads standard input.

using System;
using System.IO;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Cat;

/// <summary>The cat utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 when a file could not be read.</summary>
    public static int Main(string[] args)
    {
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: cat [file...]",
                "  Concatenate files to standard output.",
                "  With no file (or '-'), reads standard input.");
        }

        if (args.Length == 0)
        {
            Console.Write(Util.ReadAllStdin());
            return 0;
        }

        int rc = 0;
        for (int i = 0; i < args.Length; i++)
        {
            string path = args[i];
            if (Util.IsDash(path))
            {
                Console.Write(Util.ReadAllStdin());
                continue;
            }
            if (!Util.TryReadInput("cat", path, out string text))
            {
                rc = 1;
                continue;
            }
            Console.Write(text);
        }
        return rc;
    }
}
