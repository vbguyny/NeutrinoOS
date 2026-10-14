// NeutrinoOS DDK - socket connection listener (Kestrel port M2)
//
// Kestrel-shaped IConnectionListener over the DDK TCP stack. Cooperative:
// Tick() moves frames through the stack (NetworkPump), accepts established
// connections into a small pending queue, and TryAccept() hands them out
// as SocketConnectionContexts. The caller owns accepted connections and is
// responsible for ticking them (SocketConnectionContext.Tick).
//
// AcceptAsync is provided for Kestrel shape: it pumps the stack until a
// connection is pending (bounded by the cancellation token). Cooperative
// services should prefer Tick() + TryAccept() from their idle slice.

using System;
using System.Threading.Tasks;
using NeutrinoOS.DDK.Kernel;
using NeutrinoOS.DDK.Network.Sockets;
using NeutrinoOS.DDK.Network.Stack;

namespace NeutrinoOS.DDK.Connections;

/// <summary>Socket-backed connection listener (one bound TCP port).</summary>
public sealed class SocketConnectionListener : IConnectionListener
{
    private const int MaxPendingAccepts = 8;

    private readonly NetworkStack _stack;
    private readonly bool _reuseAddress;
    private readonly byte[]? _tlsCertDer;
    private readonly byte[]? _tlsKeySeed;
    private readonly SocketConnectionContext?[] _pending = new SocketConnectionContext?[MaxPendingAccepts];
    private TcpServer? _server;
    private int _pendingCount;
    private int _nextConnectionId;
    private bool _disposed;

    public SocketConnectionListener(NetworkStack stack, ConnectionEndPoint endPoint, bool reuseAddress = true)
    {
        _stack = stack;
        EndPoint = endPoint;
        _reuseAddress = reuseAddress;
    }

    /// <summary>
    /// M5: TLS 1.3 acceptor variant. When <paramref name="certDer"/> and
    /// <paramref name="keySeed"/> are non-null every accepted connection
    /// is wrapped in a TLS 1.3 server session before the first Tick (the
    /// app-facing pipes carry plaintext; see SocketConnectionContext).
    /// </summary>
    public SocketConnectionListener(NetworkStack stack, ConnectionEndPoint endPoint,
        byte[]? certDer, byte[]? keySeed, bool reuseAddress = true)
    {
        _stack = stack;
        EndPoint = endPoint;
        _reuseAddress = reuseAddress;
        _tlsCertDer = certDer;
        _tlsKeySeed = keySeed;
    }

    /// <inheritdoc/>
    public ConnectionEndPoint EndPoint { get; }

    /// <inheritdoc/>
    public bool IsBound => _server != null && _server.IsListening;

    /// <summary>Bind the port and start listening; false when it cannot be claimed.</summary>
    public bool Start()
    {
        if (_server != null)
            return _server.IsListening;
        var server = new TcpServer(_stack, EndPoint.Port, _reuseAddress);
        if (!server.Start())
            return false;
        _server = server;
        return true;
    }

    /// <summary>
    /// One cooperative slice: move frames through the stack and accept any
    /// established connections into the pending queue (bounded).
    /// </summary>
    public void Tick(int maxFrames = 8)
    {
        SocketConnectionContext?[] pending = _pending;
        var server = _server;
        if (server == null)
            return;

        // Pump + FlushTx: loopback frames are delivered straight back into
        // the stack by NetworkPump, so a synthetic stack with no NIC works.
        server.Tick(maxFrames);

        while (_pendingCount < pending.Length && server.Pending())
        {
            TcpSocket? socket = server.Accept();
            if (socket == null)
                break;
            _nextConnectionId++;
            var accepted = new SocketConnectionContext(
                socket, "socket-" + _nextConnectionId.ToString());
            if (_tlsCertDer != null && _tlsKeySeed != null)
                accepted.EnableTls(_tlsCertDer, _tlsKeySeed);
            pending[_pendingCount] = accepted;
            _pendingCount++;
        }
    }

    /// <summary>Take the next accepted connection; false when none is pending.</summary>
    public bool TryAccept(out SocketConnectionContext? context)
    {
        if (_pendingCount > 0)
        {
            context = _pending[0];
            _pendingCount--;
            for (int i = 0; i < _pendingCount; i++)
                _pending[i] = _pending[i + 1];
            _pending[_pendingCount] = null;
            return true;
        }
        context = null;
        return false;
    }

    /// <inheritdoc/>
    public ValueTask<ConnectionContext?> AcceptAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        while (!_disposed)
        {
            if (TryAccept(out SocketConnectionContext? accepted))
                return new ValueTask<ConnectionContext?>((ConnectionContext?)accepted);
            if (cancellationToken.IsCancellationRequested)
                return new ValueTask<ConnectionContext?>((ConnectionContext?)null);
            Tick();
            Thread.Sleep(1);   // cooperative wait: give the network pump time
        }
        return new ValueTask<ConnectionContext?>((ConnectionContext?)null);
    }

    /// <inheritdoc/>
    public ValueTask UnbindAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        StopCore();
        return default;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        StopCore();
        return default;
    }

    private void StopCore()
    {
        var server = _server;
        if (server != null)
        {
            server.Stop();
            _server = null;
        }

        // Queued but unaccepted connections are dropped.
        for (int i = 0; i < _pendingCount; i++)
        {
            _pending[i]?.Abort(new ConnectionAbortedException("The listener was stopped."));
            _pending[i] = null;
        }
        _pendingCount = 0;
    }
}
