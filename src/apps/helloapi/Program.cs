// NeutrinoOS M7 sample - ASP.NET Core namespace compatibility.
//
// THE point of this file: it is ordinary ASP.NET Core minimal-API code
// (Microsoft.AspNetCore.* + Microsoft.Extensions.* namespaces only). The
// SAME source file compiles and runs on Windows/ASP.NET Core
// (samples/aspnet-compat/helloapi.windows) and on NeutrinoOS against the
// DDK's namespace shim (see docs/KESTREL-PORT.md M7). Nothing
// NeutrinoOS-specific below the header.
//
// Endpoints:
//   GET /                greeting (service-resolved prefix)
//   GET /health          liveness {"status":"ok"}
//   GET /greet/{name}    route values {"greeting":"hello","name":"..."}
//   GET /echo?msg=hi     query strings -> "echo:hi"
//
// On the device it is deployed to /apps/webapp/helloapi.dll and launched:
//   webhost /apps/webapp/helloapi.dll &
// and reached from Windows 11 with
//   -netdev user,id=n0,hostfwd=tcp::18092-:8092
// then http://127.0.0.1:18092 .

using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace HelloApi;

/// <summary>A service registered in the builder's DI container (minimal-API style).</summary>
public sealed class Greeting
{
    /// <summary>The prefix the / endpoint replies with.</summary>
    public string Prefix = "Hello from ASP.NET Core shape!";
}

/// <summary>The hello ASP.NET Core API (see file header).</summary>
public static class Program
{
    /// <summary>Entry point (device loader / dotnet both find this Main).</summary>
    public static int Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<Greeting>(new Greeting());
        builder.WebHost.UseUrls("http://0.0.0.0:8092");

        var app = builder.Build();
        var greeting = app.Services.GetRequiredService<Greeting>();

        app.MapGet("/", Root);
        app.MapGet("/health", Health);
        app.MapGet("/greet/{name}", (HttpContext ctx) => Greet(ctx, greeting));
        app.MapGet("/echo", Echo);

        app.Run();
        return 0;
    }

    private static Task Root(HttpContext ctx)
    {
        return ctx.Response.WriteAsync("Hello from ASP.NET Core shape!");
    }

    private static Task Health(HttpContext ctx)
    {
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync("{\"status\":\"ok\"}");
    }

    private static Task Greet(HttpContext ctx, Greeting greeting)
    {
        var name = ctx.Request.RouteValues["name"] as string;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(
            "{\"greeting\":\"hello\",\"name\":\"" + (name ?? "world") + "\"}");
    }

    private static Task Echo(HttpContext ctx)
    {
        string msg = ctx.Request.Query["msg"];
        return ctx.Response.WriteAsync("echo:" + (msg ?? ""));
    }
}
