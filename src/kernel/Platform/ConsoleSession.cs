// NeutrinoOS kernel - Phase 2 interactive shell
//
// The boot thread hosts the `neutrinoos>` prompt. All console I/O goes
// through System.Console (korlib) -> CAL -> /dev/ttyS0 -> UART 16550,
// driven by the line discipline (echo, backspace editing, Ctrl+C/D/U,
// 32-entry arrow-key history, ANSI escape parsing).
//
// Shell behavior:
//   - Phase 2 (done): Enter submits the line; the pipeline is exercised
//     end-to-end. Ctrl+C cancels the current line, Ctrl+D on an empty
//     line prints "logout" and stops the shell.
//   - Phase 4 (this file): the minimal command handling needed to run
//     .NET 10 applications - `run <path> [args...]`, `help`, `exit`.
//     The full command parser (ls, cat, curl, ...) is Phase 5.

using System;
using ProtonOS.X64;

namespace ProtonOS.Platform;

/// <summary>
/// Minimal interactive shell on the serial console.
/// </summary>
public static class ConsoleSession
{
    private const string Prompt = "neutrinoos> ";

    /// <summary>
    /// Run the interactive session (never returns except after EOF).
    /// </summary>
    public static void Run()
    {
        Console.WriteLine("[SHELL] NeutrinoOS console ready.");
        Console.WriteLine("Type 'help' for available commands.");

        while (true)
        {
            Console.Write(Prompt);

            string? line = Console.ReadLine();

            if (line == null)
            {
                if (Console.LastReadLineCanceled)
                {
                    // Ctrl+C: the line discipline already printed "^C" and
                    // a newline; just show a fresh prompt.
                    continue;
                }

                // Ctrl+D on an empty line: end of input.
                Console.WriteLine("logout");
                break;
            }

            if (!TryParseCommand(line, out string cmd, out string[] args))
                continue;

            if (cmd == "exit" || cmd == "logout")
            {
                Console.WriteLine("logout");
                break;
            }
            else if (cmd == "help")
            {
                Console.WriteLine("NeutrinoOS shell (Phase 4 commands):");
                Console.WriteLine("  run <path> [args...]  execute a .NET 10 assembly, e.g. run /apps/hello.dll");
                Console.WriteLine("  help                  show this help");
                Console.WriteLine("  exit                  end the session (Ctrl+D also works)");
                Console.WriteLine("  (the full command parser arrives in Phase 5)");
            }
            else if (cmd == "run")
            {
                if (args.Length == 0)
                {
                    Console.WriteLine("usage: run <path> [args...]");
                    continue;
                }

                string path = args[0];
                string[] runArgs = new string[args.Length - 1];
                for (int i = 1; i < args.Length; i++)
                    runArgs[i - 1] = args[i];

                AssemblyRunner.Run(path, runArgs);
            }
            else
            {
                Console.WriteLine("Unknown command: " + cmd + " (type 'help'; the command parser is Phase 5)");
            }
        }

        CPU.HaltForever();
    }

    /// <summary>
    /// Split a submitted line into the command and its whitespace-separated
    /// arguments. Returns false for empty/whitespace-only input.
    /// </summary>
    private static bool TryParseCommand(string line, out string cmd, out string[] args)
    {
        cmd = "";
        args = new string[0];

        int n = line.Length;
        int i = 0;
        while (i < n && (line[i] == ' ' || line[i] == '\t')) i++;
        if (i >= n)
            return false;

        int cmdStart = i;
        while (i < n && line[i] != ' ' && line[i] != '\t') i++;
        cmd = line.Substring(cmdStart, i - cmdStart);

        // Count arguments
        int count = 0;
        int j = i;
        while (j < n)
        {
            while (j < n && (line[j] == ' ' || line[j] == '\t')) j++;
            if (j >= n) break;
            count++;
            while (j < n && line[j] != ' ' && line[j] != '\t') j++;
        }

        args = new string[count];
        int k = 0;
        j = i;
        while (j < n && k < count)
        {
            while (j < n && (line[j] == ' ' || line[j] == '\t')) j++;
            if (j >= n) break;
            int argStart = j;
            while (j < n && line[j] != ' ' && line[j] != '\t') j++;
            args[k++] = line.Substring(argStart, j - argStart);
        }

        return true;
    }
}
