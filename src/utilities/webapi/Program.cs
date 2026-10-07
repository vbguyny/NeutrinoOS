// NeutrinoOS sample utility: webapi - start/stop the WebAPI-style sample app
//
// Usage: webapi [start|stop|status]
//
// Starts the TasksWebApp sample (src/ddk/Samples/TasksWebApp.cs), the
// WebApplication/MapGet-style version of the task-tracker REST API - the
// shape the Kestrel port targets (docs/KESTREL-PORT.md). The kernel
// JIT-compiles the app from the DDK assembly and drives its Tick() from
// the shell idle hook, exactly like the other services.
//
// The API listens on port 8080. Reach it from Windows 11 with:
//   qemu ... -netdev user,id=n0,hostfwd=tcp::18080-:8080
//   curl http://127.0.0.1:18080/api/v1/info
//
// Docs + Postman collection: docs/samples/webapi/README.md

using System;
using NeutrinoOS.Utils;
using NeutrinoOS.DDK.Kernel;
using NeutrinoOS.DDK.Samples;
using NeutrinoOS.DDK.Services;

namespace NeutrinoOS.Utility.Webapi;

/// <summary>The webapi utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        string cmd = args.Length > 0 ? args[0] : "start";

        if (cmd == "--help" || cmd == "-h" || cmd == "help")
        {
            Console.WriteLine("usage: webapi [start|stop|status]");
            Console.WriteLine("  start   start the WebAPI-style sample service (port 8080)");
            Console.WriteLine("  stop    stop the service");
            Console.WriteLine("  status  show whether the service is running");
            Console.WriteLine("docs: docs/samples/webapi/README.md");
            return 0;
        }

        if (cmd == "stop")
        {
            int rc = Services.Stop("webapi");
            Console.WriteLine(rc == 0 ? "[webapi] service stopped" : "[webapi] not running");
            return rc == 0 ? 0 : 1;
        }

        if (cmd == "status")
        {
            Console.Write("[webapi] ");
            Console.WriteLine(TasksWebApp.Active ? "running" : "not running");
            return 0;
        }

        if (cmd != "start")
        {
            Console.WriteLine("usage: webapi [start|stop|status]");
            return 1;
        }

        int result = Services.Start("webapi");
        if (result == 0)
        {
            Console.WriteLine("[webapi] service started (driven by the kernel idle tick)");
            return 0;
        }

        Console.Write("[webapi] failed to start (code ");
        Console.Write(Util.PadLeft(result, 1));
        Console.WriteLine(")");
        if (result == -30)
            Console.WriteLine("       no eth0 - run with a virtio NIC, then configure with dhcp");
        else if (result == -32)
            Console.WriteLine("       could not bind port 8080 (already in use?)");
        return 1;
    }
}
