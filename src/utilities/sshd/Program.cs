// NeutrinoOS Phase 6 utility: sshd - start/stop the SSH server service
//
// Usage: sshd [start|stop|status]
//
// The SSH server itself lives in the DDK (ProtonOS.DDK.Services.
// SshService); this shim asks the kernel service registry to start it.
// The kernel then calls the service's Tick() from the shell idle hook,
// so the daemon runs cooperatively without extra threads - typing at
// the local console keeps remote sessions alive, exactly like the
// network utilities pump the stack while they run.

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Services;

namespace NeutrinoOS.Utility.Sshd;

/// <summary>The sshd utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        string cmd = args.Length > 0 ? args[0] : "start";

        if (cmd == "stop")
        {
            int rc = Services.Stop("sshd");
            Console.WriteLine(rc == 0 ? "[sshd] service stopped" : "[sshd] not running");
            return rc == 0 ? 0 : 1;
        }

        if (cmd == "status")
        {
            Console.Write("[sshd] ");
            Console.WriteLine(SshService.Active ? "running" : "not running");
            return 0;
        }

        if (cmd != "start")
        {
            Console.WriteLine("usage: sshd [start|stop|status]");
            return 1;
        }

        int result = Services.Start("sshd");
        if (result == 0)
        {
            Console.WriteLine("[sshd] service started (driven by the kernel idle tick)");
            return 0;
        }

        Console.Write("[sshd] failed to start (code ");
        Console.Write(Util.PadLeft(result, 1));
        Console.WriteLine(")");
        if (result == -20)
            Console.WriteLine("       no eth0 - run with a virtio NIC, then configure with dhcp");
        else if (result == -22)
            Console.WriteLine("       could not bind port 22 (already in use?)");
        return 1;
    }
}
