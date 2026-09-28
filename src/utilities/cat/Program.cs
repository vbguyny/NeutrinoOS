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
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
            return Usage();

        // With no file the utility reads standard input. On the interactive
        // console stdin is at EOF immediately (the shell owns the line
        // discipline), so a bare `cat` would print nothing - show the usage
        // instead. Pipes and `<` redirects still read stdin normally.
        if (args.Length == 0)
        {
            if (!Console.IsInputRedirected)
                return Usage();
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

    /// <summary>Prints the usage block (shared by --help and a bare `cat`).</summary>
    private static int Usage()
    {
        return Util.Help(
            "usage: cat [file...]",
            "  Concatenate files to standard output.",
            "  With no file (or '-'), reads standard input.");
    }
}
