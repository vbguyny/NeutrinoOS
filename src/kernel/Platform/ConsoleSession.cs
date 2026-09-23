// NeutrinoOS kernel - console session entry point (Phase 5)
//
// The boot thread hosts the interactive shell. All console I/O goes
// through System.Console (korlib) -> CAL -> /dev/ttyS0 (and /dev/vga0),
// driven by the line discipline (echo, backspace editing, Ctrl+C/D/U,
// 32-entry arrow-key history, ANSI escape parsing, tab completion).
//
// Phase 5: the full shell (parser, executor, built-ins, jobs) lives in
// the ProtonOS.Shell namespace (src/kernel/Shell/*.cs); this class is
// the entry point the kernel calls after boot.
//
//   - Phase 2: interactive prompt + line editing.
//   - Phase 4: minimal commands (run, help, exit).
//   - Phase 5: production shell - pipelines, redirection, background
//     jobs, environment variables, aliases, history persistence, tab
//     completion, PS1 - plus the external utility suite in /bin.

using ProtonOS.Shell;

namespace ProtonOS.Platform;

/// <summary>
/// Boot-console session: starts the Phase 5 shell on the boot thread.
/// </summary>
public static class ConsoleSession
{
    /// <summary>
    /// Runs the interactive session (never returns except after EOF/exit;
    /// the shell halts the CPU at the end).
    /// </summary>
    public static void Run()
    {
        ShellMain.Run();
    }
}
