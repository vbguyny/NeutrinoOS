// JITTest - M4 hosting-layer tests
//
// Pins the Kestrel-port M4 pieces that do not need the wire:
//   - WebApplicationBuilder fidelity: CreateBuilder -> Build,
//     --urls binding (both forms), Kestrel options plumbing.
//   - The DI shim: AddSingleton / GetService / GetRequiredService,
//     identity, missing-service behavior, interface registrations.
//   - The logging shim: level gating + the rendered line format.
//   - WebAppRuntime: hosted-mode registration, safe TickAll on a
//     not-started app, StopAll clearing the registry.
//
// The full end-to-end hosted-app launch (webhost <dll>) runs in
// build/m4-hosting-probe.sh against QEMU; it needs eth0 + a real socket.

namespace JITTest;

using NeutrinoOS.DDK.Logging;
using NeutrinoOS.DDK.Web;

/// <summary>M4 hosting-layer tests (see file header).</summary>
public static class HostingTests
{
    private sealed class TestService
    {
        public string Value = "svc-42";
    }

    private sealed class OtherService
    {
    }

    /// <summary>Runs the M4/M5 hosting tests.</summary>
    public static void RunAll()
    {
        TestBuilderDefaults();
        TestUrlsBinding();
        TestKestrelOptions();
        TestServiceCollection();
        TestLogging();
        TestHostedRuntime();
        TestM5Compat();
        TestOpenApiCompat();
    }

    private static void TestBuilderDefaults()
    {
        var builder = WebApplication.CreateBuilder(new string[0]);
        var app = builder.Build();

        TestTracker.Record("m4/builder-build-defaults", app.Port == 8080, "port=" + ((int)app.Port).ToString());
        TestTracker.Record("m4/builder-services-exposed", app.Services != null);
        TestTracker.Record("m4/builder-options-exposed", app.Options != null &&
            app.Options.MaxConnections == 4 && app.Options.MaxRequestBytes == 4096 &&
            app.Options.MaxRequestsPerConnection == 100 && app.Options.KeepAliveTimeoutMs == 10000);
        TestTracker.Record("m4/builder-options-instance-shared", ReferenceEquals(app.Options, builder.Kestrel));
    }

    private static void TestUrlsBinding()
    {
        // `--urls <value>` (separate token).
        var b1 = WebApplication.CreateBuilder(new string[] { "--urls", "http://0.0.0.0:5000" });
        TestTracker.Record("m4/urls-separate-arg", b1.Build().Port == 5000);

        // `--urls=<value>` with a wildcard host.
        var b2 = WebApplication.CreateBuilder(new string[] { "--urls=http://*:5001" });
        TestTracker.Record("m4/urls-equals-arg", b2.Build().Port == 5001);

        // Bare port convenience.
        var b3 = WebApplication.CreateBuilder(new string[] { "--urls", "5099" });
        TestTracker.Record("m4/urls-bare-port", b3.Build().Port == 5099);

        // https scheme: port still wins (TLS is not part of the M4 engine).
        var b4 = WebApplication.CreateBuilder(new string[] { "--urls", "https://+:5443" });
        TestTracker.Record("m4/urls-https-port", b4.Build().Port == 5443);

        // Unparseable value keeps the default.
        var b5 = WebApplication.CreateBuilder(new string[] { "--urls", "not-a-url" });
        TestTracker.Record("m4/urls-invalid-keeps-default", b5.Build().Port == 8080);

        // Port 0 is not a valid listeners choice here; keeps the default.
        var b6 = WebApplication.CreateBuilder(new string[] { "--urls", "http://0.0.0.0:0" });
        TestTracker.Record("m4/urls-zero-keeps-default", b6.Build().Port == 8080);
    }

    private static void TestKestrelOptions()
    {
        var builder = WebApplication.CreateBuilder(new string[0]);
        builder.Kestrel.Port = 8181;
        builder.Kestrel.MaxConnections = 7;
        builder.Kestrel.MaxRequestBytes = 8192;
        builder.Kestrel.MaxRoutes = 40;
        builder.Kestrel.MaxPathSegments = 10;
        builder.Kestrel.MaxRequestsPerConnection = 3;
        builder.Kestrel.KeepAliveTimeoutMs = 2500;

        var app = builder.Build();
        TestTracker.Record("m4/options-port-applied", app.Port == 8181);
        TestTracker.Record("m4/options-values-visible",
            app.Options.MaxConnections == 7 && app.Options.MaxRequestBytes == 8192 &&
            app.Options.MaxRoutes == 40 && app.Options.MaxPathSegments == 10 &&
            app.Options.MaxRequestsPerConnection == 3 && app.Options.KeepAliveTimeoutMs == 2500);
    }

    private static void TestServiceCollection()
    {
        var builder = WebApplication.CreateBuilder(new string[0]);
        var factory = NullLoggerFactory.Instance;
        var service = new TestService();

        builder.Services.AddSingleton<ILoggerFactory>(factory);
        builder.Services.AddSingleton<TestService>(service);

        var app = builder.Build();

        TestTracker.Record("m4/di-count", app.Services.Count == 2);

        TestService? resolved = app.Services.GetRequiredService<TestService>();
        TestTracker.Record("m4/di-identity", ReferenceEquals(resolved, service) && resolved.Value == "svc-42");

        ILoggerFactory? resolvedFactory = app.Services.GetService<ILoggerFactory>();
        TestTracker.Record("m4/di-interface-registration", ReferenceEquals(resolvedFactory, NullLoggerFactory.Instance));

        TestTracker.Record("m4/di-missing-returns-null", app.Services.GetService<OtherService>() == null);

        // Replace-on-re-register.
        var replacement = new TestService();
        replacement.Value = "svc-43";
        builder.Services.AddSingleton<TestService>(replacement);
        TestTracker.Record("m4/di-reregister-replaces",
            ReferenceEquals(app.Services.GetRequiredService<TestService>(), replacement) && app.Services.Count == 2);

        // GetRequiredService throws for an unregistered type.
        bool threw = false;
        try
        {
            app.Services.GetRequiredService<OtherService>();
        }
        catch (System.InvalidOperationException)
        {
            threw = true;
        }
        TestTracker.Record("m4/di-required-throws", threw);
    }

    private static void TestLogging()
    {
        var logger = new ConsoleLogger("t", LogLevel.Warning);
        TestTracker.Record("m4/logger-level-gate", !logger.IsEnabled(LogLevel.Information));

        logger.Log(LogLevel.Information, "hidden");
        TestTracker.Record("m4/logger-below-min-silent", logger.LastMessage == null);

        logger.Log(LogLevel.Error, "boom");
        TestTracker.Record("m4/logger-rendered-line", logger.LastMessage == "[t] Error: boom");

        var factory = new ConsoleLoggerFactory("webapp");
        ILogger a = factory.CreateLogger("startup");
        ILogger b = factory.CreateLogger("startup");
        ILogger c = factory.CreateLogger("other");
        TestTracker.Record("m4/logger-factory-cache", ReferenceEquals(a, b));
        TestTracker.Record("m4/logger-factory-category", !ReferenceEquals(a, c));

        ILogger nullLogger = NullLoggerFactory.Instance.CreateLogger("x");
        TestTracker.Record("m4/null-logger", !nullLogger.IsEnabled(LogLevel.Critical));
    }

    private static void TestHostedRuntime()
    {
        // Clean slate; the suite may run after earlier categories.
        WebAppRuntime.StopAll();
        WebAppRuntime.ClearHosted();

        int before = WebAppRuntime.GetHostedCount();

        WebAppRuntime.SetHosted();
        TestTracker.Record("m4/hosted-flag-set", WebAppRuntime.Hosted);

        var app = WebApplication.CreateBuilder(new string[0]).Build();
        WebAppRuntime.RegisterHosted(app);
        TestTracker.Record("m4/hosted-register-counts", WebAppRuntime.GetHostedCount() == before + 1);

        // TickAll on a not-started app must be a safe no-op.
        WebAppRuntime.TickAll();
        TestTracker.Record("m4/hosted-tick-not-started-safe", !app.IsRunning);

        WebAppRuntime.StopAll();
        TestTracker.Record("m4/hosted-stopall-clears", WebAppRuntime.GetHostedCount() == 0);

        WebAppRuntime.ClearHosted();
        TestTracker.Record("m4/hosted-flag-cleared", !WebAppRuntime.Hosted);
    }

    // ---- M5: real-shape compat (IServiceProvider, IHostApplicationLifetime) ----

    private static bool s_startHookFired;
    private static bool s_stopHookFired;

    private static void OnStartedHook()
    {
        s_startHookFired = true;
    }

    private static void OnStoppedHook()
    {
        s_stopHookFired = true;
    }

    private static void TestM5Compat()
    {
        var builder = WebApplication.CreateBuilder(new string[0]);
        var factory = NullLoggerFactory.Instance;
        builder.Services.AddSingleton<ILoggerFactory>(factory);
        var app = builder.Build();

        // IServiceProvider shape: non-generic lookup by Type.
        IServiceProvider provider = app.Services;
        TestTracker.Record("m5/iserviceprovider-type-lookup",
            ReferenceEquals(provider.GetService(typeof(ILoggerFactory)), factory));
        TestTracker.Record("m5/iserviceprovider-missing-null",
            provider.GetService(typeof(TestService)) == null);

        // IHostApplicationLifetime: tokens + hooks + StopApplication.
        // (The concrete HostLifetime exposes the engine hooks + state
        // predicates the interface deliberately does not.)
        var lifetime = (HostLifetime)app.Lifetime;
        TestTracker.Record("m5/lifetime-fresh",
            !lifetime.ApplicationStarted.IsCancellationRequested &&
            !lifetime.ApplicationStopping.IsCancellationRequested &&
            !lifetime.IsStopping);

        s_startHookFired = false;
        s_stopHookFired = false;
        lifetime.ApplicationStarted.Register(OnStartedHook);
        lifetime.ApplicationStopped.Register(OnStoppedHook);

        lifetime.NotifyStarted();
        TestTracker.Record("m5/lifetime-started-hook",
            s_startHookFired && lifetime.ApplicationStarted.IsCancellationRequested && lifetime.IsStarted);

        lifetime.StopApplication();
        TestTracker.Record("m5/lifetime-stopapplication",
            lifetime.IsStopping && s_stopHookFired &&
            lifetime.ApplicationStopping.IsCancellationRequested &&
            lifetime.ApplicationStopped.IsCancellationRequested);
        TestTracker.Record("m5/lifetime-not-started-after-stop", !lifetime.IsStarted);
    }

    // ---- M6: OpenAPI document (AddOpenApi / MapOpenApi) ----

    private static void OpenApiPing(HttpContext ctx)
    {
        ctx.Response.Json("{}\n");
    }

    private static void OpenApiThing(HttpContext ctx)
    {
        ctx.Response.Json("{}\n");
    }

    private static void TestOpenApiCompat()
    {
        var builder = WebApplication.CreateBuilder(new string[0]);
        builder.Services.AddOpenApi(new OpenApiOptions { Title = "T-API", Version = "9.9" });
        var app = builder.Build();
        app.Name = "openapi-test";
        app.MapGet("/ping", OpenApiPing);
        app.MapPost("/things/{id}", OpenApiThing);
        app.MapOpenApi();

        TestTracker.Record("m6/openapi-service-registered",
            app.Services.GetService<OpenApiOptions>() != null);

        string json = app.BuildOpenApiJson();
        TestTracker.Record("m6/openapi-spec-version", json.Contains("\"openapi\": \"3.1.0\""));
        TestTracker.Record("m6/openapi-info-custom",
            json.Contains("\"title\": \"T-API\"") && json.Contains("\"version\": \"9.9\""));
        TestTracker.Record("m6/openapi-path-get",
            json.Contains("\"/ping\": {") && json.Contains("\"get\": {"));
        TestTracker.Record("m6/openapi-path-post",
            json.Contains("\"/things/{id}\": {") && json.Contains("\"post\": {"));
        TestTracker.Record("m6/openapi-path-parameter", json.Contains("\"name\": \"id\""));
        TestTracker.Record("m6/openapi-doc-endpoint-excluded", !json.Contains("openapi/v1.json"));
    }
}
