// hello-console - the application half of the SDK sample pair.
//
//   dotnet build -c Release          # -> hellocon.npkg (incl. hellolib.dll)
//   neutrinoos> npkg install hellocon.npkg
//   neutrinoos> hellocon Ada

using System;
using HelloLibrary;

namespace HelloConsole;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine(Greeter.Message(args.Length > 0 ? args[0] : null));
        Console.WriteLine(Greeter.Describe());
        for (int i = 0; i < args.Length; i++)
            Console.WriteLine("arg[" + i + "] = " + args[i]);
        return 0;
    }
}
