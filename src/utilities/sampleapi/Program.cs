// NeutrinoOS sample utility: sampleapi - start/stop the sample REST API
//
// Usage: sampleapi [start|stop|status]
//
// The REST API itself lives in the DDK (NeutrinoOS.DDK.Services.SampleApi;
// see src/ddk/Services/SampleApi.cs). This shim asks the kernel service
// registry to start it; the kernel JIT-compiles the service from the DDK
// assembly and calls its Tick() from the shell idle hook, so the server
// runs cooperatively without extra threads. This is the same pattern the
// webhost and sshd utilities use.
//
// The API listens on port 8080. Reach it from Windows with:
//   qemu ... -netdev user,id=n0,hostfwd=tcp::18080-:8080
//   curl http://127.0.0.1:18080/api/v1/info
//
// Docs + Postman collection: docs/samples/rest-api/README.md

using System;
using NeutrinoOS.Utils;
using NeutrinoOS.DDK.Kernel;
using NeutrinoOS.DDK.Services;

namespace NeutrinoOS.Utility.Sampleapi;

/// <summary>The sampleapi utility (see file header).</summary>
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
            Console.WriteLine("usage: sampleapi [start|stop|status]");
            Console.WriteLine("  start   start the sample REST API service (port 8080)");
            Console.WriteLine("  stop    stop the service");
            Console.WriteLine("  status  show whether the service is running");
            Console.WriteLine("docs: docs/samples/rest-api/README.md");
            return 0;
        }

        if (cmd == "stop")
        {
            int rc = Services.Stop("sampleapi");
            Console.WriteLine(rc == 0 ? "[sampleapi] service stopped" : "[sampleapi] not running");
            return rc == 0 ? 0 : 1;
        }

        if (cmd == "status")
        {
            Console.Write("[sampleapi] ");
            Console.WriteLine(SampleApi.Active ? "running" : "not running");
            return 0;
        }

        if (cmd != "start")
        {
            Console.WriteLine("usage: sampleapi [start|stop|status]");
            return 1;
        }

        int result = Services.Start("sampleapi");
        if (result == 0)
        {
            Console.WriteLine("[sampleapi] service started (driven by the kernel idle tick)");
            return 0;
        }

        Console.Write("[sampleapi] failed to start (code ");
        Console.Write(Util.PadLeft(result, 1));
        Console.WriteLine(")");
        if (result == -30)
            Console.WriteLine("       no eth0 - run with a virtio NIC, then configure with dhcp");
        else if (result == -32)
            Console.WriteLine("       could not bind port 8080 (already in use?)");
        return 1;
    }
}
