// NeutrinoOS - ASP.NET Core namespace compatibility layer (M7):
// Microsoft.AspNetCore.Builder.
//
// The app author's Program shape, byte-for-byte the ASP.NET Core one:
//
//   var builder = WebApplication.CreateBuilder(args);
//   builder.WebHost.UseUrls("http://0.0.0.0:8092");
//   var app = builder.Build();
//   app.MapGet("/", handler);
//   app.Run();
//
// The same source compiles against the real Microsoft.AspNetCore.App on
// Windows (see samples/aspnet-compat/helloapi.windows) and against this
// shim on NeutrinoOS (see docs/KESTREL-PORT.md M7). The implementation
// forwards to NeutrinoOS.DDK.Web; documented limitations in
// HttpAbstractions.cs and docs/KESTREL-PORT.md.

using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Endpoints-mapping surface (ASP.NET Core shape; implemented by WebApplication).</summary>
public interface IEndpointRouteBuilder
{
    /// <summary>The application's service provider.</summary>
    System.IServiceProvider ServiceProvider { get; }
}

/// <summary>
/// The web-host builder surface (minimal slice; UseUrls arrives via
/// Microsoft.AspNetCore.Hosting.WebHostBuilderExtensions - the same
/// namespace layout as the real framework, so app source is identical
/// on both).
/// </summary>
public interface IWebHostBuilder
{
}

internal sealed class NeutrinoWebHostBuilder : IWebHostBuilder
{
    private readonly NeutrinoOS.DDK.Web.KestrelServerOptions _options;

    internal NeutrinoWebHostBuilder(NeutrinoOS.DDK.Web.KestrelServerOptions options)
    {
        _options = options;
    }

    /// <summary>Apply one url ("http(s)://host:port" or a bare port).</summary>
    internal void UseUrl(string url)
    {
        if (url == null || url.Length == 0)
            return;
        int port = NeutrinoOS.DDK.Web.WebApplication.ParsePort(url);
        if (port <= 0)
            return;
        if (IsHttpsScheme(url))
        {
            _options.HttpsEnabled = true;
            _options.HttpsPort = (ushort)port;
        }
        else
        {
            _options.Port = (ushort)port;
        }
    }

    private static bool IsHttpsScheme(string url)
    {
        return url.Length >= 6 &&
            url[0] == 'h' && url[1] == 't' && url[2] == 't' &&
            url[3] == 'p' && url[4] == 's' && url[5] == ':';
    }
}

/// <summary>The builder (ASP.NET Core's WebApplicationBuilder shape).</summary>
public sealed class WebApplicationBuilder
{
    private readonly NeutrinoOS.DDK.Web.WebApplicationBuilder _inner;
    private readonly NeutrinoWebHostBuilder _webHost;

    internal WebApplicationBuilder(string[] args)
    {
        _inner = NeutrinoOS.DDK.Web.WebApplication.CreateBuilder(args);
        _webHost = new NeutrinoWebHostBuilder(_inner.Kestrel);
    }

    /// <summary>Service registrations (ASP.NET Core DI shape).</summary>
    public Microsoft.Extensions.DependencyInjection.IServiceCollection Services => _inner.Services;

    /// <summary>The web-host builder (UseUrls).</summary>
    public IWebHostBuilder WebHost => _webHost;

    /// <summary>NeutrinoOS extra: the engine options (tests/tools).</summary>
    public NeutrinoOS.DDK.Web.KestrelServerOptions Kestrel => _inner.Kestrel;

    /// <summary>Build the application (options are frozen here).</summary>
    public WebApplication Build()
    {
        return new WebApplication(_inner.Build());
    }
}

/// <summary>The application (ASP.NET Core's WebApplication shape).</summary>
public sealed class WebApplication : IEndpointRouteBuilder
{
    internal readonly NeutrinoOS.DDK.Web.WebApplication Inner;

    internal WebApplication(NeutrinoOS.DDK.Web.WebApplication inner)
    {
        Inner = inner;
    }

    /// <summary>Create the builder (ASP.NET Core's WebApplication.CreateBuilder).</summary>
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        return new WebApplicationBuilder(args);
    }

    /// <summary>
    /// NeutrinoOS extra: the log/service name (prints as "[name] ...";
    /// defaults to "app" until set).
    /// </summary>
    public string ApplicationName
    {
        get { return Inner.Name; }
        set { Inner.Name = value; }
    }

    /// <summary>The application's service provider (IEndpointRouteBuilder member).</summary>
    public System.IServiceProvider ServiceProvider => Inner.Services;

    /// <summary>Same provider, under the name real WebApplication also exposes.</summary>
    public System.IServiceProvider Services => Inner.Services;

    /// <summary>The application lifetime (registered hooks fire on start/stop).</summary>
    public Microsoft.Extensions.Hosting.IHostApplicationLifetime Lifetime =>
        (Microsoft.Extensions.Hosting.IHostApplicationLifetime)Inner.Lifetime;

    /// <summary>
    /// NeutrinoOS extra: the OpenAPI document JSON for the registered
    /// routes (mirrors NeutrinoOS.DDK.Web.WebApplication).
    /// </summary>
    public string BuildOpenApiJson()
    {
        return Inner.BuildOpenApiJson();
    }

    /// <summary>
    /// Run the application. Hosted mode (launched via `webhost app.dll`):
    /// starts, registers with the kernel idle hook and returns. Foreground
    /// mode pumps until stopped.
    /// </summary>
    public int Run()
    {
        return Inner.Run();
    }
}

/// <summary>Endpoint mapping (the MapGet/MapPost/... extension shapes).</summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>Map a GET handler for a pattern ("/x", "/x/{id}").</summary>
    public static IEndpointRouteBuilder MapGet(this IEndpointRouteBuilder endpoints, string pattern, RequestDelegate handler)
    {
        GetApp(endpoints).Inner.MapGet(pattern, new ShimRouteBridge(handler).InvokeAsync);
        return endpoints;
    }

    /// <summary>Map a POST handler for a pattern.</summary>
    public static IEndpointRouteBuilder MapPost(this IEndpointRouteBuilder endpoints, string pattern, RequestDelegate handler)
    {
        GetApp(endpoints).Inner.MapPost(pattern, new ShimRouteBridge(handler).InvokeAsync);
        return endpoints;
    }

    /// <summary>Map a PUT handler for a pattern.</summary>
    public static IEndpointRouteBuilder MapPut(this IEndpointRouteBuilder endpoints, string pattern, RequestDelegate handler)
    {
        GetApp(endpoints).Inner.MapPut(pattern, new ShimRouteBridge(handler).InvokeAsync);
        return endpoints;
    }

    /// <summary>Map a DELETE handler for a pattern.</summary>
    public static IEndpointRouteBuilder MapDelete(this IEndpointRouteBuilder endpoints, string pattern, RequestDelegate handler)
    {
        GetApp(endpoints).Inner.MapDelete(pattern, new ShimRouteBridge(handler).InvokeAsync);
        return endpoints;
    }

    private static WebApplication GetApp(IEndpointRouteBuilder endpoints)
    {
        var app = endpoints as WebApplication;
        if (app == null)
            throw new System.InvalidOperationException("endpoint builder is not a NeutrinoOS WebApplication");
        return app;
    }
}

/// <summary>
/// Adapts a Microsoft.AspNetCore.Http.RequestDelegate to the engine's
/// async handler shape (wrapping the context per request).
/// </summary>
internal sealed class ShimRouteBridge
{
    private readonly RequestDelegate _handler;

    internal ShimRouteBridge(RequestDelegate handler)
    {
        _handler = handler;
    }

    internal Task InvokeAsync(NeutrinoOS.DDK.Web.HttpContext context)
    {
        var wrapped = new Microsoft.AspNetCore.Http.HttpContext(context);
        return _handler(wrapped);
    }
}
