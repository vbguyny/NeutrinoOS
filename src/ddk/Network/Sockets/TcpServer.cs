// ProtonOS DDK - TCP server helper (Phase 6)
//
// Convenience wrapper for network services (sshd, webhost): binds a
// listening port, drives the caller-pumped stack from a single Tick()
// call, and hands out accepted TcpSocket instances. All pumping happens
// on the caller's thread - the DDK stack model has no kernel pump - so
// Tick() is designed to be driven cooperatively (e.g. one slice per
// idle window while the shell waits for input).

using System;
using ProtonOS.DDK.Network.Stack;

namespace ProtonOS.DDK.Network.Sockets;

/// <summary>TCP listening server helper (see file header).</summary>
public unsafe class TcpServer
{
    private readonly NetworkStack _stack;
    private readonly TcpListener _listener;
    private readonly bool _reuseAddress;

    /// <summary>
    /// Create a server bound to a local port. Nothing is bound until
    /// <see cref="Start"/> runs.
    /// </summary>
    /// <param name="stack">Network stack to serve.</param>
    /// <param name="port">Local TCP port to listen on.</param>
    /// <param name="reuseAddress">
    /// SO_REUSEADDR: replace a stale registration for the same port so a
    /// restarted server can rebind immediately.
    /// </param>
    public TcpServer(NetworkStack stack, ushort port, bool reuseAddress = false)
    {
        _stack = stack;
        _listener = new TcpListener(stack, 0, port);
        _reuseAddress = reuseAddress;
    }

    /// <summary>The bound local port.</summary>
    public ushort Port => _listener.Port;

    /// <summary>True while the server is listening.</summary>
    public bool IsListening => _listener.IsListening;

    /// <summary>Start listening; false when the port cannot be claimed.</summary>
    public bool Start() => _listener.Start(_reuseAddress);

    /// <summary>Stop listening and drop queued connections.</summary>
    public void Stop() => _listener.Stop();

    /// <summary>True when an established connection is waiting to be accepted.</summary>
    public bool Pending() => _listener.Pending();

    /// <summary>
    /// One cooperative slice: move up to <paramref name="maxFrames"/>
    /// received frames through the stack, transmit queued responses, and
    /// reap finished connections. Cheap when there is no traffic.
    /// </summary>
    public void Tick(int maxFrames = 8)
    {
        NetworkPump.Pump(_stack, maxFrames);
        NetworkPump.FlushTx(_stack);
    }

    /// <summary>Non-blocking accept; null when no connection is queued.</summary>
    public TcpSocket? Accept() => _listener.AcceptSocket();

    /// <summary>
    /// Accept with a millisecond budget, pumping the stack while
    /// waiting. Returns null on timeout.
    /// </summary>
    public TcpSocket? Accept(int timeoutMs) => _listener.AcceptSocket(timeoutMs);
}
