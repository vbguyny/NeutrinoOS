// NeutrinoOS Phase 5 utility: kill - signal a background job
//
// usage: kill [-9] pid
//   pid is a shell background job's PID (or its job id, as listed by
//   jobs/ps). The kernel's job table implements cooperative
//   cancellation: queued jobs are cancelled outright, running jobs get
//   a cancellation request applied when they return (Phase 5 has no
//   preemptive processes; see docs/PHASE5-SHELL.md).

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Kernel;

namespace NeutrinoOS.Utility.Kill;

/// <summary>The kill utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 for invalid arguments or unknown jobs.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        bool force = false;
        var targets = new System.Collections.Generic.List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: kill [-9] pid",
                    "  Signal a shell background job (job id or PID from jobs/ps).",
                    "  Cancellation is cooperative in Phase 5; -9 is accepted.");
            }
            if (a == "-9")
                force = true;
            else
                targets.Add(a);
        }

        if (targets.Count == 0)
            return Util.Fail("kill", "usage: kill [-9] pid");

        int rc = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            if (!Util.TryParseInt(targets[i], out int pid))
            {
                rc = Util.Fail("kill", targets[i] + ": invalid pid");
                continue;
            }

            int result = SysInfo.KillShellJob(pid);
            if (result == 0)
            {
                Console.Write("[kill] job ");
                Console.Write(pid);
                Console.WriteLine(force ? " cancelled (SIGKILL request)" : " cancelled (SIGTERM request)");
            }
            else
            {
                rc = Util.Fail("kill", targets[i] + ": no such job (only shell background jobs can be signalled)");
            }
        }
        return rc;
    }
}
