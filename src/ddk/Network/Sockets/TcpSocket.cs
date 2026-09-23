// ProtonOS DDK - TCP Socket
// High-level wrapper for TCP connections

using System;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Network.Stack;

namespace ProtonOS.DDK.Network.Sockets;

/// <summary>
/// TCP socket for connection-oriented communication.
/// </summary>
public unsafe class TcpSocket
{
    private readonly NetworkStack? _stack;
    private TcpConnection? _connection;
    private int _connectionIndex;

    /// <summary>
    /// Check if the socket is connected.
    /// </summary>
    public bool Connected => _connection != null && _connection.State == TcpState.Established;

    /// <summary>
    /// Get the remote IP address (host byte order).
    /// </summary>
    public uint RemoteAddress => _connection?.RemoteEndpoint.IP ?? 0;

    /// <summary>
    /// Get the remote port.
    /// </summary>
    public ushort RemotePort => _connection?.RemoteEndpoint.Port ?? 0;

    /// <summary>
    /// Get the local IP address (host byte order).
    /// </summary>
    public uint LocalAddress => _connection?.LocalEndpoint.IP ?? 0;

    /// <summary>
    /// Get the local port.
    /// </summary>
    public ushort LocalPort => _connection?.LocalEndpoint.Port ?? 0;

    /// <summary>
    /// Get the number of bytes available to read.
    /// </summary>
    public int Available => _connection?.Available ?? 0;

    /// <summary>
    /// Get the underlying connection state.
    /// </summary>
    public TcpState State => _connection?.State ?? TcpState.Closed;

    /// <summary>
    /// Create a socket for outgoing connections.
    /// </summary>
    /// <param name="stack">Network stack to use.</param>
    public TcpSocket(NetworkStack stack)
    {
        _stack = stack;
        _connection = null;
        _connectionIndex = -1;
    }

    /// <summary>
    /// Create a socket from an accepted connection (used by TcpListener).
    /// The connection is tracked by the stack's table, so sends and
    /// closes go through the owning stack (Phase 6).
    /// </summary>
    internal TcpSocket(NetworkStack stack, TcpConnection connection)
    {
        _stack = stack;
        _connection = connection;
        _connectionIndex = connection.StackIndex;
    }

    /// <summary>
    /// Connect to a remote endpoint.
    /// </summary>
    /// <param name="address">Remote IP address (host byte order).</param>
    /// <param name="port">Remote port.</param>
    /// <returns>True if connection initiated (check Connected for completion).</returns>
    public bool Connect(uint address, ushort port)
    {
        if (_stack == null)
            return false;

        if (_connection != null)
            return false;  // Already connected/connecting

        _connectionIndex = _stack.TcpConnect(address, port);
        if (_connectionIndex < 0)
        {
            if (_connectionIndex == -2)
            {
                Debug.WriteLine("[TcpSocket] Connect failed - need ARP resolution");
            }
            else
            {
                Debug.WriteLine("[TcpSocket] Connect failed - connection table full");
            }
            return false;
        }

        _connection = _stack.GetTcpConnection(_connectionIndex);
        return true;
    }

    /// <summary>
    /// Send data on the socket.
    /// </summary>
    /// <param name="data">Pointer to data to send.</param>
    /// <param name="length">Number of bytes to send.</param>
    /// <returns>Number of bytes sent, or 0 on error.</returns>
    public int Send(byte* data, int length)
    {
        if (_connection == null || !Connected || _stack == null || _connectionIndex < 0)
            return 0;

        // Segment large writes so each frame stays within one Ethernet
        // MTU (the stack builds a single IP packet per call - no
        // fragmentation support), transmitting after each segment.
        const int MaxSegment = 1400;
        int sent = 0;
        while (sent < length)
        {
            int chunk = length - sent;
            if (chunk > MaxSegment)
                chunk = MaxSegment;

            int n = _stack.TcpSend(_connectionIndex, data + sent, chunk);
            if (n <= 0)
                break;
            sent += n;
            NetworkPump.FlushTx(_stack);
        }
        return sent;
    }

    /// <summary>
    /// Send data from a span.
    /// </summary>
    public int Send(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return 0;

        fixed (byte* ptr = data)
        {
            return Send(ptr, data.Length);
        }
    }

    /// <summary>
    /// Receive data from the socket.
    /// </summary>
    /// <param name="buffer">Buffer to receive data into.</param>
    /// <param name="maxLength">Maximum bytes to read.</param>
    /// <returns>Number of bytes received.</returns>
    public int Receive(byte* buffer, int maxLength)
    {
        if (_connection == null)
            return 0;

        if (_stack != null && _connectionIndex >= 0)
        {
            return _stack.TcpReceive(_connectionIndex, buffer, maxLength);
        }

        // For accepted connections, read directly from connection
        return _connection.Read(buffer, maxLength);
    }

    /// <summary>
    /// Receive data into a span.
    /// </summary>
    public int Receive(Span<byte> buffer)
    {
        if (buffer.Length == 0)
            return 0;

        fixed (byte* ptr = buffer)
        {
            return Receive(ptr, buffer.Length);
        }
    }

    /// <summary>
    /// Close the socket.
    /// </summary>
    public void Close()
    {
        if (_connection == null)
            return;

        if (_stack != null && _connectionIndex >= 0)
        {
            _stack.TcpClose(_connectionIndex);
            NetworkPump.FlushTx(_stack);
        }

        _connection = null;
        _connectionIndex = -1;
    }

    /// <summary>
    /// Half-close: send a FIN for the send direction while the receive
    /// direction remains open (FIN_WAIT1). Use Close for the full close
    /// (Phase 6, TCP shutdown semantics).
    /// </summary>
    public void Shutdown()
    {
        if (_stack != null && _connectionIndex >= 0)
        {
            _stack.TcpShutdownSend(_connectionIndex);
            NetworkPump.FlushTx(_stack);
        }
    }

    /// <summary>
    /// Block (pumping the stack) until at least one byte is available or
    /// the peer closes or the timeout expires; returns the bytes read
    /// into <paramref name="buffer"/> (0 on timeout/closed).
    /// </summary>
    public int ReceiveWait(byte* buffer, int maxLength, int timeoutMs)
    {
        if (_stack == null)
            return Receive(buffer, maxLength);

        ulong start = Timer.GetUptimeMilliseconds();
        while (true)
        {
            NetworkPump.Pump(_stack, 4);
            int n = Receive(buffer, maxLength);
            if (n > 0)
                return n;
            if (_connection == null || _connection.IsClosed || State == TcpState.CloseWait)
                return 0;
            if (Timer.GetUptimeMilliseconds() - start >= (ulong)timeoutMs)
                return 0;
        }
    }

    /// <summary>
    /// Block (pumping the stack) until buffered data is available or the
    /// timeout expires. The bytes stay in the receive queue for a later
    /// Receive call.
    /// </summary>
    public bool WaitReadable(int timeoutMs)
    {
        if (_connection == null)
            return false;
        if (Available > 0)
            return true;
        if (_stack == null)
            return false;

        ulong start = Timer.GetUptimeMilliseconds();
        while (true)
        {
            NetworkPump.Pump(_stack, 4);
            if (Available > 0)
                return true;
            if (_connection.IsClosed)
                return false;
            if (Timer.GetUptimeMilliseconds() - start >= (ulong)timeoutMs)
                return false;
        }
    }

    /// <summary>
    /// Poll for connection state changes (call periodically after Connect).
    /// </summary>
    /// <returns>True if connected.</returns>
    public bool Poll()
    {
        if (_connection == null)
            return false;

        return _connection.State == TcpState.Established;
    }
}
