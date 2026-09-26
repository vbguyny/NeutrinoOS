// file-utility - a shell utility that reads and writes files.
//
//   filetool demo  <path>            write a small file, read it back
//   filetool cat   <path>            print a file
//   filetool count <path>            count lines/characters
//
// Exit codes: 0 ok, 1 usage/IO error (becomes $? in the shell).

using System;
using System.IO;

namespace NeutrinoOS.Sample.FileTool;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Usage();
            return 1;
        }

        string command = args[0];
        string path = args[1];

        switch (command)
        {
            case "demo":
                return Demo(path);
            case "cat":
                return Cat(path);
            case "count":
                return Count(path);
            default:
                Console.Error.WriteLine("filetool: unknown command: " + command);
                Usage();
                return 1;
        }
    }

    private static int Demo(string path)
    {
        string[] lines =
        {
            "NeutrinoOS file utility demo",
            "written at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            "line three",
        };
        File.WriteAllLines(path, lines);
        Console.WriteLine("wrote " + lines.Length + " lines to " + path);

        string[] back = File.ReadAllLines(path);
        Console.WriteLine("read back " + back.Length + " lines:");
        for (int i = 0; i < back.Length; i++)
            Console.WriteLine("  " + back[i]);

        File.AppendAllText(path, "appended line\n");
        Console.WriteLine("appended 1 line (" + new FileInfo(path).Length + " bytes total)");
        return 0;
    }

    private static int Cat(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("filetool: " + path + ": no such file");
            return 1;
        }
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
            Console.WriteLine(lines[i]);
        return 0;
    }

    private static int Count(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("filetool: " + path + ": no such file");
            return 1;
        }
        string[] lines = File.ReadAllLines(path);
        long chars = 0;
        for (int i = 0; i < lines.Length; i++)
            chars += lines[i].Length;
        Console.WriteLine(lines.Length + " lines, " + chars + " characters");
        return 0;
    }

    private static void Usage()
    {
        Console.WriteLine("usage: filetool <demo|cat|count> <path>");
    }
}
