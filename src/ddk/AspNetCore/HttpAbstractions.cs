// NeutrinoOS - ASP.NET Core namespace compatibility layer (M7).
//
// These types live in the SAME namespaces as the real framework
// (Microsoft.AspNetCore.Http) so that an app written against ASP.NET
// Core minimal APIs compiles unchanged against the DDK (for NeutrinoOS)
// and against the real Microsoft.AspNetCore.App (for Windows). The
// implementation forwards to the NeutrinoOS web engine
// (NeutrinoOS.DDK.Web); see docs/KESTREL-PORT.md M7.
//
// Scope of this first slice (documented in KESTREL-PORT.md):
//   - HttpContext/HttpRequest/HttpResponse with the minimal-API shapes:
//     Method, Path, QueryString, Query["k"], RouteValues["k"],
//     Response.StatusCode/ContentType/WriteAsync(string).
//   - RequestDelegate (Task-returning) is the only handler shape -
//     lambda/RequestDelegateFactory overloads (Results, parameter
//     binding) are later milestones.
//   - IQueryCollection/IRouteValueDictionary interfaces are NOT
//     implemented yet: the concrete shapes with the same members are
//     provided (code that uses the members compiles; code that stores
//     the interface type does not).

using System.Threading.Tasks;

namespace Microsoft.AspNetCore.Http;

/// <summary>A request handler (ASP.NET Core's RequestDelegate shape).</summary>
public delegate Task RequestDelegate(HttpContext context);

/// <summary>One HTTP request/response pair (wraps the NeutrinoOS engine context).</summary>
public sealed class HttpContext
{
    private readonly NeutrinoOS.DDK.Web.HttpContext _inner;
    private readonly HttpRequest _request;
    private readonly HttpResponse _response;

    /// <summary>Wraps an engine context (runtime plumbing; also used by tests).</summary>
    public HttpContext(NeutrinoOS.DDK.Web.HttpContext inner)
    {
        _inner = inner;
        _request = new HttpRequest(inner.Request);
        _response = new HttpResponse(inner.Response);
    }

    /// <summary>The inbound request.</summary>
    public HttpRequest Request => _request;

    /// <summary>The outbound response.</summary>
    public HttpResponse Response => _response;
}

/// <summary>The inbound request as seen by a handler.</summary>
public sealed class HttpRequest
{
    private readonly NeutrinoOS.DDK.Web.HttpRequest _inner;
    private readonly QueryCollection _query;
    private readonly RouteValueDictionary _routeValues;

    internal HttpRequest(NeutrinoOS.DDK.Web.HttpRequest inner)
    {
        _inner = inner;
        _query = new QueryCollection(inner);
        _routeValues = new RouteValueDictionary(inner);
    }

    /// <summary>HTTP method, e.g. "GET".</summary>
    public string Method => _inner.Method;

    /// <summary>Path only (query string removed).</summary>
    public PathString Path => new PathString(_inner.Path);

    /// <summary>Raw query string without '?', or "".</summary>
    public QueryString QueryString => new QueryString(_inner.QueryString);

    /// <summary>The parsed query string: Query["msg"].</summary>
    public QueryCollection Query => _query;

    /// <summary>Values captured by "{name}" route segments.</summary>
    public RouteValueDictionary RouteValues => _routeValues;
}

/// <summary>The outbound response a handler fills in.</summary>
public sealed class HttpResponse
{
    private readonly NeutrinoOS.DDK.Web.HttpResponse _inner;

    internal HttpResponse(NeutrinoOS.DDK.Web.HttpResponse inner)
    {
        _inner = inner;
    }

    /// <summary>Status code; defaults to 200.</summary>
    public int StatusCode
    {
        get { return _inner.StatusCode; }
        set { _inner.StatusCode = value; }
    }

    /// <summary>Content-Type header; defaults to text/plain.</summary>
    public string ContentType
    {
        get { return _inner.ContentType; }
        set { _inner.ContentType = value; }
    }

    /// <summary>Append a chunk to the response body (extension-method shape).</summary>
    public Task WriteAsync(string text)
    {
        _inner.Write(text);
        return Task.CompletedTask;
    }
}

/// <summary>The query string as a keyed collection: Query["msg"].</summary>
public sealed class QueryCollection
{
    private readonly NeutrinoOS.DDK.Web.HttpRequest _inner;

    internal QueryCollection(NeutrinoOS.DDK.Web.HttpRequest inner)
    {
        _inner = inner;
    }

    /// <summary>The first value for a key (StringValues.None when absent).</summary>
    public StringValues this[string key]
    {
        get { return new StringValues(_inner.Query(key)); }
    }

    /// <summary>True when the key is present; the value comes back in StringValues.</summary>
    public bool TryGetValue(string key, out StringValues value)
    {
        string v = _inner.Query(key);
        value = new StringValues(v);
        return v != null;
    }
}

/// <summary>The "{name}" route values: RouteValues["id"].</summary>
public sealed class RouteValueDictionary
{
    private readonly NeutrinoOS.DDK.Web.HttpRequest _inner;

    internal RouteValueDictionary(NeutrinoOS.DDK.Web.HttpRequest inner)
    {
        _inner = inner;
    }

    /// <summary>The captured value for a route segment name, or null (boxed string).</summary>
    public object this[string key]
    {
        get { return _inner.Route(key); }
    }

    /// <summary>True when the route captured the key.</summary>
    public bool TryGetValue(string key, out object value)
    {
        value = _inner.Route(key);
        return value != null;
    }
}

/// <summary>The path portion of the request URL (BCL-shape struct).</summary>
public struct PathString
{
    private string _value;

    public PathString(string value)
    {
        _value = value;
    }

    /// <summary>True when the path is non-empty.</summary>
    public bool HasValue => _value != null && _value.Length > 0;

    /// <summary>The raw path string.</summary>
    public string Value => _value;

    public static implicit operator string(PathString path) => path._value;

    public static implicit operator PathString(string value) => new PathString(value);

    public override string ToString() => _value == null ? "" : _value;
}

/// <summary>The raw query string (BCL-shape struct; "?" prefix preserved).</summary>
public struct QueryString
{
    private string _value;

    public QueryString(string value)
    {
        _value = value;
    }

    /// <summary>True when the query string is non-empty.</summary>
    public bool HasValue => _value != null && _value.Length > 0;

    /// <summary>The raw query string.</summary>
    public string Value => _value;

    public static implicit operator string(QueryString q) => q._value;

    public static implicit operator QueryString(string value) => new QueryString(value);

    public override string ToString()
    {
        if (_value == null || _value.Length == 0)
            return "";
        return "?" + _value;
    }
}

/// <summary>One-or-more string values (minimal single-value slice).</summary>
public struct StringValues
{
    private string _value;

    public StringValues(string value)
    {
        _value = value;
    }

    /// <summary>Number of values (0 or 1 in this slice).</summary>
    public int Count => _value == null ? 0 : 1;

    /// <summary>The i-th value (null past the end).</summary>
    public string this[int index] => index == 0 ? _value : null;

    /// <summary>The single value (null when absent).</summary>
    public string Value => _value;

    public static implicit operator string(StringValues values) => values._value;

    public static implicit operator StringValues(string value) => new StringValues(value);

    public override string ToString() => _value == null ? "" : _value;
}
