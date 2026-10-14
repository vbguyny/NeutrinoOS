// NeutrinoOS utility: webhost - start/stop the web host service, or
// launch a hosted web app DLL (Kestrel port M4).
//
// Usage: webhost [start|stop|status]
//        webhost <app.dll> [args...]        (e.g. /apps/webapp/webapp.dll)
//
// start/stop/status manage the built-in HTTP/1.1 + TLS 1.3 server
// (NeutrinoOS.DDK.Services.WebService), driven by the kernel idle tick.
//
// The app form launches a web app DLL as a cooperative hosted app: the
// kernel loads the assembly, runs its Main in hosted mode (the app
// builds a WebApplication and calls app.Run()), and then ticks the
// registered app from the shell idle hook - the app stays reachable
// while the shell remains usable. Bare names resolve to
// /apps/webapp/<name>.dll.
//
// Note: hosting full ASP.NET Core / Kestrel is the Kestrel-port roadmap
// (docs/KESTREL-PORT.md); this launcher hosts the DDK WebApplication
// surface today.

using System;
using NeutrinoOS.Utils;
using NeutrinoOS.DDK.Kernel;
using NeutrinoOS.DDK.Services;
using NeutrinoOS.DDK.Web;

namespace NeutrinoOS.Utility.Webhost;

/// <summary>The webhost utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;

        // M4: `webhost <app.dll> [args...]` launches a hosted web app.
        if (args.Length > 0 && IsAppPath(args[0]))
            return RunApp(args);

        string cmd = args.Length > 0 ? args[0] : "start";

        if (cmd == "--help" || cmd == "-h" || cmd == "help")
        {
            Console.WriteLine("usage: webhost [start|stop|status]");
            Console.WriteLine("       webhost <app.dll> [args...]  launch a hosted web app");
            Console.WriteLine("  start        start the built-in HTTP/HTTPS server (ports 80/443)");
            Console.WriteLine("  stop         stop the built-in server and all hosted apps");
            Console.WriteLine("  status       show server + hosted-app state");
            Console.WriteLine("  <app.dll>    launch a web app DLL as a cooperative hosted app");
            Console.WriteLine("               (bare names resolve to /apps/webapp/<name>.dll)");
            Console.WriteLine("config: /etc/webhost.conf (Port=, HttpsPort=)");
            Console.WriteLine("certs:  /etc/ssl/certs/neutrinoos.crt + /etc/ssl/private/neutrinoos.key");
            Console.WriteLine("static: /var/www");
            return 0;
        }

        if (cmd == "stop")
        {
            int rcApp = Services.Stop("webapp");
            int rcWeb = Services.Stop("webhost");
            Console.WriteLine(rcApp == 0 || rcWeb == 0 ? "[web] services stopped" : "[web] not running");
            return rcApp == 0 || rcWeb == 0 ? 0 : 1;
        }

        if (cmd == "status")
        {
            Console.Write("[web] built-in server: ");
            Console.WriteLine(WebService.Active ? "running" : "not running");
            Console.Write("[web] hosted apps: ");
            Console.WriteLine(WebAppRuntime.GetHostedCount().ToString());
            return 0;
        }

        if (cmd != "start")
        {
            Console.WriteLine("usage: webhost [start|stop|status] | <app.dll> [args...]");
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

    /// <summary>True when the argument looks like a web app DLL to launch.</summary>
    private static bool IsAppPath(string arg)
    {
        if (arg == null || arg.Length == 0)
            return false;
        // ".dll" suffix or an explicit path.
        if (arg.Length >= 4 &&
            arg[arg.Length - 4] == '.' && arg[arg.Length - 3] == 'd' &&
            arg[arg.Length - 2] == 'l' && arg[arg.Length - 1] == 'l')
            return true;
        for (int i = 0; i < arg.Length; i++)
        {
            if (arg[i] == '/')
                return true;
        }
        return false;
    }

    /// <summary>Launch a web app DLL as a cooperative hosted app (M4).</summary>
    private static int RunApp(string[] args)
    {
        string path = args[0];
        bool hasSlash = false;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '/')
            {
                hasSlash = true;
                break;
            }
        }
        if (!hasSlash)
            path = "/apps/webapp/" + path;   // default deployment directory

        var appArgs = new string[args.Length - 1];
        for (int i = 1; i < args.Length; i++)
            appArgs[i - 1] = args[i];

        Console.Write("[webhost] launching ");
        Console.WriteLine(path);
        int rc = Services.StartApp(path, appArgs);
        if (rc == 0)
        {
            Console.WriteLine("[webhost] app started (cooperative; kernel idle tick drives it)");
            return 0;
        }

        Console.Write("[webhost] app launch failed (code ");
        Console.Write(Util.PadLeft(rc, 1));
        Console.WriteLine(")");
        if (rc == -7)
            Console.WriteLine("       the app did not call app.Run() - is it a WebApplication app?");
        else if (rc == -30)
            Console.WriteLine("       no eth0 - run with a virtio NIC, then configure with dhcp");
        else if (rc == -32)
            Console.WriteLine("       could not bind the app's port (already in use?)");
        return 1;
    }
}
