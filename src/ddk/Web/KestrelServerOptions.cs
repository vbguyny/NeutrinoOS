// NeutrinoOS Web - Kestrel port milestone M4: the server options surface.
//
// The Kestrel-shaped configuration object for the WebHost engine: the
// endpoint (port) plus the engine limits that used to be compile-time
// constants. App authors configure it through the builder:
//
//   var builder = WebApplication.CreateBuilder(args);   // --urls sets Port
//   builder.Kestrel.MaxConnections = 8;
//   var app = builder.Build();                          // options frozen in
//
// The defaults are the values the engine shipped with before M4, so an
// app that never touches the options behaves exactly as before.

namespace NeutrinoOS.DDK.Web;

/// <summary>Kestrel-lite server options (see file header).</summary>
public sealed class KestrelServerOptions
{
    /// <summary>TCP port to bind (default 8080; <c>--urls</c> overrides it).</summary>
    public ushort Port = 8080;

    /// <summary>Connection pool size (1..16; the engine clamps it).</summary>
    public int MaxConnections = 4;

    /// <summary>Request buffer size in bytes (head + body must fit).</summary>
    public int MaxRequestBytes = 4096;

    /// <summary>Maximum registered routes.</summary>
    public int MaxRoutes = 32;

    /// <summary>Route/path segment array size.</summary>
    public int MaxPathSegments = 8;

    /// <summary>Keep-alive request cap per connection before it closes.</summary>
    public int MaxRequestsPerConnection = 100;

    /// <summary>Idle keep-alive timeout in milliseconds.</summary>
    public ulong KeepAliveTimeoutMs = 10000;

    /// <summary>Enable the HTTPS (TLS 1.3) listener (M5).</summary>
    public bool HttpsEnabled = false;

    /// <summary>
    /// HTTPS port (M5; default 8443). Uses the shared /etc/ssl Ed25519
    /// certificate, generated on first use by WebService.EnsureCertificate.
    /// </summary>
    public ushort HttpsPort = 8443;
}
