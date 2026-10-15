// NeutrinoOS Web - Kestrel port milestone M5: the host lifetime.
//
// The ASP.NET Core IHostApplicationLifetime subset app authors use:
//
//   var app = builder.Build();
//   app.Lifetime.ApplicationStarted.Register(OnStarted);
//   app.Lifetime.ApplicationStopping.Register(OnStopping);
//   ...
//   app.Lifetime.StopApplication();   // request a graceful stop
//
// The engine fires Started once the listener(s) bind, Stopping when a
// stop begins and Stopped once the listeners are down. StopApplication()
// routes through WebApplication.Stop(), so a hosted app also releases
// its kernel service registration (equivalent to `webhost stop`).
//
// The class implements BOTH NeutrinoOS.DDK.Web.IHostApplicationLifetime
// and Microsoft.Extensions.Hosting.IHostApplicationLifetime (identical
// member shapes): the Microsoft.AspNetCore shim exposes the latter.

using System.Threading;

namespace NeutrinoOS.DDK.Web;

/// <summary>The IHostApplicationLifetime subset (see file header).</summary>
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

/// <summary>Default lifetime implementation (see file header).</summary>
public sealed class HostLifetime : IHostApplicationLifetime,
    Microsoft.Extensions.Hosting.IHostApplicationLifetime
{
    private readonly CancellationTokenSource _started = new CancellationTokenSource();
    private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
    private readonly CancellationTokenSource _stopped = new CancellationTokenSource();
    private bool _stopRequested;

    /// <summary>The application this lifetime belongs to (stop requests route here).</summary>
    internal WebApplication? Owner;

    /// <inheritdoc/>
    public CancellationToken ApplicationStarted => _started.Token;

    /// <inheritdoc/>
    public CancellationToken ApplicationStopping => _stopping.Token;

    /// <inheritdoc/>
    public CancellationToken ApplicationStopped => _stopped.Token;

    /// <summary>True after a successful start and before any stop begins.</summary>
    public bool IsStarted => _started.IsCancellationRequested && !_stopping.IsCancellationRequested;

    /// <summary>True once a stop was requested or began.</summary>
    public bool IsStopping => _stopRequested || _stopping.IsCancellationRequested;

    /// <inheritdoc/>
    public void StopApplication()
    {
        _stopRequested = true;
        if (Owner != null)
            Owner.Stop();   // fires the stopping/stopped notifications
    }

    /// <summary>Engine hook: the listener(s) bound successfully.</summary>
    public void NotifyStarted()
    {
        if (!_started.IsCancellationRequested)
            _started.Cancel();
    }

    /// <summary>Engine hook: a stop began.</summary>
    public void NotifyStopping()
    {
        if (!_stopping.IsCancellationRequested)
            _stopping.Cancel();
    }

    /// <summary>Engine hook: the listeners are down.</summary>
    public void NotifyStopped()
    {
        if (!_stopped.IsCancellationRequested)
            _stopped.Cancel();
    }
}
