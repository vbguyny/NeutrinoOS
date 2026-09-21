// NeutrinoOS Phase 4 application template.
//
// Build on Windows 11 (or in WSL) with:
//     dotnet build -c Release -f net10.0
// then copy the output DLL to the NeutrinoOS image (see README.md in this
// folder) and run it from the shell:
//     neutrinoos> run /apps/myapp.dll
//
// The application uses the standard .NET BCL surface; on NeutrinoOS the
// kernel resolves those references against korlib (see
// docs/PHASE4-JIT-COMPAT.md for the supported subset and known gaps).

using System;

namespace MyApp;

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
