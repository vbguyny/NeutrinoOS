// NeutrinoOS DDK - socket connection listener factory (Kestrel port M2)
//
// Binds SocketConnectionListeners to local endpoints on a network stack.
// Kestrel calls this abstraction KestrelServerOptions.Listen(...) wiring:
// the factory is the transport entry point for the (future) HTTP core.

using System;
using System.Threading.Tasks;
using NeutrinoOS.DDK.Network.Stack;

namespace NeutrinoOS.DDK.Connections;

/// <summary>Creates socket-backed connection listeners for one network stack.</summary>
public sealed class SocketConnectionListenerFactory : IConnectionListenerFactory
{
    private readonly NetworkStack _stack;

    public SocketConnectionListenerFactory(NetworkStack stack)
    {
        _stack = stack;
    }

    /// <summary>
    /// Bind a listener to <paramref name="endPoint"/>. Throws when the port
    /// cannot be claimed (address in use, stack not configured).
    /// </summary>
    public ValueTask<IConnectionListener> BindAsync(ConnectionEndPoint endPoint,
        System.Threading.CancellationToken cancellationToken = default)
    {
        var listener = new SocketConnectionListener(_stack, endPoint);
        if (!listener.Start())
        {
            throw new InvalidOperationException(
                "Bind failed on port " + ((int)endPoint.Port).ToString());
        }
        return new ValueTask<IConnectionListener>((IConnectionListener)listener);
    }
}
