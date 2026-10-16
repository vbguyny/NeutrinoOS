// NeutrinoOS M8 sample - NuGet/third-party packages on NeutrinoOS.
//
// THE point of this file: it consumes real NuGet packages compiled for
// .NET 10 - Microsoft.Extensions.ObjectPool and
// Microsoft.Extensions.Primitives (both lib/net10.0 assets) - and the
// SAME source runs on Windows/ASP.NET Core
// (samples/nuget-compat/libapi.windows) and on NeutrinoOS, where the
// package dlls ship to ::/lib and the kernel resolves them by assembly
// name at first use (see docs/NUGET.md).
//
// Endpoints:
//   GET /          blurb
//   GET /strings   StringValues/StringSegment behaviour (Primitives)
//   GET /pool      ObjectPool demo (lease/return/reuse + pooled writer)
//   GET /health    liveness
//
// On the device it is deployed to /apps/webapp/libapi.dll and launched:
//   webhost /apps/webapp/libapi.dll &
// and reached from Windows 11 with
//   -netdev user,id=n0,hostfwd=tcp::18098-:8098

using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Primitives;

namespace LibApi;

/// <summary>Per-app state with the shared pooled StringBuilder supply.</summary>
public sealed class AppState
{
    /// <summary>Pool reused across requests (deterministic; retained = 2).</summary>
    public ObjectPool<StringBuilder> Buffers;
}

/// <summary>The libapi sample (see file header).</summary>
public static class Program
{
    /// <summary>Entry point (device loader / dotnet both find this Main).</summary>
    public static int Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls("http://0.0.0.0:8098");

        var provider = new DefaultObjectPoolProvider { MaximumRetained = 2 };
        var state = new AppState
        {
            Buffers = provider.Create(new StringBuilderPooledObjectPolicy()),
        };
        builder.Services.AddSingleton<AppState>(state);

        var app = builder.Build();
        var st = app.Services.GetRequiredService<AppState>();

        app.MapGet("/", Root);
        app.MapGet("/strings", Strings);
        app.MapGet("/pool", (HttpContext ctx) => Pool(ctx, st));
        app.MapGet("/health", Health);

        app.Run();
        return 0;
    }

    private static Task Root(HttpContext ctx)
    {
        return ctx.Response.WriteAsync(
            "libapi: Microsoft.Extensions.ObjectPool + Primitives on NeutrinoOS");
    }

    private static Task Health(HttpContext ctx)
    {
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync("{\"status\":\"ok\"}");
    }

    private static Task Strings(HttpContext ctx)
    {
        // Fully qualified: the web-host template also references Microsoft.AspNetCore.Http,
        // which re-declares StringValues and makes the short name ambiguous.
        var values = new Microsoft.Extensions.Primitives.StringValues(new string[] { "alpha", "beta", "gamma" });
        var segment = new StringSegment("hello world", 0, 5);

        bool starts = segment.StartsWith("hello", StringComparison.Ordinal);
        bool equals = segment.Equals(new StringSegment("hello"), StringComparison.Ordinal);
        int at = segment.IndexOf('l');

        // NOTE: StringValues.ToString() routes through string.Create<TState>(...)
        // with a SpanAction delegate, which the device JIT does not support yet
        // (documented boundary). Joining via ToArray + string.Join exercises the
        // same data and produces identical output.
        string joined = string.Join(",", values.ToArray());

        return ctx.Response.WriteAsync(
            "count=" + values.Count.ToString() +
            ";joined=" + joined +
            ";segment=" + segment.Value +
            ";starts=" + (starts ? "true" : "false") +
            ";equals=" + (equals ? "true" : "false") +
            ";indexof=" + at.ToString());
    }

    private static Task Pool(HttpContext ctx, AppState st)
    {
        StringBuilder first = st.Buffers.Get();
        first.Append("pooled-buffer");
        st.Buffers.Return(first);

        StringBuilder second = st.Buffers.Get();
        bool reused = ReferenceEquals(first, second);
        // The pooled policy clears the builder on Return; content appended
        // after the second lease proves the buffer is usable again.
        second.Append("after-reuse");
        string text = second.ToString();
        st.Buffers.Return(second);

        return ctx.Response.WriteAsync(
            "reused=" + (reused ? "true" : "false") + ";text=" + text);
    }
}
