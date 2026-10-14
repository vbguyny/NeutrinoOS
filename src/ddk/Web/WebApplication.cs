// NeutrinoOS Web - Kestrel port milestone M4: the WebApplication surface.
//
// The API an app author writes against. It mirrors ASP.NET Core minimal
// APIs closely enough that the same Program.cs shape works on-device:
//
//   ASP.NET Core (and NeutrinoOS M4):
//     var builder = WebApplication.CreateBuilder(args);
//     builder.Services.AddSingleton<ILoggerFactory>(lf);
//     var app = builder.Build();
//     app.MapGet("/api/v1/tasks", OnListTasks);
//     app.Run();
//
// M4 brings the pieces that make the shape honest:
//   - CreateBuilder(args) returns a WebApplicationBuilder with Services
//     (the DI shim, ServiceCollection) and Kestrel options; Build()
//     freezes them into the application.
//   - `--urls http://host:port` (or --urls=...) binds the port.
//   - Run() starts the listener and then either (a) registers with
//     WebAppRuntime and returns - the kernel idle hook drives Tick()
//     when the app was launched via `webhost <app.dll>` - or (b) pumps
//     a foreground loop that owns the machine (direct `run app.dll`).
//   - The classic cooperative contract (Start/Tick/Stop as a kernel
//     service, used by the `webapi` sample) still works unchanged.

using System;

namespace NeutrinoOS.DDK.Web;

/// <summary>The Minimal-API-style application object (M4 subset).</summary>
public sealed class WebApplication
{
    private readonly WebHost _host;
    private readonly ServiceCollection _services;
    private readonly HostLifetime _lifetime;
    private bool _runStop;

    internal WebApplication(WebHost host, ServiceCollection services)
    {
        _host = host;
        _services = services ?? new ServiceCollection();
        _lifetime = new HostLifetime();
        _lifetime.Owner = this;
    }

    /// <summary>Log name, e.g. "webapi" (printed as [webapi] ...).</summary>
    public string Name
    {
        get { return _host.Name; }
        set { _host.Name = value; }
    }

    /// <summary>TCP port to listen on (default 8080).</summary>
    public ushort Port
    {
        get { return _host.Port; }
        set { _host.Port = value; }
    }

    /// <summary>Requests handled since Start().</summary>
    public int RequestCount => _host.RequestCount;

    /// <summary>True while the listener is running.</summary>
    public bool IsRunning => _host.IsRunning;

    /// <summary>
    /// Create the builder (ASP.NET Core's CreateBuilder). Configure
    /// <c>builder.Services</c> / <c>builder.Kestrel</c>, then call
    /// <c>builder.Build()</c> for the application. <paramref name="args"/>:
    /// <c>--urls</c> sets the listening port at Build().
    /// </summary>
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        return new WebApplicationBuilder(args);
    }

    /// <summary>The DI container (M4): app.Services.GetRequiredService&lt;T&gt;().</summary>
    public ServiceCollection Services => _services;

    /// <summary>The engine behind this application (M6: OpenAPI plumbing).</summary>
    internal WebHost Host => _host;

    /// <summary>
    /// The OpenAPI document JSON for the currently registered routes (the
    /// body the MapOpenApi endpoint serves; also handy for tooling/tests).
    /// See OpenApi.cs (M6).
    /// </summary>
    public string BuildOpenApiJson()
    {
        return _host.BuildOpenApiJson();
    }

    /// <summary>The Kestrel-lite options this app was built with (M4).</summary>
    public KestrelServerOptions Options => _host.Options;

    /// <summary>
    /// The application lifetime (M5): IHostApplicationLifetime-shaped
    /// hooks + StopApplication(). Fires Started once the listeners bind,
    /// Stopping/Stopped around Stop().
    /// </summary>
    public IHostApplicationLifetime Lifetime => _lifetime;

    /// <summary>Register a GET handler for a pattern ("/x", "/x/{id}").</summary>
    public WebApplication MapGet(string pattern, RequestHandler handler)
    {
        _host.AddRoute("GET", pattern, handler);
        return this;
    }

    /// <summary>Register a POST handler for a pattern.</summary>
    public WebApplication MapPost(string pattern, RequestHandler handler)
    {
        _host.AddRoute("POST", pattern, handler);
        return this;
    }

    /// <summary>Register a PUT handler for a pattern.</summary>
    public WebApplication MapPut(string pattern, RequestHandler handler)
    {
        _host.AddRoute("PUT", pattern, handler);
        return this;
    }

    /// <summary>Register a DELETE handler for a pattern.</summary>
    public WebApplication MapDelete(string pattern, RequestHandler handler)
    {
        _host.AddRoute("DELETE", pattern, handler);
        return this;
    }

    /// <summary>Register an async GET handler for a pattern.</summary>
    public WebApplication MapGet(string pattern, RequestHandlerAsync handler)
    {
        _host.AddRouteAsync("GET", pattern, handler);
        return this;
    }

    /// <summary>Register an async POST handler for a pattern.</summary>
    public WebApplication MapPost(string pattern, RequestHandlerAsync handler)
    {
        _host.AddRouteAsync("POST", pattern, handler);
        return this;
    }

    /// <summary>Register an async PUT handler for a pattern.</summary>
    public WebApplication MapPut(string pattern, RequestHandlerAsync handler)
    {
        _host.AddRouteAsync("PUT", pattern, handler);
        return this;
    }

    /// <summary>Register an async DELETE handler for a pattern.</summary>
    public WebApplication MapDelete(string pattern, RequestHandlerAsync handler)
    {
        _host.AddRouteAsync("DELETE", pattern, handler);
        return this;
    }

    /// <summary>Start the listener; 0 = ok (-30 no eth0, -32 bind failed).</summary>
    public int Start()
    {
        int rc = _host.Start();
        if (rc == 0)
            _lifetime.NotifyStarted();
        return rc;
    }

    /// <summary>One bounded work slice (kernel idle hook drives this).</summary>
    public void Tick()
    {
        _host.Tick();
    }

    /// <summary>Stop the listener (and any foreground Run loop).</summary>
    public void Stop()
    {
        _runStop = true;
        _lifetime.NotifyStopping();
        _host.Stop();
        _lifetime.NotifyStopped();
    }

    // ====================================================================
    // app.Run() (M4)
    // ====================================================================

    /// <summary>
    /// The application's main loop (ASP.NET Core's app.Run()). Hosted mode
    /// (launched via `webhost &lt;app.dll&gt;`): Start(), register with
    /// WebAppRuntime and return - the kernel idle hook drives Tick() so the
    /// shell stays usable. Foreground mode: Start() then pump bounded Tick
    /// slices until Stop() is called.
    /// </summary>
    public int Run()
    {
        return Run(0);
    }

    /// <summary>
    /// Bounded form used by tests and tooling: <paramref name="maxTicks"/>
    /// &gt; 0 pumps at most that many slices, then stops and returns.
    /// </summary>
    public int Run(int maxTicks)
    {
        _runStop = false;
        int rc = Start();
        if (rc != 0)
            return rc;

        if (WebAppRuntime.Hosted)
        {
            WebAppRuntime.RegisterHosted(this);
            return 0;
        }

        Console.WriteLine("[" + Name + "] running (foreground pump loop)");
        int ticks = 0;
        while (!_runStop)
        {
            Tick();
            ticks++;
            if (maxTicks > 0 && ticks >= maxTicks)
                break;
        }
        Stop();
        return 0;
    }

    // ====================================================================
    // --urls binding (M4)
    // ====================================================================

    /// <summary>
    /// Apply the <c>--urls</c> / <c>--urls=</c> command-line value (the M4
    /// slice of ASP.NET's URL configuration): the first parseable port
    /// wins. Scheme and host are ignored - the engine binds all interfaces.
    /// </summary>
    internal static void ApplyUrls(string[] args, KestrelServerOptions options)
    {
        if (args == null)
            return;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == null)
                continue;

            string? value = null;
            if (a == "--urls" && i + 1 < args.Length)
            {
                value = args[i + 1];
                i++;
            }
            else if (a.Length > 7 && a[0] == '-' && a[1] == '-' &&
                     a[2] == 'u' && a[3] == 'r' && a[4] == 'l' && a[5] == 's' && a[6] == '=')
            {
                value = a.Substring(7, a.Length - 7);
            }
            if (value == null)
                continue;

            int port = ParsePort(value);
            if (port > 0)
                options.Port = (ushort)port;
        }
    }

    /// <summary>Extract the port from "http://host:port" (a bare port works too).</summary>
    internal static int ParsePort(string value)
    {
        if (value == null || value.Length == 0)
            return -1;

        // Digits after the last ':'; a bare number is accepted as well.
        int start = value.Length;
        while (start > 0)
        {
            char c = value[start - 1];
            if (c < '0' || c > '9')
                break;
            start--;
        }
        if (start == value.Length)
            return -1;   // no trailing digits

        // A bare number is the whole string; otherwise require the ':'.
        if (start > 0 && value[start - 1] != ':')
            return -1;

        int port = 0;
        for (int i = start; i < value.Length; i++)
        {
            port = port * 10 + (value[i] - '0');
            if (port > 65535)
                return -1;
        }
        return port > 0 ? port : -1;
    }
}

/// <summary>
/// ASP.NET-Core-shaped builder (M4): Services (the DI shim), Kestrel
/// options and --urls binding; Build() freezes them into the application.
/// </summary>
public sealed class WebApplicationBuilder
{
    private readonly string[] _args;

    /// <summary>Service registrations (instance singletons).</summary>
    public ServiceCollection Services { get; } = new ServiceCollection();

    /// <summary>Kestrel-lite options; --urls sets Port at Build().</summary>
    public KestrelServerOptions Kestrel { get; } = new KestrelServerOptions();

    internal WebApplicationBuilder(string[] args)
    {
        _args = args ?? new string[0];
    }

    /// <summary>Build the application with the configured options.</summary>
    public WebApplication Build()
    {
        WebApplication.ApplyUrls(_args, Kestrel);
        return new WebApplication(new WebHost("app", Kestrel), Services);
    }
}
