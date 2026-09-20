// NeutrinoOS kernel - Phase 2 interactive shell
//
// The boot thread hosts the `neutrinoos>` prompt. All console I/O goes
// through System.Console (korlib) -> CAL -> /dev/ttyS0 -> UART 16550,
// driven by the line discipline (echo, backspace editing, Ctrl+C/D/U,
// 32-entry arrow-key history, ANSI escape parsing).
//
// Shell behavior (Phase 2 - no command parser yet):
//   - Enter submits the line; the shell echoes it back via
//     System.Console.WriteLine so the pipeline is exercised end-to-end.
//   - Ctrl+C cancels the current line and re-prompts.
//   - Ctrl+D on an empty line prints "logout" and stops the shell.

using System;
using ProtonOS.X64;

namespace ProtonOS.Platform;

/// <summary>
/// Minimal interactive shell on the serial console (Phase 2).
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

            // Echo the submitted line back (validates the full
            // ReadLine -> WriteLine round trip; the command parser is a
            // later phase).
            Console.WriteLine(line);
        }

        CPU.HaltForever();
    }
}
