// NeutrinoOS Web - Kestrel port Milestone 1: the WebApplication surface.
//
// The API an app author writes against. It mirrors ASP.NET Core minimal
// APIs closely enough that the same Program.cs shape works on-device:
//
//   ASP.NET Core (desktop / future milestones):
//     var builder = WebApplication.CreateBuilder(args);
//     var app = builder.Build();
//     app.MapGet("/api/v1/tasks", () => Results.Json(Tasks()));
//     app.Run();
//
//   NeutrinoOS M1 (this file, host driven by the kernel idle tick):
//     var app = WebApplication.CreateBuilder(args);   // builder+app fused
//     app.MapGet("/api/v1/tasks", OnListTasks);       // sync RequestHandler
//     // kernel calls app.Start() / app.Tick() / app.Stop() as a service
//
// app.Run() (a blocking foreground loop) arrives with the async transport
// in M2; today apps are hosted cooperatively so the shell stays usable.

namespace NeutrinoOS.DDK.Web;

/// <summary>The Minimal-API-style application object (M1 subset).</summary>
public sealed class WebApplication
{
    private readonly WebHost _host;

    private WebApplication(WebHost host)
    {
        _host = host;
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
    /// Create the application (ASP.NET Core's CreateBuilder, fused with
    /// Build(); the returned object is both). <paramref name="args"/> is
    /// reserved for configuration in a later milestone.
    /// </summary>
    public static WebApplication CreateBuilder(string[] args)
    {
        return new WebApplication(new WebHost("webapi", 8080));
    }

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

    /// <summary>Start the listener; 0 = ok (-30 no eth0, -32 bind failed).</summary>
    public int Start()
    {
        return _host.Start();
    }

    /// <summary>One bounded work slice (kernel idle hook drives this).</summary>
    public void Tick()
    {
        _host.Tick();
    }

    /// <summary>Stop the listener.</summary>
    public void Stop()
    {
        _host.Stop();
    }
}
