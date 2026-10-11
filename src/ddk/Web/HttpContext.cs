// NeutrinoOS Web - Kestrel port Milestone 1: the application-facing surface.
//
// This file provides the request/response objects and the handler delegate
// for WebApplication-style apps (see WebApplication.cs). The shape mirrors
// ASP.NET Core minimal APIs so that app code keeps the same look:
//
//   app.MapGet("/api/v1/tasks", OnListTasks);
//   static void OnListTasks(HttpContext ctx) { ctx.Response.Json(TasksJson()); }
//
// Mapping to ASP.NET Core (for when the real Kestrel port lands, see
// docs/KESTREL-PORT.md):
//   HttpContext          <-> Microsoft.AspNetCore.Http.HttpContext
//   HttpRequest.Query    <-> HttpRequest.Query
//   HttpRequest.Route    <-> HttpRequest.RouteValues
//   HttpResponse.Json    <-> Results.Json + HttpContext.Response
//   RequestHandler       <-> RequestDelegate (sync subset in M1)

using NeutrinoOS.DDK.Services;   // WebService's proven string helpers (same assembly)

namespace NeutrinoOS.DDK.Web;

/// <summary>A request handler (the M1 synchronous subset of RequestDelegate).</summary>
public delegate void RequestHandler(HttpContext context);

/// <summary>
/// The M3 slice of Kestrel's <c>IHttpRequestFeature</c>
/// (Microsoft.AspNetCore.Http.Features.IHttpRequestFeature): the pieces the
/// engine fills in before a handler runs. Handlers keep using the concrete
/// HttpRequest; this is the feature-shaped view (and the port target for
/// M4's feature collection).
/// </summary>
public interface IHttpRequestFeature
{
    /// <summary>HTTP method, e.g. "GET".</summary>
    string Method { get; set; }

    /// <summary>Path only (query string removed).</summary>
    string Path { get; set; }

    /// <summary>Raw query string without '?', or "".</summary>
    string QueryString { get; set; }

    /// <summary>Request body decoded as Latin-1 text ("" when absent).</summary>
    string Body { get; set; }
}

/// <summary>
/// The M3 slice of Kestrel's <c>IHttpResponseFeature</c>: status + the
/// write-side surface handlers already use (Write/Json/Text).
/// </summary>
public interface IHttpResponseFeature
{
    /// <summary>Status code; defaults to 200.</summary>
    int StatusCode { get; set; }

    /// <summary>Content-Type header; defaults to text/plain.</summary>
    string ContentType { get; set; }

    /// <summary>The accumulated body ("" when nothing was written).</summary>
    string Body { get; }

    /// <summary>Append a chunk to the body.</summary>
    void Write(string text);

    /// <summary>Set the body to a JSON document and Content-Type to application/json.</summary>
    void Json(string json);
}

/// <summary>One HTTP request/response pair handed to a route handler.</summary>
public sealed class HttpContext
{
    /// <summary>The inbound request.</summary>
    public readonly HttpRequest Request;

    /// <summary>The outbound response (write to it in the handler).</summary>
    public readonly HttpResponse Response;

    /// <summary>Creates the context; hosts build this per request.</summary>
    public HttpContext()
    {
        Request = new HttpRequest();
        Response = new HttpResponse();
    }

    /// <summary>The request as an IHttpRequestFeature (feature-shaped view).</summary>
    public IHttpRequestFeature RequestFeature
    {
        get { return Request; }
    }

    /// <summary>The response as an IHttpResponseFeature (feature-shaped view).</summary>
    public IHttpResponseFeature ResponseFeature
    {
        get { return Response; }
    }
}

/// <summary>The inbound request as seen by a handler.</summary>
public sealed class HttpRequest : IHttpRequestFeature
{
    /// <summary>HTTP method, e.g. "GET" (always uppercase from clients).</summary>
    public string Method;

    /// <summary>Path only (query string removed), e.g. "/api/v1/tasks".</summary>
    public string Path;

    /// <summary>Raw query string without '?', or "".</summary>
    public string QueryString;

    /// <summary>Request body decoded as Latin-1 text ("" when absent).</summary>
    public string Body;

    // Route captures filled by the host for {name} pattern segments.
    private string[] _routeNames;
    private string[] _routeValues;
    private int _routeCount;

    /// <summary>Called by the host before the handler runs.</summary>
    public void SetRoute(string[] names, string[] values, int count)
    {
        _routeNames = names;
        _routeValues = values;
        _routeCount = count;
    }

    /// <summary>Value of a {name} segment in the matched route, or null.</summary>
    public string Route(string name)
    {
        if (_routeNames == null)
            return null;
        for (int i = 0; i < _routeCount; i++)
        {
            if (WebService.StrEq(_routeNames[i], name))
                return _routeValues[i];
        }
        return null;
    }

    /// <summary>Value of a query-string key ("a=1&amp;b=x" style), or null.</summary>
    public string Query(string key)
    {
        string query = QueryString;
        if (query == null || query.Length == 0)
            return null;
        string needle = key + "=";
        int start = 0;
        while (start <= query.Length)
        {
            int amp = WebService.IndexOfChar2(query, '&', start);
            int end = amp < 0 ? query.Length : amp;
            if (end - start >= needle.Length)
            {
                bool match = true;
                for (int i = 0; i < needle.Length; i++)
                {
                    if (query[start + i] != needle[i])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    var chars = new char[end - start - needle.Length];
                    for (int i = start + needle.Length; i < end; i++)
                        chars[i - start - needle.Length] = query[i] == '+' ? ' ' : query[i];
                    return new string(chars);
                }
            }
            if (amp < 0)
                break;
            start = amp + 1;
        }
        return null;
    }

    // ---- IHttpRequestFeature (explicit: handlers keep using the fields) ----

    string IHttpRequestFeature.Method
    {
        get { return Method; }
        set { Method = value; }
    }

    string IHttpRequestFeature.Path
    {
        get { return Path; }
        set { Path = value; }
    }

    string IHttpRequestFeature.QueryString
    {
        get { return QueryString; }
        set { QueryString = value; }
    }

    string IHttpRequestFeature.Body
    {
        get { return Body; }
        set { Body = value; }
    }
}

/// <summary>The outbound response a handler fills in.</summary>
public sealed class HttpResponse : IHttpResponseFeature
{
    /// <summary>Status code; defaults to 200.</summary>
    public int StatusCode;

    /// <summary>Content-Type header; defaults to text/plain.</summary>
    public string ContentType;

    private string _body;

    /// <summary>Creates a response with the 200/text-plain defaults.</summary>
    public HttpResponse()
    {
        StatusCode = 200;
        ContentType = "text/plain";
    }

    /// <summary>The accumulated body ("" when nothing was written).</summary>
    public string Body => _body == null ? "" : _body;

    /// <summary>Append a chunk to the body (string concatenation, small payloads).</summary>
    public void Write(string text)
    {
        if (text == null || text.Length == 0)
            return;
        _body = _body == null ? text : _body + text;
    }

    /// <summary>Set the body to a JSON document and Content-Type to application/json.</summary>
    public void Json(string json)
    {
        ContentType = "application/json";
        _body = json == null ? "" : json;
    }

    /// <summary>Set the body to text with an explicit content type.</summary>
    public void Text(string text, string contentType)
    {
        ContentType = contentType;
        _body = text == null ? "" : text;
    }

    /// <summary>Set the status code (chainable body via Write/Json before or after).</summary>
    public void Status(int code)
    {
        StatusCode = code;
    }

    // ---- IHttpResponseFeature (explicit: handlers keep using the members) ----

    int IHttpResponseFeature.StatusCode
    {
        get { return StatusCode; }
        set { StatusCode = value; }
    }

    string IHttpResponseFeature.ContentType
    {
        get { return ContentType; }
        set { ContentType = value; }
    }

    string IHttpResponseFeature.Body
    {
        get { return Body; }
    }

    void IHttpResponseFeature.Write(string text)
    {
        Write(text);
    }

    void IHttpResponseFeature.Json(string json)
    {
        Json(json);
    }
}

/// <summary>Reason-phrase lookup for the status line.</summary>
public static class WebStatus
{
    /// <summary>Canonical reason phrase for a status code ("OK" when unknown).</summary>
    public static string TextFor(int code)
    {
        if (code == 200) return "OK";
        if (code == 201) return "Created";
        if (code == 204) return "No Content";
        if (code == 301) return "Moved Permanently";
        if (code == 302) return "Found";
        if (code == 304) return "Not Modified";
        if (code == 400) return "Bad Request";
        if (code == 401) return "Unauthorized";
        if (code == 403) return "Forbidden";
        if (code == 404) return "Not Found";
        if (code == 405) return "Method Not Allowed";
        if (code == 409) return "Conflict";
        if (code == 413) return "Payload Too Large";
        if (code == 415) return "Unsupported Media Type";
        if (code == 422) return "Unprocessable Entity";
        if (code == 431) return "Request Header Fields Too Large";
        if (code == 500) return "Internal Server Error";
        if (code == 501) return "Not Implemented";
        if (code == 503) return "Service Unavailable";
        return "OK";
    }
}
