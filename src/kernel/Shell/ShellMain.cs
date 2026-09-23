// NeutrinoOS kernel - Phase 5 shell: REPL
//
// The interactive loop: banner, prompt (PS1-aware), read a line from the
// line discipline, record history, execute, and repeat. Ctrl+C cancels
// the current line; Ctrl+D on an empty line (or the exit built-in) ends
// the session. While waiting for input the shell pumps background jobs
// cooperatively (LineDiscipline.IdleHook -> IdlePump -> JobManager.Pump).

using System;
using System.Runtime.InteropServices;
using ProtonOS.Platform;
using ProtonOS.X64;

namespace ProtonOS.Shell;

/// <summary>The Phase 5 shell REPL (see file header).</summary>
public static unsafe class ShellMain
{
    /// <summary>True while the shell is waiting for a line (idle pump gate).</summary>
    private static bool _waitingForInput;

    /// <summary>
    /// Runs the interactive session. Returns after EOF / exit; the caller
    /// halts the CPU.
    /// </summary>
    public static void Run()
    {
        ShellInit.Initialize();

        Console.WriteLine("[SHELL] NeutrinoOS console ready.");
        Console.WriteLine("Type 'help' for available commands.");

        // Background jobs execute while the shell waits for input.
        LineDiscipline.IdleHook = &IdlePump;

        while (true)
        {
            Console.Write(ShellInit.BuildPrompt());

            _waitingForInput = true;
            string? line;
            try
            {
                line = Console.ReadLine();
            }
            finally
            {
                _waitingForInput = false;
            }

            if (line == null)
            {
                if (Console.LastReadLineCanceled)
                {
                    // Ctrl+C: fresh prompt.
                    continue;
                }

                // Ctrl+D on an empty line: end of input.
                Console.WriteLine("logout");
                break;
            }

            string trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            ShellInit.RecordHistory(trimmed);

            ShellExecutor.ExecuteLine(trimmed);

            if (ShellState.ExitRequested)
            {
                ShellInit.SaveHistoryOnExit();
                Console.WriteLine("logout");
                break;
            }
        }

        CPU.HaltForever();
    }

    /// <summary>
    /// Idle hook installed into the line discipline: runs one queued
    /// background job when the shell - not an application - is waiting
    /// for console input.
    /// </summary>
    [UnmanagedCallersOnly]
    public static void IdlePump()
    {
        if (!_waitingForInput)
            return;
        if (ShellExecutor.InForegroundCommand)
            return;
        if (!JobManager.HasQueuedJobs)
            return;
        JobManager.Pump();
    }
}
