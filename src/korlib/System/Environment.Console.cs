// NeutrinoOS korlib - System.Environment (Phase 2 additions)
//
// Adds process-style members on top of the existing Environment class:
// Exit/ExitCode, GetCommandLineArgs and CurrentDirectory.
//
// Deviations from the official .NET BCL (documented per the Phase 2 spec):
//   - Environment.Exit halts the calling process (or the kernel shell host)
//     through the kernel's process-exit path.
//   - GetCommandLineArgs returns an empty array in Phase 2: the boot shell
//     has no argv. Application argv captured by execve is wired in a later
//     phase.
//   - CurrentDirectory returns the kernel's current working directory
//     ("/" when no VFS working directory is set).

using System.Runtime.InteropServices;

namespace System;

public static partial class Environment
{
#if KORLIB_IL
    // IL metadata stubs; the AOT kernel provides the real implementations.
    private static void EnvExit(int exitCode) => throw new PlatformNotSupportedException();
    private static unsafe int EnvGetCurrentDirectory(char* buffer, int capacity) => throw new PlatformNotSupportedException();
#else
    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern void EnvExit(int exitCode);

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int EnvGetCurrentDirectory(char* buffer, int capacity);
#endif

    /// <summary>
    /// Gets or sets the exit code of the process. The value is returned by
    /// the process-exit path; on the kernel shell it is stored but no
    /// process termination occurs until <see cref="Exit(int)"/> is called.
    /// </summary>
    public static int ExitCode { get; set; }

    /// <summary>
    /// Terminates the calling process with the specified exit code. On the
    /// kernel boot shell this halts the CPU (NeutrinoOS Phase 2 behavior;
    /// there is no init process to return to).
    /// </summary>
    public static void Exit(int exitCode)
    {
        ExitCode = exitCode;
        EnvExit(exitCode);
    }

    /// <summary>
    /// Returns the command-line arguments of the process. NeutrinoOS
    /// Phase 2 has no argv for the boot shell, so this returns an empty
    /// array (documented deviation).
    /// </summary>
    public static string[] GetCommandLineArgs()
    {
        return new string[0];
    }

    /// <summary>
    /// Gets the fully qualified path of the current working directory.
    /// Phase 5: backed by korlib's Directory current-directory state,
    /// which the shell's cd built-in drives and all relative-path
    /// resolution consults.
    /// </summary>
    public static string CurrentDirectory
    {
        get
        {
            string cwd = global::System.IO.Directory.GetCurrentDirectory();
            return string.IsNullOrEmpty(cwd) ? "/" : cwd;
        }
        set
        {
            global::System.IO.Directory.SetCurrentDirectory(value);
        }
    }
}
