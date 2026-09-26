// NeutrinoOS application template.
//
// Create a project from this template and build it:
//     dotnet new neutrino-console -n MyApp
//     cd MyApp
//     dotnet build -c Release
//
// The Release build also produces MyApp.npkg (bin/Release/net10.0/) through
// the NeutrinoOS SDK MSBuild targets. Install it on the device with
// `npkg install` (see docs/SDK-GETTING-STARTED.md) or run it directly from
// the shell once the dll is on the image:
//     neutrinoos> run /apps/myapp.dll
//
// The application uses the standard .NET BCL surface; on NeutrinoOS the
// kernel resolves those references against korlib (see
// docs/PHASE4-JIT-COMPAT.md for the supported subset and known gaps).

using System;

namespace NeutrinoConsoleApp;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("Hello from NeutrinoOS!");
        if (args != null)
        {
            for (int i = 0; i < args.Length; i++)
                Console.WriteLine("arg: " + args[i]);
        }
        return 0;
    }
}
