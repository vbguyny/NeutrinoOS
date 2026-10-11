// NeutrinoOS DDK - connection listener interfaces (Kestrel port milestone M2)
//
// Kestrel's transport abstraction, adapted to the cooperative DDK stack:
// BindAsync returns a bound listener; AcceptAsync hands out ConnectionContexts;
// UnbindAsync/DisposeAsync release the port. The concrete socket-backed
// implementation is SocketConnectionListener.

using System.Threading.Tasks;

namespace NeutrinoOS.DDK.Connections;

/// <summary>A bound endpoint accepting server-side connections.</summary>
public interface IConnectionListener
{
    /// <summary>The endpoint the listener is bound to.</summary>
    ConnectionEndPoint EndPoint { get; }

    /// <summary>True while the listener is bound and can accept.</summary>
    bool IsBound { get; }

    /// <summary>
    /// Accept the next connection, or null once the listener is unbound.
    /// Cooperative implementation: pumps the network stack while waiting.
    /// </summary>
    ValueTask<ConnectionContext?> AcceptAsync(System.Threading.CancellationToken cancellationToken = default);

    /// <summary>Stop listening; queued but unaccepted connections are aborted.</summary>
    ValueTask UnbindAsync(System.Threading.CancellationToken cancellationToken = default);

    /// <summary>Unbind and release resources.</summary>
    ValueTask DisposeAsync();
}

/// <summary>Creates connection listeners bound to local endpoints.</summary>
public interface IConnectionListenerFactory
{
    /// <summary>Bind a listener to <paramref name="endPoint"/>.</summary>
    ValueTask<IConnectionListener> BindAsync(ConnectionEndPoint endPoint,
        System.Threading.CancellationToken cancellationToken = default);
}
