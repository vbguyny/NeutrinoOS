// NeutrinoOS test app - interactive greeting (p10hello)
//
// Prompts for a name and echoes it back as "Hello, <name>!". Deployed to
// /apps of the CLI image:
//   root-/> run /apps/p10hello.dll
//   Enter your name: Ada
//   Hello, Ada!
//
// Also usable as a startup app (startup --set run /apps/p10hello.dll): the
// prompt then appears during boot, before the shell banner. Exercises
// Console.Write + Console.ReadLine + string interpolation through the JIT
// and the UART line discipline.

using System;

namespace Phase10.Hello;

public static class Program
{
    public static int Main()
    {
        Console.Write("Enter your name: ");
        string name = Console.ReadLine();
        if (name == null)
        {
            // Input closed (EOF) or cancelled with Ctrl+C: no greeting.
            Console.WriteLine();
            Console.WriteLine("[p10hello] no name entered (input closed)");
            return 1;
        }

        Console.WriteLine($"Hello, {name}!");
        return 0;
    }
}
