// NeutrinoOS Phase 6 - minimal web app template (ASP.NET Core minimal API).
//
// This template shows the target interface for .NET web hosting on
// NeutrinoOS. It builds on desktop .NET 10 exactly like any ASP.NET Core
// app (dotnet run -> http://localhost:5000).
//
// IMPORTANT (Phase 6 scope): full Kestrel porting to NeutrinoOS is a
// Phase 7+ goal. On the device itself, the DDK WebService (started with
// the `webhost` command) serves the SAME routing surface as this
// template:  "/", "/health", "/time" plus static files from /var/www.
// See docs/PHASE6-WEB.md for the porting plan and the fallback server.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => Results.Content(
    "<!doctype html><html><head><title>NeutrinoOS</title></head><body>" +
    "<h1>Hello from .NET on NeutrinoOS</h1>" +
    "<p>Minimal API template for Phase 6 web hosting.</p></body></html>",
    "text/html"));

app.MapGet("/health", () => Results.Json(new { status = "ok" }));

app.MapGet("/time", () => Results.Json(new
{
    utc = DateTime.UtcNow.ToString("O"),
    uptimeSeconds = Environment.TickCount64 / 1000
}));

app.UseStaticFiles();

app.Run();
