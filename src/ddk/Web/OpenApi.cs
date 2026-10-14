// NeutrinoOS Web - milestone M6: the Microsoft.AspNetCore.OpenApi slice.
//
// The ASP.NET Core 10 OpenAPI flow, mapped onto the Kestrel-port engine
// (see docs/KESTREL-PORT.md M6 and the real docs: "Generate OpenAPI
// documents"):
//
//   var builder = WebApplication.CreateBuilder(args);
//   builder.Services.AddOpenApi();                  // services (doc name "v1")
//   var app = builder.Build();
//   app.MapGet("/api/v1/tasks", OnListTasks);
//   app.MapOpenApi();                               // GET /openapi/v1.json
//   app.Run();
//
// Fidelity with Microsoft.AspNetCore.OpenApi as implemented here:
//   - AddOpenApi() registers the document services; the default document
//     name is "v1" (AddOpenApi("internal") renames it; the last call wins -
//     multiple documents are a documented limitation of this shim).
//   - MapOpenApi() maps the document endpoint; the default route is
//     /openapi/{documentName}.json and MapOpenApi(pattern) overrides it.
//   - The document is generated AT REQUEST TIME from the live route table,
//     as OpenAPI 3.1 JSON (the .NET 10 default), with the URL path
//     parameters ({id}) described as required string path parameters.
//   - Unknown HTTP methods are excluded from the document; the document
//     endpoint itself is excluded too.
//   - Like the other M4/M5 shims this is shape-fidelity for on-device apps,
//     not the full package: transformer APIs (AddOpenApi delegates,
//     WithOpenApi/WithTags/WithName), YAML output, build-time generation
//     and multiple documents are not supported yet.
//
// Unlike the real generator (which introspects handler parameter/return
// types via ApiExplorer), the engine has no such metadata: the routes are
// registered as (method, pattern, handler) triples, so the document
// describes methods + paths + path parameters and a default 200 response.

using NeutrinoOS.DDK.Services;   // WebService's proven string helpers (same assembly)

namespace NeutrinoOS.DDK.Web;

/// <summary>
/// The Microsoft.AspNetCore.OpenApi.OpenApiOptions subset: the document
/// name, the info section and the endpoint route.
/// </summary>
public sealed class OpenApiOptions
{
    /// <summary>Document name (default "v1"; fills {documentName} in routes).</summary>
    public string DocumentName = "v1";

    /// <summary>info.title; null uses the application name (host name).</summary>
    public string Title;

    /// <summary>info.version (default "1.0.0").</summary>
    public string Version = "1.0.0";

    /// <summary>OpenAPI spec version string (.NET 10 default "3.1.0").</summary>
    public string OpenApiVersion = "3.1.0";

    /// <summary>Endpoint route pattern (ASP.NET default; {documentName} fills in).</summary>
    public string DocumentRoute = "/openapi/{documentName}.json";
}

/// <summary>
/// AddOpenApi() (the Microsoft.Extensions.DependencyInjection.
/// OpenApiServiceCollectionExtensions shape) on the M4 DI shim.
/// </summary>
public static class OpenApiServiceCollectionExtensions
{
    /// <summary>ASPNET: builder.Services.AddOpenApi() - document name "v1".</summary>
    public static ServiceCollection AddOpenApi(this ServiceCollection services)
    {
        return AddOpenApi(services, new OpenApiOptions());
    }

    /// <summary>ASPNET: AddOpenApi("internal") - renames the document.</summary>
    public static ServiceCollection AddOpenApi(this ServiceCollection services, string documentName)
    {
        var options = new OpenApiOptions();
        if (documentName != null && documentName.Length > 0)
            options.DocumentName = documentName;
        return AddOpenApi(services, options);
    }

    /// <summary>Register a prepared options instance (customize via the fields).</summary>
    public static ServiceCollection AddOpenApi(this ServiceCollection services, OpenApiOptions options)
    {
        if (services == null)
            return services;
        if (options == null)
            options = new OpenApiOptions();
        services.Add(typeof(OpenApiOptions), options);
        return services;
    }
}

/// <summary>
/// MapOpenApi() (the Microsoft.AspNetCore.Builder.
/// OpenApiEndpointRouteBuilderExtensions shape) on WebApplication: maps the
/// endpoint that serves the generated OpenAPI document.
/// </summary>
public static class OpenApiWebApplicationExtensions
{
    /// <summary>Map the document endpoint (default route /openapi/v1.json).</summary>
    public static WebApplication MapOpenApi(this WebApplication app)
    {
        return MapOpenApi(app, null);
    }

    /// <summary>
    /// Map the document endpoint at a custom route ("{documentName}" is
    /// replaced with the configured document name).
    /// </summary>
    public static WebApplication MapOpenApi(this WebApplication app, string pattern)
    {
        if (app == null)
            return app;

        OpenApiOptions options = app.Services.GetService<OpenApiOptions>();
        if (options == null)
            options = new OpenApiOptions();

        string route = pattern == null || pattern.Length == 0 ? options.DocumentRoute : pattern;
        if (route == null || route.Length == 0)
            route = "/openapi/{documentName}.json";
        route = ResolveDocumentName(route, options.DocumentName);

        app.Host.AddOpenApiRoute(route, options);
        return app;
    }

    /// <summary>Substitute {documentName} in a route pattern.</summary>
    private static string ResolveDocumentName(string pattern, string documentName)
    {
        string name = documentName == null || documentName.Length == 0 ? "v1" : documentName;
        const string token = "{documentName}";

        int index = -1;
        for (int i = 0; i + token.Length <= pattern.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < token.Length; j++)
            {
                if (pattern[i + j] != token[j])
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
            {
                index = i;
                break;
            }
        }
        if (index < 0)
            return pattern;

        return pattern.Substring(0, index) + name +
               pattern.Substring(index + token.Length, pattern.Length - index - token.Length);
    }
}

/// <summary>
/// The document generator: renders the OpenAPI 3.1 JSON for a host's live
/// route table. Called per request by the MapOpenApi endpoint (and by
/// WebApplication.BuildOpenApiJson for tooling/tests).
/// </summary>
internal static class OpenApiDocument
{
    internal static string Build(WebHost host, OpenApiOptions options)
    {
        string specVersion = HasText(options == null ? null : options.OpenApiVersion)
            ? options.OpenApiVersion : "3.1.0";
        string title = HasText(options == null ? null : options.Title)
            ? options.Title : DefaultTitle(host);
        string infoVersion = HasText(options == null ? null : options.Version)
            ? options.Version : "1.0.0";

        var body = "{\n";
        body += "  \"openapi\": \"" + JsonEscape(specVersion) + "\",\n";
        body += "  \"info\": {\n";
        body += "    \"title\": \"" + JsonEscape(title) + "\",\n";
        body += "    \"version\": \"" + JsonEscape(infoVersion) + "\"\n";
        body += "  },\n";
        body += "  \"paths\": {";

        string paths = BuildPaths(host);
        if (paths.Length > 0)
            body += paths;

        // Close "paths", then the root document. (The first M6 cut appended
        // only one brace here, so every real JSON parser rejected the doc
        // with "expected ',' or '}' after property value" at EOF while
        // substring checks still passed - pinned now by json.tool checks in
        // the probes and the brace-balance record in HostingTests.)
        body += "\n  }\n}\n";
        return body;
    }

    /// <summary>The info.title fallback (the real generator uses the app name).</summary>
    private static string DefaultTitle(WebHost host)
    {
        string name = host == null ? null : host.Name;
        if (name == null || name.Length == 0)
            return "NeutrinoOS API";
        return name;
    }

    private static bool HasText(string s)
    {
        return s != null && s.Length > 0;
    }

    // ====================================================================
    // paths
    // ====================================================================

    /// <summary>
    /// The "paths" object interior: one path item per distinct pattern
    /// (first registration wins the position), one operation per route,
    /// the document endpoint itself excluded.
    /// </summary>
    private static string BuildPaths(WebHost host)
    {
        if (host == null)
            return "";

        int count = host.RouteCount;
        var body = "";
        for (int i = 0; i < count; i++)
        {
            if (host.RouteKindAt(i) == WebHost.RouteKindOpenApi)
                continue;
            string pattern = host.RoutePatternAt(i);
            if (!IsFirstWithPattern(host, pattern, i))
                continue;

            var item = "\n    \"" + JsonEscape(pattern) + "\": {";
            bool firstOp = true;
            for (int j = i; j < count; j++)
            {
                if (host.RouteKindAt(j) == WebHost.RouteKindOpenApi)
                    continue;
                if (!WebService.StrEq(host.RoutePatternAt(j), pattern))
                    continue;
                string operation = OperationName(host.RouteMethodAt(j));
                if (operation == null)
                    continue;   // unknown methods are excluded (ASP.NET behavior)
                if (!firstOp)
                    item += ",";
                firstOp = false;
                item += BuildOperation(operation, pattern);
            }
            if (firstOp)
                continue;   // no supported operations: exclude the path entirely

            item += "\n    }";
            if (body.Length > 0)
                body += ",";
            body += item;
        }
        return body;
    }

    /// <summary>True when no earlier (non-document) route uses this pattern.</summary>
    private static bool IsFirstWithPattern(WebHost host, string pattern, int index)
    {
        for (int k = 0; k < index; k++)
        {
            if (host.RouteKindAt(k) == WebHost.RouteKindOpenApi)
                continue;
            if (WebService.StrEq(host.RoutePatternAt(k), pattern))
                return false;
        }
        return true;
    }

    /// <summary>One path item operation (parameters + default 200 response).</summary>
    private static string BuildOperation(string operation, string pattern)
    {
        var body = "\n      \"" + operation + "\": {";
        string parameters = BuildParameters(pattern);
        if (parameters.Length > 0)
            body += "\n" + parameters;
        body += "\n        \"responses\": { \"200\": { \"description\": \"OK\" } }\n      }";
        return body;
    }

    /// <summary>The parameters array for {name} segments, or "" when none.</summary>
    private static string BuildParameters(string pattern)
    {
        var names = new string[16];
        int nameCount = 0;
        int i = 0;
        while (i < pattern.Length && nameCount < names.Length)
        {
            if (pattern[i] != '{')
            {
                i++;
                continue;
            }
            int close = -1;
            for (int k = i + 1; k < pattern.Length; k++)
            {
                if (pattern[k] == '}')
                {
                    close = k;
                    break;
                }
            }
            if (close < 0)
                break;
            if (close > i + 1)
            {
                names[nameCount] = pattern.Substring(i + 1, close - i - 1);
                nameCount++;
            }
            i = close + 1;
        }
        if (nameCount == 0)
            return "";

        var body = "        \"parameters\": [\n";
        for (int k = 0; k < nameCount; k++)
        {
            if (k > 0)
                body += ",\n";
            body += "          { \"name\": \"" + JsonEscape(names[k]) +
                    "\", \"in\": \"path\", \"required\": true, " +
                    "\"schema\": { \"type\": \"string\" } }";
        }
        body += "\n        ],";
        return body;
    }

    /// <summary>The OpenAPI operation key for a method, or null (excluded).</summary>
    private static string OperationName(string method)
    {
        if (WebService.StrEq(method, "GET")) return "get";
        if (WebService.StrEq(method, "POST")) return "post";
        if (WebService.StrEq(method, "PUT")) return "put";
        if (WebService.StrEq(method, "DELETE")) return "delete";
        if (WebService.StrEq(method, "PATCH")) return "patch";
        if (WebService.StrEq(method, "HEAD")) return "head";
        if (WebService.StrEq(method, "OPTIONS")) return "options";
        if (WebService.StrEq(method, "TRACE")) return "trace";
        return null;
    }

    /// <summary>Escape a string for embedding inside JSON quotes.</summary>
    private static string JsonEscape(string s)
    {
        if (s == null || s.Length == 0)
            return "";
        var chars = new char[s.Length * 2 + 4];
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"' || c == '\\')
            {
                chars[n++] = '\\';
                chars[n++] = c;
            }
            else if (c == '\n')
            {
                chars[n++] = '\\';
                chars[n++] = 'n';
            }
            else if (c == '\r')
            {
                chars[n++] = '\\';
                chars[n++] = 'r';
            }
            else if (c == '\t')
            {
                chars[n++] = '\\';
                chars[n++] = 't';
            }
            else if (c < 32)
            {
                // Drop other control characters (JSON requires escapes).
            }
            else
            {
                chars[n++] = c;
            }
        }
        return new string(chars, 0, n);
    }
}
