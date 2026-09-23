// NeutrinoOS Phase 5 utility: echo - print arguments
//
// usage: echo [-n] [args...]
//   -n   do not print the trailing newline

using System;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Echo;

/// <summary>The echo utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; always returns 0.</summary>
    public static int Main(string[] args)
    {
        bool noNewline = false;
        int start = 0;

        if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: echo [-n] [args...]",
                "  -n   do not print the trailing newline");
        }

        if (args.Length > 0 && args[0] == "-n")
        {
            noNewline = true;
            start = 1;
        }

        for (int i = start; i < args.Length; i++)
        {
            if (i > start)
                Console.Write(' ');
            Console.Write(args[i]);
        }

        if (!noNewline)
            Console.WriteLine();

        return 0;
    }
}
