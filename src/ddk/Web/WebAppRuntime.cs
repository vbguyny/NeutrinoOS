// NeutrinoOS Web - Kestrel port milestone M4: the hosted-app runtime.
//
// The deployment model from specs/phase-6.md, implemented the NeutrinoOS
// way: a web app DLL is launched as a cooperative background service.
//
//   webhost /apps/webapp/webapp.dll [args...] [&]
//
// The kernel's ServiceRegistry.StartApp():
//   1. calls WebAppRuntime.SetHosted() (this class, compiled from the DDK
//      by name - the same mechanism as the webhost/webapi services),
//   2. loads + JIT-compiles + invokes the app DLL's Main(string[]),
//   3. the app's Program.cs builds a WebApplication and calls app.Run();
//      in hosted mode Run() starts the listener, registers the app here
//      and returns (the kernel's idle hook drives Tick),
//   4. the kernel checks GetHostedCount() grew and registers this class's
//      TickAll/StopAll as the "webapp" service tick.
//
// Stop via `webhost stop` -> ServiceRegistry.Stop("webapp") -> StopAll().
//
// app.Run() outside hosted mode (e.g. `run /apps/webapp/webapp.dll`) is
// the foreground form: Start + a bounded pump loop that owns the machine.

using System;

namespace NeutrinoOS.DDK.Web;

/// <summary>Hosted web-app registry + kernel service entry points (see file header).</summary>
public static class WebAppRuntime
{
    /// <summary>Maximum concurrently hosted apps.</summary>
    public const int MaxHostedApps = 4;

    private static readonly WebApplication[] _apps = new WebApplication[MaxHostedApps];
    private static int _count;
    private static bool _hosted;

    /// <summary>True while a launcher is hosting this process's app (see file header).</summary>
    public static bool Hosted => _hosted;

    /// <summary>Set by the kernel launcher before the app's Main runs.</summary>
    public static void SetHosted()
    {
        _hosted = true;
    }

    /// <summary>Cleared by the kernel launcher after the app's Main returns.</summary>
    public static void ClearHosted()
    {
        _hosted = false;
    }

    /// <summary>Number of registered hosted apps (kernel checks this grew).</summary>
    public static int GetHostedCount()
    {
        return _count;
    }

    /// <summary>
    /// Called by WebApplication.Run() in hosted mode: keep the app alive
    /// and let the kernel's idle hook drive it.
    /// </summary>
    public static void RegisterHosted(WebApplication app)
    {
        if (app == null)
            return;
        for (int i = 0; i < MaxHostedApps; i++)
        {
            if (_apps[i] == null)
            {
                _apps[i] = app;
                _count++;
                Console.WriteLine("[" + app.Name + "] hosted cooperatively (kernel idle tick drives it)");
                return;
            }
        }
        Console.WriteLine("[webapp] host slot limit reached (" + MaxHostedApps + ") - app not registered");
    }

    /// <summary>One bounded slice for every hosted app (kernel "webapp" service tick).</summary>
    public static void TickAll()
    {
        for (int i = 0; i < MaxHostedApps; i++)
        {
            WebApplication? app = _apps[i];
            if (app != null)
                app.Tick();
        }
    }

    /// <summary>Stop every hosted app and clear the registry (kernel Stop).</summary>
    public static void StopAll()
    {
        for (int i = 0; i < MaxHostedApps; i++)
        {
            WebApplication? app = _apps[i];
            if (app != null)
            {
                app.Stop();
                _apps[i] = null;
            }
        }
        _count = 0;
    }
}
