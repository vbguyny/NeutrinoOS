// NeutrinoOS DDK - "--version" flag helper (Phase 7 release packaging)
//
// Utilities share one version string (the kernel's). Each utility calls
// VersionFlag.Handle(args) at the top of Main; when the user passed
// --version/-V it prints the string and returns true, so the utility
// returns 0 immediately.

using ProtonOS.DDK.Kernel;

namespace ProtonOS.DDK.Util;

/// <summary>Shared --version handling for utilities.</summary>
public static class VersionFlag
{
    /// <summary>Handle --version/-V; true when the flag was present (caller returns 0).</summary>
    public static bool Handle(string[] args)
    {
        if (args == null)
            return false;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--version" || args[i] == "-V")
            {
                System.Console.WriteLine(SysInfo.GetVersionString());
                return true;
            }
        }
        return false;
    }
}
