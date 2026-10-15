// NeutrinoOS - ASP.NET Core namespace compatibility layer (M7):
// Microsoft.AspNetCore.Hosting.
//
// In the real framework UseUrls is an extension on IWebHostBuilder
// (WebHostBuilderExtensions in Microsoft.AspNetCore.Hosting); the shim
// mirrors that layout so `builder.WebHost.UseUrls("...")` - with the
// same `using Microsoft.AspNetCore.Hosting;` - compiles unchanged on
// Windows/ASP.NET Core and on NeutrinoOS.
//
// Supported url shapes: "http://0.0.0.0:8092", "https://0.0.0.0:8443"
// or a bare port. The https scheme enables the TLS 1.3 listener on that
// port (M5); host names are ignored - the engine binds all interfaces,
// matching the M4 --urls behaviour.

namespace Microsoft.AspNetCore.Hosting;

/// <summary>UseUrls for the device web-host builder (framework parity).</summary>
public static class WebHostBuilderExtensions
{
    /// <summary>
    /// Bind the endpoints from URLs like "http://0.0.0.0:8092" /
    /// "https://0.0.0.0:8443".
    /// </summary>
    public static Microsoft.AspNetCore.Builder.IWebHostBuilder UseUrls(
        this Microsoft.AspNetCore.Builder.IWebHostBuilder hostBuilder, params string[] urls)
    {
        var device = hostBuilder as Microsoft.AspNetCore.Builder.NeutrinoWebHostBuilder;
        if (device == null)
            throw new System.InvalidOperationException("web host builder is not a NeutrinoOS builder");
        if (urls != null)
        {
            for (int i = 0; i < urls.Length; i++)
                device.UseUrl(urls[i]);
        }
        return hostBuilder;
    }
}
