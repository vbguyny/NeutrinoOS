// NeutrinoOS library template.
//
// Build with:
//     dotnet new neutrino-library -n MyLib
//     cd MyLib
//     dotnet build -c Release        # also produces MyLib.npkg (/lib)

using System;

namespace NeutrinoLibrary;

public static class MyLibrary
{
    public static string Greet(string name)
    {
        return "Hello, " + (name ?? "world") + "!";
    }

    /// <summary>Entry point used by `run` when the library is executed directly.</summary>
    public static int Main(string[] args)
    {
        Console.WriteLine(Greet(args.Length > 0 ? args[0] : null));
        return 0;
    }
}
