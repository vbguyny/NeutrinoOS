// hello-webapp - minimal ASP.NET Core app for the NeutrinoOS web surface.
//
// Build:  dotnet build -c Release     (also produces hello-webapp.npkg)
// Run:    dotnet run                  -> http://localhost:5000
//
// On the device, the DDK WebService (the `webhost` command) serves the
// same routing surface; see docs/PHASE6-WEB.md for the porting plan.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => Results.Content(
    "<!doctype html><html><head><title>hello-webapp</title></head><body>" +
    "<h1>Hello from .NET on NeutrinoOS</h1>" +
    "<p>SDK sample web application.</p></body></html>",
    "text/html"));

app.MapGet("/health", () => Results.Json(new { status = "ok" }));

app.MapGet("/hello", (string? name) => Results.Json(new
{
    message = "Hello, " + (string.IsNullOrEmpty(name) ? "world" : name) + "!"
}));

app.Run();
