// NeutrinoOS Phase 5 utility: ps - list processes and threads
//
// usage: ps
//   NeutrinoOS Phase 5 has two "process" layers to show:
//     - shell background jobs (started with '&'), each with a job id
//       and a synthetic PID (1000+id); state + exit code come from the
//       kernel's job table (Kernel_GetShellJobAt);
//     - kernel threads (the boot shell thread, the PS/2 and console
//       worker threads, ...) with their ids and states
//       (Kernel_GetThreadInfoAt).
//   There is no per-process CPU time in Phase 5 (documented in
//   docs/PHASE5-UTILITIES.md).

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Kernel;

namespace NeutrinoOS.Utility.Ps;

/// <summary>The ps utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; always returns 0.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: ps",
                "  List shell background jobs and kernel threads.",
                "  Jobs are started with '&' (see the shell's jobs built-in).");
        }
        if (args.Length > 0)
            return Util.Fail("ps", "usage: ps");

        Console.WriteLine("SHELL JOBS");
        int jobCount = SysInfo.GetShellJobCount();
        if (jobCount == 0)
        {
            Console.WriteLine("  (no background jobs)");
        }
        else
        {
            Console.WriteLine("  PID   JOB   STATE     EXIT   COMMAND");
            for (int i = 0; i < jobCount; i++)
            {
                if (!SysInfo.TryGetShellJobInfo(i, out int pid, out int jobId, out int state,
                        out int exitCode, out string command))
                    continue;

                Console.Write("  ");
                Console.Write(Util.PadLeft(pid, 5));
                Console.Write(" ");
                Console.Write(Util.PadLeft(jobId, 5));
                Console.Write("   ");
                Console.Write(PadRight(JobStateName(state), 9));
                Console.Write(" ");
                Console.Write(Util.PadLeft(exitCode, 4));
                Console.Write("   ");
                Console.WriteLine(command);
            }
        }

        Console.WriteLine();
        Console.WriteLine("KERNEL THREADS");
        int threadCount = Thread.GetThreadCount();
        if (threadCount == 0)
        {
            Console.WriteLine("  (no threads)");
        }
        else
        {
            Console.WriteLine("  TID   STATE       STACK-KB");
            for (int i = 0; i < threadCount; i++)
            {
                if (!SysInfo.TryGetThreadInfo(i, out uint tid, out int state, out ulong stackSize))
                    continue;

                Console.Write("  ");
                Console.Write(Util.PadLeft((long)tid, 5));
                Console.Write("   ");
                Console.Write(PadRight(ThreadStateName(state), 11));
                Console.Write(" ");
                if (stackSize >= 1024)
                {
                    Console.Write(Util.PadLeft((long)(stackSize / 1024), 6));
                }
                else
                {
                    Console.Write(Util.PadLeft((long)stackSize, 6));
                }
                Console.Write(stackSize >= 1024 ? " KB" : " B ");
                Console.WriteLine();
            }
        }
        return 0;
    }

    private static string JobStateName(int state)
    {
        switch (state)
        {
            case 0: return "queued";
            case 1: return "running";
            case 2: return "done";
            case 3: return "killed";
            default: return "?";
        }
    }

    private static string ThreadStateName(int state)
    {
        switch (state)
        {
            case 0: return "created";
            case 1: return "ready";
            case 2: return "running";
            case 3: return "blocked";
            case 4: return "suspended";
            case 5: return "terminated";
            default: return "?";
        }
    }

    private static string PadRight(string s, int width)
    {
        if (s.Length >= width)
            return s;
        var sb = new System.Text.StringBuilder(s);
        for (int i = s.Length; i < width; i++)
            sb.Append(' ');
        return sb.ToString();
    }
}
