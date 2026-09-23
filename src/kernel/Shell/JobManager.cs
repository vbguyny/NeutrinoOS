// NeutrinoOS kernel - Phase 5 shell: background job manager
//
// Phase 5 has no preemptive processes (the Tier-0 JIT executes
// assemblies in kernel mode; JIT compilation is not reentrant and the
// kernel GC scans the boot thread's stack), so background jobs are
// cooperative:
//
//   - `cmd &` registers a job and returns immediately (the shell prints
//     a job banner with the job id and PID).
//   - The job executes while the shell is idle waiting for input: the
//     REPL's read loop calls JobManager.Pump() between keystrokes
//     (LineDiscipline.IdleHook), so a `sleep 2 &` job runs during the
//     next prompt wait without blocking command entry.
//   - `jobs` lists the job table with state and exit code.
//   - `kill <pid>` cancels a queued job or requests cancellation of the
//     running job (cooperative: the job's exit code is reported as 130;
//     a job already inside JIT code cannot be interrupted - documented
//     limitation, see docs/PHASE5-SHELL.md).
//
// Only single-command pipelines without pipes/redirections can be
// backgrounded in Phase 5; anything else is rejected with a clear
// message (documented).

using System;

namespace ProtonOS.Shell;

/// <summary>Background job states (see file header).</summary>
public enum ShellJobState : byte
{
    /// <summary>Registered, waiting for the shell to go idle.</summary>
    Queued = 0,
    /// <summary>Executing on the shell thread while it waits for input.</summary>
    Running,
    /// <summary>Finished; <c>ExitCode</c> is valid.</summary>
    Done,
    /// <summary>Cancelled by kill before it ran (or while it ran: exit 130).</summary>
    Killed
}

/// <summary>The job table entry (kernel-side copy; jobs built-in prints it).</summary>
public struct ShellJob
{
    /// <summary>Job number (1-based, shown by jobs and used by kill).</summary>
    public int Id;

    /// <summary>NeutrinoOS "PID" for the job (kernel has no per-job pid; 1000+id).</summary>
    public int Pid;

    /// <summary>Rendered command line.</summary>
    public string Command;

    /// <summary>Current state.</summary>
    public ShellJobState State;

    /// <summary>Exit code (valid for Done/Killed).</summary>
    public int ExitCode;

    /// <summary>Cancellation requested (cooperative).</summary>
    public bool KillRequested;

    /// <summary>Program path resolved at start time.</summary>
    public string ExePath;

    /// <summary>Program arguments.</summary>
    public string[] Args;
}

/// <summary>
/// Cooperative background job manager (see file header).
/// </summary>
public static class JobManager
{
    /// <summary>Maximum simultaneous jobs.</summary>
    public const int MaxJobs = 8;

    /// <summary>Base "PID" reported for background jobs.</summary>
    private const int PidBase = 1000;

    private static readonly ShellJob[] _jobs = new ShellJob[MaxJobs];
    private static int _jobCount;

    /// <summary>True when at least one job is Queued (used by the idle pump).</summary>
    public static bool HasQueuedJobs
    {
        get
        {
            for (int i = 0; i < _jobCount; i++)
            {
                if (_jobs[i].State == ShellJobState.Queued)
                    return true;
            }
            return false;
        }
    }

    /// <summary>Number of job table entries (used by the ps/kill exports).</summary>
    public static int Count => _jobCount;

    /// <summary>
    /// Returns the command line for job <paramref name="index"/> and its
    /// id/pid/state/exit code (used by the ps/kill kernel exports); null
    /// for a bad index.
    /// </summary>
    public static string? GetJobInfo(int index, out int id, out int pid, out int state, out int exitCode)
    {
        id = 0;
        pid = 0;
        state = 0;
        exitCode = 0;
        if (index < 0 || index >= _jobCount)
            return null;

        ShellJob job = _jobs[index];
        id = job.Id;
        pid = job.Pid;
        state = (int)job.State;
        exitCode = job.ExitCode;
        return job.Command;
    }

    /// <summary>
    /// Silent cooperative kill for the kill utility export: queued jobs
    /// are cancelled, running jobs get a cancellation request. Returns 0
    /// when a job was affected, -1 when the id/pid is unknown.
    /// </summary>
    public static int KillForExport(int idOrPid)
    {
        for (int i = 0; i < _jobCount; i++)
        {
            if (_jobs[i].Id == idOrPid || _jobs[i].Pid == idOrPid)
            {
                if (_jobs[i].State == ShellJobState.Queued)
                {
                    _jobs[i].State = ShellJobState.Killed;
                    _jobs[i].ExitCode = 130;
                    _jobs[i].KillRequested = true;
                    return 0;
                }
                if (_jobs[i].State == ShellJobState.Running)
                {
                    _jobs[i].KillRequested = true;
                    return 0;
                }
                return -1;
            }
        }
        return -1;
    }

    /// <summary>
    /// Registers a background pipeline. Phase 5 supports a single external
    /// command (no pipes, no redirections); other shapes are rejected with
    /// a clear message and exit code 1 (documented limitation).
    /// </summary>
    public static int StartBackground(ShellPipeline pipeline)
    {
        if (pipeline.CommandCount != 1 || pipeline.Commands[0].RedirCount != 0)
        {
            Console.Error.WriteLine("neutrinoos: background pipelines with pipes or redirections are not supported (Phase 5 limitation)");
            return 1;
        }

        if (_jobCount >= MaxJobs)
        {
            Console.Error.WriteLine("neutrinoos: too many background jobs (max 8)");
            return 1;
        }

        ShellCommand cmd = pipeline.Commands[0];
        string[] args = new string[cmd.ArgCount];
        for (int i = 0; i < cmd.ArgCount; i++)
            args[i] = cmd.Args[i];

        string name = args[0];
        string? exe = ShellState.FindExecutable(name);
        if (exe == null)
        {
            Console.Error.WriteLine("neutrinoos: " + name + ": command not found");
            return ShellExecutor.ExitCommandNotFound;
        }

        string[] progArgs = new string[args.Length - 1];
        for (int i = 1; i < args.Length; i++)
            progArgs[i - 1] = args[i];

        int id = NextJobId();
        int slot = _jobCount;

        // Field-by-field store: a whole-struct copy into an array element
        // of a struct containing GC references needs the RhpByRefAssignRef
        // helper, which the kernel runtime does not link. Individual stfld
        // writes are fine.
        _jobs[slot].Id = id;
        _jobs[slot].Pid = PidBase + id;
        _jobs[slot].Command = RenderCommandLine(args);
        _jobs[slot].State = ShellJobState.Queued;
        _jobs[slot].ExitCode = 0;
        _jobs[slot].KillRequested = false;
        _jobs[slot].ExePath = exe;
        _jobs[slot].Args = progArgs;
        _jobCount++;

        Console.Write("[jobs] [");
        Console.Write(id);
        Console.Write("] pid ");
        Console.Write(PidBase + id);
        Console.Write(" started: ");
        Console.WriteLine(_jobs[slot].Command);
        return 0;
    }

    private static int NextJobId()
    {
        int max = 0;
        for (int i = 0; i < _jobCount; i++)
        {
            if (_jobs[i].Id > max)
                max = _jobs[i].Id;
        }
        return max + 1;
    }

    private static string RenderCommandLine(string[] args)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0)
                sb.Append(' ');
            sb.Append(args[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Runs one queued job to completion. Called by the REPL between
    /// keystrokes (LineDiscipline idle hook) - never re-entered because
    /// jobs cannot themselves wait for input (documented limitation).
    /// </summary>
    public static void Pump()
    {
        for (int i = 0; i < _jobCount; i++)
        {
            if (_jobs[i].State != ShellJobState.Queued)
                continue;

            _jobs[i].State = ShellJobState.Running;
            bool killRequested = _jobs[i].KillRequested;

            int rc = 130;
            if (!killRequested)
            {
                rc = ProtonOS.Platform.AssemblyRunner.Run(_jobs[i].ExePath, _jobs[i].Args);
                if (rc < 0)
                    rc = ShellExecutor.ExitNotExecutable;
            }
            if (_jobs[i].KillRequested)
                rc = 130;

            _jobs[i].State = _jobs[i].KillRequested ? ShellJobState.Killed : ShellJobState.Done;
            _jobs[i].ExitCode = rc;

            Console.Write("[jobs] [");
            Console.Write(_jobs[i].Id);
            Console.Write("] ");
            Console.Write(_jobs[i].State == ShellJobState.Killed ? "killed" : "done");
            Console.Write(" (exit ");
            Console.Write(rc);
            Console.WriteLine(")");
            return;     // one job per idle window
        }
    }

    /// <summary>Prints the job table (jobs built-in).</summary>
    public static void PrintJobs()
    {
        if (_jobCount == 0)
        {
            Console.WriteLine("(no jobs)");
            return;
        }

        for (int i = 0; i < _jobCount; i++)
        {
            ShellJob job = _jobs[i];
            Console.Write("  [");
            Console.Write(job.Id);
            Console.Write("] pid ");
            Console.Write(job.Pid);
            Console.Write("  ");
            Console.Write(StateName(job.State));
            if (job.State == ShellJobState.Done || job.State == ShellJobState.Killed)
            {
                Console.Write(" (exit ");
                Console.Write(job.ExitCode);
                Console.Write(")");
            }
            Console.Write("  ");
            Console.WriteLine(job.Command);
        }
    }

    private static string StateName(ShellJobState state)
    {
        switch (state)
        {
            case ShellJobState.Queued: return "queued";
            case ShellJobState.Running: return "running";
            case ShellJobState.Killed: return "killed";
            default: return "done";
        }
    }

    /// <summary>
    /// Cooperative kill: a queued job is cancelled before it runs; a
    /// running job gets a cancellation request (applied when it returns).
    /// Accepts the job id or the printed pid. Returns 0 on success.
    /// </summary>
    public static int Kill(int idOrPid)
    {
        for (int i = 0; i < _jobCount; i++)
        {
            if (_jobs[i].Id == idOrPid || _jobs[i].Pid == idOrPid)
            {
                if (_jobs[i].State == ShellJobState.Queued)
                {
                    _jobs[i].State = ShellJobState.Killed;
                    _jobs[i].ExitCode = 130;
                    _jobs[i].KillRequested = true;
                    Console.Write("[jobs] [");
                    Console.Write(_jobs[i].Id);
                    Console.WriteLine("] killed (was queued)");
                    return 0;
                }
                if (_jobs[i].State == ShellJobState.Running)
                {
                    _jobs[i].KillRequested = true;
                    Console.WriteLine("neutrinoos: kill: running jobs complete cooperatively (Phase 5 limitation)");
                    return 0;
                }
                Console.WriteLine("neutrinoos: kill: job already finished");
                return 1;
            }
        }
        Console.Error.WriteLine("neutrinoos: kill: no such job (use jobs to list)");
        return 1;
    }
}
