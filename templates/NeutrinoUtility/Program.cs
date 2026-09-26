// NeutrinoOS utility template.
//
// Utilities are small command-line tools on $PATH (/bin). Build with:
//     dotnet new neutrino-utility -n MyTool
//     cd MyTool
//     dotnet build -c Release        # also produces MyTool.npkg
//
// Keep the assembly name FAT 8.3 sized (<= 8 chars) when installing onto
// the FAT boot volume.
//
// Standard streams: Console.In/Out/Error map to the NeutrinoOS console;
// the exit code becomes $? in the shell.

using System;

namespace NeutrinoUtility;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            Console.WriteLine("Usage: neutrinoutility [file...]");
            Console.WriteLine("Reads files (or stdin) and prints them with line numbers.");
            return 0;
        }

        int number = 0;
        if (args.Length == 0)
        {
            string line;
            while ((line = Console.ReadLine()) != null)
                Console.WriteLine(++number + "\t" + line);
            return 0;
        }

        for (int i = 0; i < args.Length; i++)
        {
            if (!System.IO.File.Exists(args[i]))
            {
                Console.Error.WriteLine("neutrinoutility: " + args[i] + ": no such file");
                return 1;
            }
            string[] lines = System.IO.File.ReadAllLines(args[i]);
            for (int j = 0; j < lines.Length; j++)
                Console.WriteLine(++number + "\t" + lines[j]);
        }
        return 0;
    }
}
