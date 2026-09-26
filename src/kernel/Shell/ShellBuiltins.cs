// NeutrinoOS kernel - Phase 5 shell: built-in commands
//
// Built-ins execute in the shell process (they need shell state: the
// current directory, environment, aliases, history, jobs). File-text
// utilities (ls, cat, echo, ...) are external .dll utilities resolved
// through $PATH and executed via the Tier-0 JIT (Task 4 requirement);
// the built-ins here are the shell intrinsics that cannot be external:
//
//   cd pwd exit logout export unset env-like listing history
//   alias unalias source jobs fg bg kill-job help run true false gc
//
// Every built-in prints a usage line for --help (or -h).

using System;
using System.IO;
using ProtonOS.Platform;
using ProtonOS.Profiling;

namespace ProtonOS.Shell;

/// <summary>
/// Built-in command implementations. <see cref="TryRun"/> returns false
/// when the name is not a built-in (the executor then resolves it as an
/// external utility through $PATH).
/// </summary>
public static class ShellBuiltins
{
    // cd - target of the previous cd (lazy default: static string field
    // initializers trip the bflat TypePreinit pass; see Directory.cs note).
    private static string? _previousDir;

    /// <summary>
    /// Runs the built-in named args[0] when it exists; args includes the
    /// command name at index 0. Returns true when the command was a
    /// built-in (with its exit code in <paramref name="exitCode"/>).
    /// </summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        string name = args[0];

        switch (name)
        {
            case "cd": return true && RunCd(args, out exitCode);
            case "pwd": return true && RunPwd(args, out exitCode);
            case "exit": return true && RunExit(args, out exitCode);
            case "logout": return true && RunExit(args, out exitCode);
            case "export": return true && RunExport(args, out exitCode);
            case "unset": return true && RunUnset(args, out exitCode);
            case "history": return true && RunHistory(args, out exitCode);
            case "alias": return true && RunAlias(args, out exitCode);
            case "unalias": return true && RunUnalias(args, out exitCode);
            case "source": return true && RunSource(args, out exitCode);
            case "jobs": return true && RunJobs(args, out exitCode);
            case "fg": return true && RunFgBg(args, "fg", out exitCode);
            case "bg": return true && RunFgBg(args, "bg", out exitCode);
            case "help": return true && RunHelp(args, out exitCode);
            case "run": return true && RunAssembly(args, out exitCode);
            case "true": return true && Succeed(out exitCode);
            case "false": return true && Fail(out exitCode);
            case "gc": return true && RunGc(args, out exitCode);
            case "boottime": return true && RunBootTime(args, out exitCode);
            case "jitstats": return true && RunJitStats(args, out exitCode);
            case "gcstats": return true && RunGcStats(args, out exitCode);
            case "perf": return true && RunPerf(args, out exitCode);
            case "version": return true && RunVersion(args, out exitCode);
            case "poweroff": return true && RunPoweroff(args, out exitCode);
            case "reboot": return true && RunReboot(args, out exitCode);
            default: return false;
        }
    }

    private static bool Succeed(out int exitCode)
    {
        exitCode = 0;
        return true;
    }

    private static bool Fail(out int exitCode)
    {
        exitCode = 1;
        return true;
    }

    // ==================== cd / pwd ====================

    private static bool RunCd(string[] args, out int exitCode)
    {
        exitCode = 0;

        if (args.Length > 1 && (args[1] == "--help" || args[1] == "-h"))
        {
            Console.WriteLine("usage: cd [dir]");
            Console.WriteLine("  Change the current directory. 'cd' or 'cd ~' goes to $HOME,");
            Console.WriteLine("  'cd -' returns to the previous directory.");
            return true;
        }

        string target;
        if (args.Length < 2)
        {
            target = ShellState.GetVar("HOME");
            if (target.Length == 0)
                target = "/";
        }
        else
        {
            target = args[1];
        }

        if (target == "-")
        {
            target = _previousDir ?? "/";
            Console.WriteLine(target);
        }
        else if (target == "~")
        {
            target = ShellState.GetVar("HOME");
            if (target.Length == 0)
                target = "/";
        }
        else if (target.StartsWith("~/"))
        {
            string home = ShellState.GetVar("HOME");
            if (home.Length == 0)
                home = "/";
            target = home + target.Substring(1);
        }

        string resolved = Path.GetFullPath(target);
        if (!Directory.Exists(resolved))
        {
            Console.Error.WriteLine("neutrinoos: cd: " + args[args.Length > 1 ? 1 : 0] + ": no such directory");
            exitCode = 1;
            return true;
        }

        string old = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(resolved);
        ShellState.SetVar("PWD", resolved);
        _previousDir = old;
        return true;
    }

    private static bool RunPwd(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length > 1 && (args[1] == "--help" || args[1] == "-h"))
        {
            Console.WriteLine("usage: pwd");
            Console.WriteLine("  Print the current working directory.");
            return true;
        }
        Console.WriteLine(Directory.GetCurrentDirectory());
        return true;
    }

    // ==================== exit ====================

    private static bool RunExit(string[] args, out int exitCode)
    {
        int code = ShellState.LastExit;
        if (args.Length > 1)
        {
            if (!TryParseInt(args[1], out code))
            {
                Console.Error.WriteLine("neutrinoos: exit: " + args[1] + ": numeric argument required");
                code = 2;
            }
        }
        if (code < 0)
            code = 0;
        if (code > 255)
            code = 255;
        ShellState.ExitCode = code;
        ShellState.ExitRequested = true;
        exitCode = code;
        return true;
    }

    // ==================== export / unset ====================

    private static bool RunExport(string[] args, out int exitCode)
    {
        exitCode = 0;

        if (args.Length > 1 && (args[1] == "--help" || args[1] == "-h"))
        {
            Console.WriteLine("usage: export [NAME=VALUE ...]   (also: export NAME lists it)");
            Console.WriteLine("  Set an environment variable, e.g. export PS1='\\u@\\h:\\w\\$ '.");
            Console.WriteLine("  With no arguments, print the environment like env (see env.dll).");
            return true;
        }

        if (args.Length < 2)
        {
            PrintEnvironment();
            return true;
        }

        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            int eq = a.IndexOf('=');
            if (eq <= 0)
            {
                // export NAME: keep the current value (no-op when unset).
                if (ShellState.GetVar(a).Length == 0 && Environment.GetEnvironmentVariable(a) == null)
                    Console.Error.WriteLine("neutrinoos: export: " + a + ": not a valid name or NAME=VALUE");
                continue;
            }

            string varName = a.Substring(0, eq);
            string value = a.Substring(eq + 1);
            if (!IsValidName(varName))
            {
                Console.Error.WriteLine("neutrinoos: export: " + varName + ": not a valid identifier");
                exitCode = 1;
                continue;
            }
            ShellState.SetVar(varName, value);
        }
        return true;
    }

    private static bool RunUnset(string[] args, out int exitCode)
    {
        exitCode = 0;

        if (args.Length > 1 && (args[1] == "--help" || args[1] == "-h"))
        {
            Console.WriteLine("usage: unset NAME [NAME ...]");
            Console.WriteLine("  Remove environment variables.");
            return true;
        }
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: unset NAME [NAME ...]");
            exitCode = 1;
            return true;
        }
        for (int i = 1; i < args.Length; i++)
            ShellState.SetVar(args[i], null);
        return true;
    }

    private static void PrintEnvironment()
    {
        string[] names = Environment.GetEnvironmentVariableNames();
        for (int i = 0; i < names.Length; i++)
            Console.WriteLine(names[i] + "=" + ShellState.GetVar(names[i]));
    }

    private static bool IsValidName(string name)
    {
        if (name.Length == 0 || !ShellLexer.IsNameStart(name[0]))
            return false;
        for (int i = 1; i < name.Length; i++)
        {
            if (!ShellLexer.IsNameChar(name[i]))
                return false;
        }
        return true;
    }

    // ==================== history ====================

    private static bool RunHistory(string[] args, out int exitCode)
    {
        exitCode = 0;

        if (args.Length > 1 && (args[1] == "--help" || args[1] == "-h"))
        {
            Console.WriteLine("usage: history [N]");
            Console.WriteLine("  Print the command history (persisted in /.history; 32 entries).");
            return true;
        }

        int count = ProtonOS.Platform.LineDiscipline.GetHistoryCount();
        int first = 1;
        if (args.Length > 1 && TryParseInt(args[1], out int n) && n >= 0 && n < count)
            first = count - n + 1;

        var buffer = new char[LineDiscipline.LineCapacity];
        for (int i = first - 1; i < count; i++)
        {
            int len = LineDiscipline.GetHistoryEntry(i, buffer);
            Console.Write("  ");
            Console.Write(i + 1);
            Console.Write("  ");
            Console.WriteLine(new string(buffer, 0, len));
        }
        return true;
    }

    // ==================== alias / unalias ====================

    private static bool RunAlias(string[] args, out int exitCode)
    {
        exitCode = 0;

        if (args.Length > 1 && (args[1] == "--help" || args[1] == "-h"))
        {
            Console.WriteLine("usage: alias [name=command]");
            Console.WriteLine("  Define or list aliases, e.g. alias ll='ls -l'.");
            return true;
        }

        if (args.Length < 2)
        {
            int aliases = ShellState.AliasCount;
            if (aliases == 0)
            {
                Console.WriteLine("(no aliases)");
                return true;
            }
            for (int i = 0; i < aliases; i++)
            {
                ShellState.GetAliasAt(i, out string? aliasName, out string? aliasValue);
                Console.WriteLine(aliasName + "='" + aliasValue + "'");
            }
            return true;
        }

        for (int i = 1; i < args.Length; i++)
        {
            int eq = args[i].IndexOf('=');
            if (eq <= 0)
            {
                // alias NAME: print its value when set.
                string? value = ShellState.GetAlias(args[i]);
                if (value == null)
                {
                    Console.Error.WriteLine("neutrinoos: alias: " + args[i] + ": not found");
                    exitCode = 1;
                }
                else
                {
                    Console.WriteLine(args[i] + "='" + value + "'");
                }
                continue;
            }
            string aliasName = args[i].Substring(0, eq);
            string aliasValue = args[i].Substring(eq + 1);
            if (!ShellState.SetAlias(aliasName, aliasValue))
            {
                Console.Error.WriteLine("neutrinoos: alias: table full (max 16)");
                exitCode = 1;
            }
        }
        return true;
    }

    private static bool RunUnalias(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: unalias name [name ...]");
            exitCode = 1;
            return true;
        }
        for (int i = 1; i < args.Length; i++)
        {
            if (!ShellState.RemoveAlias(args[i]))
            {
                Console.Error.WriteLine("neutrinoos: unalias: " + args[i] + ": not found");
                exitCode = 1;
            }
        }
        return true;
    }

    // ==================== source ====================

    private static bool RunSource(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 2 || args[1] == "--help" || args[1] == "-h")
        {
            Console.WriteLine("usage: source file");
            Console.WriteLine("  Execute commands from a file (one per line; # comments are skipped).");
            return true;
        }

        string path = Path.GetFullPath(args[1]);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("neutrinoos: source: " + args[1] + ": no such file");
            exitCode = 1;
            return true;
        }

        string[] lines = File.ReadAllLines(path);
        int last = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            last = ShellExecutor.ExecuteLine(line);
        }
        exitCode = last;
        return true;
    }

    // ==================== jobs / fg / bg ====================

    private static bool RunJobs(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length > 1 && (args[1] == "--help" || args[1] == "-h"))
        {
            Console.WriteLine("usage: jobs");
            Console.WriteLine("  List background jobs (started with &; see docs/PHASE5-SHELL.md).");
            return true;
        }
        JobManager.PrintJobs();
        return true;
    }

    private static bool RunFgBg(string[] args, string which, out int exitCode)
    {
        exitCode = 0;
        Console.Error.WriteLine("neutrinoos: " + which + ": job control into the foreground is not supported in Phase 5");
        Console.Error.WriteLine("  (background jobs run cooperatively while the shell waits for input; see docs/PHASE5-SHELL.md)");
        exitCode = 1;
        return true;
    }

    // ==================== help ====================

    private static bool RunHelp(string[] args, out int exitCode)
    {
        exitCode = 0;

        if (args.Length > 1)
        {
            string cmd = args[1];
            string? text = GetBuiltinHelp(cmd);
            if (text != null)
            {
                Console.WriteLine(text);
                return true;
            }
            Console.WriteLine(cmd + " is an external utility; run '" + cmd + " --help'");
            return true;
        }

        Console.WriteLine("NeutrinoOS shell (Phase 5) - built-in commands:");
        Console.WriteLine("  cd pwd exit logout export unset history alias unalias source");
        Console.WriteLine("  jobs fg bg help run true false gc poweroff reboot");
        Console.WriteLine();
        Console.WriteLine("External utilities resolve through $PATH (default /bin:/apps):");
        Console.WriteLine("  ls cat echo mkdir rm cp mv touch head tail wc grep find");
        Console.WriteLine("  ps kill sleep df mount umount uname date uptime free env");
        Console.WriteLine("  ifconfig dhcp ping dns netstat wget curl ssh");
        Console.WriteLine();
        Console.WriteLine("Operators:  |   >   >>   <   2>   2>>   ;   &&   ||   &");
        Console.WriteLine("Quoting:    '...' (literal)   \"...\" (escapes + $VARS)   \\x");
        Console.WriteLine("Variables:  $VAR  ${VAR}  $? (last exit)  $$ (shell pid)");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  ls /apps | wc -l");
        Console.WriteLine("  echo hello world > /test.txt");
        Console.WriteLine("  cat /test.txt");
        Console.WriteLine("  sleep 2 &");
        return true;
    }

    private static string? GetBuiltinHelp(string cmd)
    {
        switch (cmd)
        {
            case "cd": return "usage: cd [dir] (cd ~ | cd -) - change directory";
            case "pwd": return "usage: pwd - print the working directory";
            case "exit": return "usage: exit [code] - end the shell session";
            case "logout": return "usage: logout - end the shell session";
            case "export": return "usage: export NAME=VALUE [NAME=VALUE ...] - set environment variables";
            case "unset": return "usage: unset NAME [NAME ...] - remove environment variables";
            case "history": return "usage: history [N] - print command history";
            case "alias": return "usage: alias [name='command'] - define or list aliases";
            case "unalias": return "usage: unalias name - remove an alias";
            case "source": return "usage: source file - execute commands from a file";
            case "jobs": return "usage: jobs - list background jobs";
            case "fg": return "fg - not supported in Phase 5 (see docs/PHASE5-SHELL.md)";
            case "bg": return "bg - not supported in Phase 5 (see docs/PHASE5-SHELL.md)";
            case "help": return "usage: help [command] - show help";
            case "run": return "usage: run <path.dll> [args...] - run a .NET assembly (Phase 4 compatible)";
            case "true": return "usage: true - exit with status 0";
            case "false": return "usage: false - exit with status 1";
            case "gc": return "usage: gc - trigger a garbage collection and print statistics";
            case "boottime": return "usage: boottime - print the recorded boot timeline";
            case "jitstats": return "usage: jitstats [reset] - Tier-0 JIT compile statistics";
            case "gcstats": return "usage: gcstats - heap and pause statistics for the garbage collector";
            case "perf": return "usage: perf start|stop|reset|dump - kernel sampling profiler (see /dev/profiler)";
            case "version": return "usage: version - print the NeutrinoOS version string";
            case "poweroff": return "usage: poweroff - shut down the system via ACPI S5 (Phase 9)";
            case "reboot": return "usage: reboot - reset the system via ACPI/PCI reset (Phase 9)";
            default: return null;
        }
    }

    // ==================== run (Phase 4 compatibility) ====================

    private static bool RunAssembly(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: run <path.dll> [args...]");
            exitCode = 1;
            return true;
        }

        string path = Path.GetFullPath(args[1]);
        if (!File.Exists(path) && !path.EndsWith(".dll"))
        {
            string withExt = path + ".dll";
            if (File.Exists(withExt))
                path = withExt;
        }

        string[] progArgs = new string[args.Length - 2];
        for (int i = 2; i < args.Length; i++)
            progArgs[i - 2] = args[i];

        int rc = AssemblyRunner.Run(path, progArgs);
        exitCode = rc < 0 ? ShellExecutor.ExitNotExecutable : rc;
        return true;
    }

    // ==================== gc ====================

    private static bool RunGc(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length > 1 && (args[1] == "--help" || args[1] == "-h"))
        {
            Console.WriteLine("usage: gc");
            Console.WriteLine("  Trigger a garbage collection and print heap statistics");
            Console.WriteLine("  (heap size, collection count, collection duration).");
            return true;
        }
        KernelGc.ReportAndCollect();
        return true;
    }

    // ============ Phase 7: boottime / jitstats / gcstats / perf / version ============

    /// <summary>Prints the NeutrinoOS version string (Phase 7).</summary>
    private static bool RunVersion(string[] args, out int exitCode)
    {
        exitCode = 0;
        Console.WriteLine(ProtonOS.Exports.DDK.SystemInfoExports.VersionString);
        return true;
    }

    // ==================== Phase 9: poweroff / reboot ====================

    /// <summary>
    /// Shuts the machine down via ACPI S5 (Phase 9). Prints the detection
    /// summary, writes PM1_CNT and never returns; exit code 1 with a
    /// message when the platform has no ACPI S5 mechanism.
    /// </summary>
    private static bool RunPoweroff(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length > 1 && (args[1] == "--help" || args[1] == "-h"))
        {
            Console.WriteLine("usage: poweroff");
            Console.WriteLine("  Shut down the system via ACPI S5 (PM1_CNT write with");
            Console.WriteLine("  the \\\\_S5 SLP_TYP values from the DSDT).");
            return true;
        }

        Console.WriteLine("Shutting down NeutrinoOS (ACPI S5)...");
        if (!ProtonOS.Platform.PowerManagement.IsAvailable)
        {
            ProtonOS.Platform.PowerManagement.Initialize();
            if (!ProtonOS.Platform.PowerManagement.IsAvailable)
            {
                Console.Error.WriteLine("poweroff: ACPI S5 is not available on this machine");
                exitCode = 1;
                return true;
            }
        }
        ProtonOS.Platform.PowerManagement.PowerOff();
        exitCode = 0;
        return true;
    }

    /// <summary>
    /// Resets the machine (Phase 9): FADT reset register, then 0xCF9,
    /// then the keyboard-controller reset. Does not return.
    /// </summary>
    private static bool RunReboot(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length > 1 && (args[1] == "--help" || args[1] == "-h"))
        {
            Console.WriteLine("usage: reboot");
            Console.WriteLine("  Reset the system: ACPI FADT reset register when present,");
            Console.WriteLine("  otherwise the 0xCF9 PCI reset and the 8042 pulse.");
            return true;
        }

        Console.WriteLine("Rebooting NeutrinoOS...");
        ProtonOS.Platform.PowerManagement.Reboot();
        exitCode = 0;
        return true;
    }

    /// <summary>Prints the recorded boot timeline (Phase 7 boot profiling).</summary>
    private static bool RunBootTime(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length > 1 && (args[1] == "--help" || args[1] == "-h"))
        {
            Console.WriteLine("usage: boottime");
            Console.WriteLine("  Print the boot stage timeline recorded by BootLog");
            Console.WriteLine("  (one line per stage, milliseconds since boot).");
            return true;
        }
        StringWriter sw = new StringWriter();
        BootLog.FormatTimeline(sw);
        Console.Write(sw.ToString());
        return true;
    }

    /// <summary>Prints Tier-0 JIT compile statistics (Phase 7); "reset" clears them.</summary>
    private static bool RunJitStats(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length > 1 && (args[1] == "--help" || args[1] == "-h"))
        {
            Console.WriteLine("usage: jitstats [reset]");
            Console.WriteLine("  JIT compile counters: methods, wall time, native code size,");
            Console.WriteLine("  slowest compilations. 'reset' clears the counters.");
            return true;
        }
        if (args.Length > 1 && args[1] == "reset")
        {
            JitStats.Reset();
            JitStats.Count = Runtime.JitDiag.CompiledMethods;
            Console.WriteLine("[jitstats] counters reset (method count re-synced)");
            return true;
        }
        JitStats.Count = Runtime.JitDiag.CompiledMethods;
        StringWriter sw = new StringWriter();
        JitStats.Format(sw);
        Console.Write(sw.ToString());
        return true;
    }

    /// <summary>Prints GC heap and pause statistics (Phase 7).</summary>
    private static bool RunGcStats(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length > 1 && (args[1] == "--help" || args[1] == "-h"))
        {
            Console.WriteLine("usage: gcstats");
            Console.WriteLine("  Garbage collector statistics: SOH/LOH heap usage, live");
            Console.WriteLine("  object counts, collection count and pause times.");
            return true;
        }

        Memory.GCHeap.GetStats(out ulong sohUsed, out ulong objects, out ulong free);
        Memory.GCHeap.GetLOHStats(out ulong lohUsed, out ulong lohObjects);
        Memory.GC.GetExtendedStats(out ulong collections, out ulong lastPauseMs, out ulong totalPauseMs);

        Console.WriteLine("[gcstats] NeutrinoOS garbage collector");
        Console.Write("[gcstats] SOH: ");
        Console.Write((long)(sohUsed / 1024));
        Console.Write(" KB used (");
        Console.Write((long)objects);
        Console.Write(" objects), ");
        Console.Write((long)(free / 1024));
        Console.WriteLine(" KB free");
        Console.Write("[gcstats] LOH: ");
        Console.Write((long)(lohUsed / 1024));
        Console.Write(" KB (");
        Console.Write((long)lohObjects);
        Console.WriteLine(" objects)");
        Console.Write("[gcstats] collections: ");
        Console.Write((long)collections);
        Console.Write("   last pause: ");
        Console.Write((long)lastPauseMs);
        Console.Write(" ms   total pause: ");
        Console.Write((long)totalPauseMs);
        Console.WriteLine(" ms");
        return true;
    }

    /// <summary>Controls the kernel sampling profiler (Phase 7).</summary>
    private static bool RunPerf(string[] args, out int exitCode)
    {
        exitCode = 0;
        string sub = args.Length > 1 ? args[1] : "dump";
        if (sub == "--help" || sub == "-h")
        {
            Console.WriteLine("usage: perf start|stop|reset|dump");
            Console.WriteLine("  Kernel sampling profiler: samples the interrupted RIP on");
            Console.WriteLine("  every LAPIC timer tick (1 kHz). 'dump' prints the hottest");
            Console.WriteLine("  64-byte code ranges with nearest JIT symbols.");
            Console.WriteLine("  The same report is readable from /dev/profiler.");
            return true;
        }
        if (sub == "start")
        {
            Profiler.Start();
            Console.WriteLine("[perf] sampling started (1 kHz); run 'perf stop' then 'perf dump'");
            return true;
        }
        if (sub == "stop")
        {
            Profiler.Stop();
            Console.Write("[perf] sampling stopped; samples=");
            Console.WriteLine((long)Profiler.SampleCount);
            return true;
        }
        if (sub == "reset")
        {
            Profiler.Reset();
            Console.WriteLine("[perf] samples cleared");
            return true;
        }
        if (sub != "dump")
        {
            Console.Error.WriteLine("perf: unknown subcommand '" + sub + "' (start|stop|reset|dump)");
            exitCode = 1;
            return true;
        }
        StringWriter sw = new StringWriter();
        Profiler.Format(sw);
        Console.Write(sw.ToString());
        return true;
    }

    // ==================== helpers ====================

    private static bool TryParseInt(string s, out int value)
    {
        value = 0;
        if (s.Length == 0)
            return false;
        int i = 0;
        bool negative = false;
        if (s[0] == '-')
        {
            negative = true;
            i = 1;
            if (s.Length == 1)
                return false;
        }
        int result = 0;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9')
                return false;
            result = result * 10 + (c - '0');
            if (result > 1000000000)
                return false;
        }
        value = negative ? -result : result;
        return true;
    }
}
