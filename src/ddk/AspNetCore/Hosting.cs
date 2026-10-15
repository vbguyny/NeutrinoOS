// NeutrinoOS - ASP.NET Core namespace compatibility layer (M7):
// Microsoft.Extensions.Hosting.
//
// The lifetime surface minimal-API apps use:
//
//   app.Lifetime.ApplicationStarted.Register(OnStarted);
//   app.Lifetime.StopApplication();
//
// The engine type (NeutrinoOS.DDK.Web.HostLifetime) implements BOTH
// this interface and the DDK's own IHostApplicationLifetime - identical
// member shapes - so the shim's WebApplication.Lifetime can hand out
// the Microsoft.Extensions.Hosting-typed view directly.

using System.Threading;

namespace Microsoft.Extensions.Hosting;

/// <summary>The application lifetime (ASP.NET Core shape; see HostLifetime).</summary>
public interface IHostApplicationLifetime
{
    /// <summary>Fires once the application has started.</summary>
    CancellationToken ApplicationStarted { get; }

    /// <summary>Fires when a stop begins.</summary>
    CancellationToken ApplicationStopping { get; }

    /// <summary>Fires once the application has stopped.</summary>
    CancellationToken ApplicationStopped { get; }

    /// <summary>Request a graceful stop of the application.</summary>
    void StopApplication();
}
