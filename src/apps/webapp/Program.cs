// NeutrinoOS M4 sample web app - the deployment model in action.
//
// This is a standalone .NET 10 app DLL deployed under /apps/webapp/ and
// launched as a cooperative hosted app from the NeutrinoOS shell:
//
//   webhost /apps/webapp/webapp.dll --urls http://0.0.0.0:8090 &
//
// The Program.cs shape is the ASP.NET Core minimal-API shape the port
// targets (docs/KESTREL-PORT.md M4):
//
//   var builder = WebApplication.CreateBuilder(args);   // + --urls
//   builder.Services.AddSingleton<..>(..);              // DI shim
//   var app = builder.Build();
//   app.MapGet("/", handler);
//   app.Run();     // hosted: registers + returns (kernel idle tick drives)
//
// Endpoints:
//   GET /        app blurb (builder style)
//   GET /health  liveness
//   GET /urls    the port the builder resolved (--urls binding)
//   GET /di      a service resolved from app.Services (DI shim)
//   GET /log     the last rendered ILogger line (logging shim)
//
// Reach it from Windows 11 with
//   -netdev user,id=n0,hostfwd=tcp::18090-:8090
// then http://127.0.0.1:18090 .

using System;
using NeutrinoOS.DDK.Kernel;
using NeutrinoOS.DDK.Logging;
using NeutrinoOS.DDK.Web;

namespace NeutrinoOS.Apps.Webapp;

/// <summary>A service registered in the builder's DI container (demo).</summary>
public sealed class AppInfo
{
    /// <summary>Opaque value the /di endpoint reports.</summary>
    public string Name = "webapp-m4";
}

/// <summary>The M4 sample web app (see file header).</summary>
public static class Program
{
    private static WebApplication _app;
    private static ILogger _log;

    /// <summary>Entry point (invoked by `webhost <app.dll>` in hosted mode).</summary>
    public static int Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // DI shim: register the logger factory and an app service instance.
        var loggerFactory = new ConsoleLoggerFactory("webapp");
        builder.Services.AddSingleton<ILoggerFactory>(loggerFactory);
        builder.Services.AddSingleton<AppInfo>(new AppInfo());

        // Endpoint defaults; `--urls` (in args) overrides the HTTP port at
        // Build(). M5: HTTPS (TLS 1.3) on 8091 using the shared /etc/ssl
        // Ed25519 certificate.
        builder.Kestrel.Port = 8090;
        builder.Kestrel.HttpsEnabled = true;
        builder.Kestrel.HttpsPort = 8091;

        var app = builder.Build();
        app.Name = "webapp";

        _app = app;
        _log = loggerFactory.CreateLogger("startup");

        app.MapGet("/", OnRoot);
        app.MapGet("/health", OnHealth);
        app.MapGet("/urls", OnUrls);
        app.MapGet("/di", OnDi);
        app.MapGet("/log", OnLog);
        app.MapGet("/lifetime", OnLifetime);

        // M5: IHostApplicationLifetime hook (fires inside Run -> Start).
        app.Lifetime.ApplicationStarted.Register(OnAppStarted);

        _log.Log(LogLevel.Information, "starting on port " + ((int)app.Port).ToString());

        // Hosted mode: starts, registers with WebAppRuntime, returns (the
        // kernel idle tick drives it). Foreground: pumps until Stop().
        return app.Run();
    }

    private static void OnRoot(HttpContext ctx)
    {
        ctx.Response.Json("{\"app\":\"webapp\",\"style\":\"WebApplicationBuilder\",\"milestone\":\"M4\"}\n");
    }

    private static void OnHealth(HttpContext ctx)
    {
        ctx.Response.Json("{\"status\":\"ok\"}\n");
    }

    private static void OnUrls(HttpContext ctx)
    {
        ctx.Response.Json("{\"port\":" + ((int)_app.Port).ToString() + "}\n");
    }

    private static void OnDi(HttpContext ctx)
    {
        AppInfo info = _app.Services.GetService<AppInfo>();
        if (info == null)
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.Json("{\"error\":\"AppInfo not registered\"}\n");
            return;
        }
        ctx.Response.Json("{\"service\":\"AppInfo\",\"value\":\"" + info.Name + "\"}\n");
    }

    private static void OnLog(HttpContext ctx)
    {
        string last = _log == null ? "" : ((ConsoleLogger)_log).LastMessage;
        if (last == null)
            last = "";
        ctx.Response.Json("{\"last\":\"" + last + "\"}\n");
    }

    /// <summary>IHostApplicationLifetime.ApplicationStarted hook (M5).</summary>
    private static void OnAppStarted()
    {
        if (_log != null)
            _log.Log(LogLevel.Information, "lifetime: ApplicationStarted hook fired");
    }

    /// <summary>Reports the lifetime state - proves the hooks fire on-device (M5).</summary>
    private static void OnLifetime(HttpContext ctx)
    {
        bool started = _app.Lifetime.ApplicationStarted.IsCancellationRequested;
        bool stopping = _app.Lifetime.ApplicationStopping.IsCancellationRequested;
        ctx.Response.Json("{\"started\":" + (started ? "true" : "false") +
            ",\"stopping\":" + (stopping ? "true" : "false") + "}\n");
    }
}
