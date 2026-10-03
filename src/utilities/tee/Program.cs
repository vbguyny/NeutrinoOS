// NeutrinoOS Phase 5 utility: tee - copy input to files and stdout
//
// usage: tee [-a] file...
//
//   -a   append to the files instead of overwriting them
//
// Reads standard input (a pipe or '<' redirect; the interactive
// console blocks on read, so require a redirect) and writes it to
// standard output and to each file.

using System;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Tee;

/// <summary>The tee utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 when a file could not be written.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;

        bool append = false;
        var files = new System.Collections.Generic.List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: tee [-a] file...",
                    "  -a   append to the files instead of overwriting",
                    "  Copy standard input to standard output and to each file.",
                    "  Requires a pipe or '<' redirect on standard input.");
            }
            if (a == "-a")
                append = true;
            else if (a.Length > 1 && a[0] == '-')
                return Util.Fail("tee", a + ": unknown option");
            else
                files.Add(a);
        }

        if (files.Count == 0)
            return Util.Fail("tee", "missing file operand");
        if (!Console.IsInputRedirected)
            return Util.Fail("tee", "no input (use a pipe or < redirect)");

        string text = Util.ReadAllStdin();
        Console.Write(text);

        int rc = 0;
        for (int i = 0; i < files.Count; i++)
        {
            try
            {
                if (append)
                    System.IO.File.AppendAllText(files[i], text);
                else
                    System.IO.File.WriteAllText(files[i], text);
            }
            catch (Exception)
            {
                rc = Util.Fail("tee", files[i] + ": write failed");
            }
        }
        return rc;
    }
}
