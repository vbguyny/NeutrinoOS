// NeutrinoOS Phase 5 utility: uname - print system information
//
// usage: uname [-a]
//   -a   print the full version string
//
// The version string comes from the kernel (Kernel_GetNeutrinoVersion):
// "NeutrinoOS 0.5 phase5 x86_64".

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Kernel;

namespace NeutrinoOS.Utility.Uname;

/// <summary>The uname utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; always returns 0.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        bool all = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: uname [-a]",
                    "  -a   print the full NeutrinoOS version string",
                    "  Without -a, prints the system name only.");
            }
            if (a == "-a")
                all = true;
            else
                return Util.Fail("uname", a + ": unknown option");
        }

        if (all)
            Console.WriteLine(SysInfo.GetVersionString());
        else
            Console.WriteLine("NeutrinoOS");
        return 0;
    }
}
