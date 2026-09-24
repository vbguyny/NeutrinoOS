// NeutrinoOS Phase 6 utility: webhost - start/stop the web host service
//
// Usage: webhost [start|stop|status]
//
// The HTTP/1.1 + TLS 1.3 server lives in the DDK
// (ProtonOS.DDK.Services.WebService); this shim asks the kernel service
// registry to start it. The kernel calls the service's Tick() from the
// shell idle hook, so the server runs cooperatively without extra
// threads.
//
// Note: hosting full ASP.NET Core / Kestrel apps is a Phase 7+ goal
// (see docs/PHASE6-WEB.md); this service is the Phase 6 fallback server
// with the same simple routing surface (/, /health, /time, static files).

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Services;

namespace NeutrinoOS.Utility.Webhost;

/// <summary>The webhost utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        string cmd = args.Length > 0 ? args[0] : "start";

        if (cmd == "--help" || cmd == "-h" || cmd == "help")
        {
            Console.WriteLine("usage: webhost [start|stop|status]");
            Console.WriteLine("  start   start the built-in HTTP/HTTPS server (ports 80/443)");
            Console.WriteLine("  stop    stop the server");
            Console.WriteLine("  status  show whether the server is running");
            Console.WriteLine("config: /etc/webhost.conf (Port=, HttpsPort=)");
            Console.WriteLine("certs:  /etc/ssl/certs/neutrinoos.crt + /etc/ssl/private/neutrinoos.key");
            Console.WriteLine("static: /var/www");
            return 0;
        }

        if (cmd == "stop")
        {
            int rc = Services.Stop("webhost");
            Console.WriteLine(rc == 0 ? "[web] service stopped" : "[web] not running");
            return rc == 0 ? 0 : 1;
        }

        if (cmd == "status")
        {
            Console.Write("[web] ");
            Console.WriteLine(WebService.Active ? "running" : "not running");
            return 0;
        }

        if (cmd != "start")
        {
            Console.WriteLine("usage: webhost [start|stop|status]");
            return 1;
        }

        int result = Services.Start("webhost");
        if (result == 0)
        {
            Console.WriteLine("[web] service started (driven by the kernel idle tick)");
            return 0;
        }

        Console.Write("[web] failed to start (code ");
        Console.Write(Util.PadLeft(result, 1));
        Console.WriteLine(")");
        if (result == -30)
            Console.WriteLine("       no eth0 - run with a virtio NIC, then configure with dhcp");
        else if (result == -32)
            Console.WriteLine("       could not bind the HTTP port (already in use?)");
        return 1;
    }
}
