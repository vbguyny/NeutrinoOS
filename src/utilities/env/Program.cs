// NeutrinoOS Phase 5 utility: env - print the environment
//
// usage: env
//   Prints NAME=VALUE for every environment variable (the shell and the
//   application share one environment table in korlib; the enumeration
//   goes through the DDK SysInfo kernel export).

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Kernel;

namespace NeutrinoOS.Utility.Env;

/// <summary>The env utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; always returns 0.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: env",
                "  Print the environment (NAME=VALUE per line).",
                "  Use the shell's export/unset built-ins to change it.");
        }

        if (args.Length > 0)
            return Util.Fail("env", "usage: env");

        int count = SysInfo.GetEnvironmentVariableCount();
        for (int i = 0; i < count; i++)
        {
            if (SysInfo.TryGetEnvironmentVariable(i, out string name, out string value))
                Console.WriteLine(name + "=" + value);
        }
        return 0;
    }
}
