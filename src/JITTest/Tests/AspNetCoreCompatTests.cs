// JITTest - M7 ASP.NET Core namespace-compat tests
//
// Pins the Microsoft.AspNetCore.*/Microsoft.Extensions.* shim layer the
// DDK added in M7 (docs/KESTREL-PORT.md): the same-namespace types that
// let an ASP.NET Core minimal-API Program.cs compile unchanged for the
// device (see src/apps/helloapi/Program.cs, whose Windows twin lives in
// samples/aspnet-compat/helloapi.windows).
//
// Covered here without the wire:
//   - WebApplication.CreateBuilder + builder.WebHost.UseUrls (http/
//     https/bare-port/multi) mapping onto the Kestrel-lite options.
//   - AddSingleton / GetService / GetRequiredService through the
//     Microsoft.Extensions.DependencyInjection extensions over
//     System.IServiceProvider (korlib type added in M7).
//   - MapGet/MapPost route registration (visible via the OpenAPI doc).
//   - The HttpContext/HttpRequest/HttpResponse wrappers (status,
//     content-type, WriteAsync, route values, query, PathString/
//     QueryString/StringValues conversions).
//   - app.Lifetime as Microsoft.Extensions.Hosting.IHostApplicationLifetime.
//   - LogInformation/... extensions over the simplified ILogger.
//   - The instance-bound async method -> RequestHandlerAsync delegate
//     pattern the shim's MapGet relies on (Tier-0 risk area).
//
// The full end-to-end run (device webhost + Windows twin + byte
// comparison) is build/m7-aspnet-probe.sh against QEMU.

namespace JITTest;

using System.Threading.Tasks;
using NeutrinoOS.DDK.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MsApp = Microsoft.AspNetCore.Builder.WebApplication;
using MsContext = Microsoft.AspNetCore.Http.HttpContext;
using MsDelegate = Microsoft.AspNetCore.Http.RequestDelegate;
using MsPathString = Microsoft.AspNetCore.Http.PathString;
using MsQueryString = Microsoft.AspNetCore.Http.QueryString;
using MsStringValues = Microsoft.AspNetCore.Http.StringValues;

/// <summary>M7 ASP.NET Core namespace-compat tests (see file header).</summary>
public static class AspNetCoreCompatTests
{
    private sealed class Greeting
    {
        public string Prefix = "hello";
    }

    private sealed class Other
    {
    }

    private sealed class TestLogger : ILogger
    {
        public LogLevel LastLevel;
        public string LastMessage;

        public bool IsEnabled(LogLevel level) => true;

        public void Log(LogLevel level, string message)
        {
            LastLevel = level;
            LastMessage = message;
        }
    }

    // Mirrors the DDK ShimRouteBridge shape: an instance-bound async
    // method used as a RequestHandlerAsync delegate (the same IL pattern
    // the shim's MapGet produces, exercised here on the JIT surface).
    private sealed class MirrorBridge
    {
        internal Task InvokeAsync(HttpContext context)
        {
            context.Response.Write("mirror-ok");
            return Task.CompletedTask;
        }
    }

    private static bool _startedFired;

    private static void OnStarted()
    {
        _startedFired = true;
    }

    private static Task Greet(MsContext ctx)
    {
        ctx.Response.WriteAsync("ok");
        return Task.CompletedTask;
    }

    private static Task Respond(MsContext ctx)
    {
        ctx.Response.StatusCode = 201;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync("{\"wrapped\":true}");
    }

    /// <summary>Runs the M7 tests.</summary>
    public static void RunAll()
    {
        TestUseUrls();
        TestServices();
        TestRoutes();
        TestContextWrapper();
        TestRequestShapes();
        TestStructConversions();
        TestLifetimeAndName();
        TestLoggerExtensions();
        TestBoundMethodDelegate();
    }

    private static void TestUseUrls()
    {
        var b1 = MsApp.CreateBuilder(new string[0]);
        b1.WebHost.UseUrls("http://0.0.0.0:8092");
        TestTracker.Record("m7/useurls-http", ((int)b1.Kestrel.Port) == 8092,
            "port=" + ((int)b1.Kestrel.Port).ToString());

        var b2 = MsApp.CreateBuilder(new string[0]);
        b2.WebHost.UseUrls("https://0.0.0.0:8443");
        TestTracker.Record("m7/useurls-https",
            b2.Kestrel.HttpsEnabled && ((int)b2.Kestrel.HttpsPort) == 8443);

        var b3 = MsApp.CreateBuilder(new string[0]);
        b3.WebHost.UseUrls("8093");
        TestTracker.Record("m7/useurls-bare-port", ((int)b3.Kestrel.Port) == 8093);

        var b4 = MsApp.CreateBuilder(new string[0]);
        b4.WebHost.UseUrls(new string[] { "http://0.0.0.0:8094", "https://0.0.0.0:8444" });
        TestTracker.Record("m7/useurls-multi",
            ((int)b4.Kestrel.Port) == 8094 && ((int)b4.Kestrel.HttpsPort) == 8444 && b4.Kestrel.HttpsEnabled);
    }

    private static void TestServices()
    {
        var builder = MsApp.CreateBuilder(new string[0]);
        var info = new Greeting();
        builder.Services.AddSingleton<Greeting>(info);
        var app = builder.Build();

        var got = app.Services.GetRequiredService<Greeting>();
        TestTracker.Record("m7/services-singleton-identity", ReferenceEquals(got, info));

        System.IServiceProvider sp = app.Services;
        var byType = sp.GetService(typeof(Greeting));
        TestTracker.Record("m7/services-system-iserviceprovider", ReferenceEquals(byType, info));

        var optional = app.Services.GetService<Greeting>();
        TestTracker.Record("m7/services-getservice-generic", ReferenceEquals(optional, info));

        bool threw = false;
        try
        {
            _ = app.Services.GetRequiredService<Other>();
        }
        catch (System.InvalidOperationException)
        {
            threw = true;
        }
        TestTracker.Record("m7/services-required-throws", threw);
    }

    private static void TestRoutes()
    {
        var app = MsApp.CreateBuilder(new string[0]).Build();

        var endpoints = app.MapGet("/greet/{name}", Greet);
        TestTracker.Record("m7/mapget-fluent-return", ReferenceEquals(endpoints, app));

        app.MapPost("/things", Greet);

        string doc = app.BuildOpenApiJson();
        TestTracker.Record("m7/mapget-registers-route", doc.IndexOf("/greet/{name}") >= 0);
        TestTracker.Record("m7/mappost-registers-route", doc.IndexOf("/things") >= 0);

        app.ApplicationName = "helloapi";
        TestTracker.Record("m7/appname-maps-name", app.ApplicationName == "helloapi");
    }

    private static void TestContextWrapper()
    {
        var engine = new HttpContext();
        var wrapped = new MsContext(engine);

        MsDelegate handler = Respond;
        Task task = handler(wrapped);

        TestTracker.Record("m7/context-handler-task-completed", task.IsCompleted);
        TestTracker.Record("m7/context-response-status", engine.Response.StatusCode == 201);
        TestTracker.Record("m7/context-response-content-type", engine.Response.ContentType == "application/json");
        TestTracker.Record("m7/context-response-write-body", engine.Response.Body == "{\"wrapped\":true}",
            engine.Response.Body);
    }

    private static void TestRequestShapes()
    {
        var engine = new HttpContext();
        engine.Request.Method = "POST";
        engine.Request.Path = "/greet/ada";
        engine.Request.QueryString = "msg=hi";
        engine.Request.SetRoute(new string[] { "name" }, new string[] { "ada" }, 1);

        var wrapped = new MsContext(engine);
        TestTracker.Record("m7/request-method", wrapped.Request.Method == "POST");

        MsPathString path = wrapped.Request.Path;
        TestTracker.Record("m7/request-path-string", path.HasValue && ((string)path) == "/greet/ada");

        var name = wrapped.Request.RouteValues["name"] as string;
        TestTracker.Record("m7/request-route-values", name == "ada");

        object raw = null;
        bool found = wrapped.Request.RouteValues.TryGetValue("name", out raw);
        TestTracker.Record("m7/request-route-tryget", found && ((string)raw) == "ada");
        bool absent = wrapped.Request.RouteValues.TryGetValue("nope", out raw);
        TestTracker.Record("m7/request-route-tryget-absent", !absent);

        MsStringValues sv = wrapped.Request.Query["msg"];
        TestTracker.Record("m7/request-query-implicit", ((string)sv) == "hi");
        TestTracker.Record("m7/request-query-count", sv.Count == 1);

        MsStringValues missing = wrapped.Request.Query["none"];
        TestTracker.Record("m7/request-query-absent", missing.Count == 0);

        MsQueryString q = wrapped.Request.QueryString;
        TestTracker.Record("m7/request-query-string-raw", q.HasValue && ((string)q) == "msg=hi");
    }

    private static void TestStructConversions()
    {
        MsPathString p = "/health";
        string ps = p;
        TestTracker.Record("m7/pathstring-roundtrip", ps == "/health" && p.HasValue);

        var empty = new MsPathString(null);
        TestTracker.Record("m7/pathstring-empty", !empty.HasValue && empty.ToString() == "");

        MsQueryString qs = "msg=hi";
        TestTracker.Record("m7/querystring-tostring", qs.ToString() == "?msg=hi");
        string qraw = qs;
        TestTracker.Record("m7/querystring-raw", qraw == "msg=hi");

        MsStringValues one = "abc";
        TestTracker.Record("m7/stringvalues-tostring", one.ToString() == "abc");

        var none = new MsStringValues(null);
        TestTracker.Record("m7/stringvalues-none", none.Count == 0 && none.ToString() == "");
    }

    private static void TestLifetimeAndName()
    {
        var app = MsApp.CreateBuilder(new string[0]).Build();

        _startedFired = false;
        app.Lifetime.ApplicationStarted.Register(OnStarted);

        var lifetime = app.Lifetime as HostLifetime;
        TestTracker.Record("m7/lifetime-host-lifetime-type", lifetime != null);

        if (lifetime != null)
            lifetime.NotifyStarted();
        TestTracker.Record("m7/lifetime-started-fires", _startedFired);
    }

    private static void TestLoggerExtensions()
    {
        var log = new TestLogger();
        log.LogInformation("boot ok");
        TestTracker.Record("m7/logger-information",
            log.LastMessage == "boot ok" && log.LastLevel == LogLevel.Information);

        log.LogWarning("careful");
        TestTracker.Record("m7/logger-warning", log.LastLevel == LogLevel.Warning);

        log.LogTrace("t");
        log.LogDebug("d");
        log.LogError("e");
        log.LogCritical("c");
        TestTracker.Record("m7/logger-levels-shared-values",
            log.LastLevel == LogLevel.Critical && (int)LogLevel.Trace == 0 && (int)LogLevel.Information == 2);
    }

    private static void TestBoundMethodDelegate()
    {
        var bridge = new MirrorBridge();
        RequestHandlerAsync handler = bridge.InvokeAsync;
        var ctx = new HttpContext();
        ctx.Response.StatusCode = 200;

        handler(ctx);

        TestTracker.Record("m7/bound-delegate-invoked", ctx.Response.Body == "mirror-ok",
            ctx.Response.Body);
    }
}
