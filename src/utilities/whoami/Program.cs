// NeutrinoOS Phase 5 utility: whoami - print the current user name
//
// usage: whoami
//
// Reads $USER from the shared environment table (the shell exports it;
// it defaults to "root" because NeutrinoOS currently runs an
// unprivileged single-user shell).

using System;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Whoami;

/// <summary>The whoami utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; always returns 0.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: whoami",
                "  Print the current user name ($USER, default \"root\").");
        }
        if (args.Length > 0)
            return Util.Fail("whoami", "usage: whoami");

        string user = Util.GetEnvironment("USER");
        if (user.Length == 0)
            user = Util.GetEnvironment("USERNAME");
        if (user.Length == 0)
            user = "root";
        Console.WriteLine(user);
        return 0;
    }
}
