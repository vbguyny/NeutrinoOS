// ProtonOS DDK - TCP Connection State Management
// Manages individual TCP connection state and data transfer

using System;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Network.Sockets;

namespace ProtonOS.DDK.Network.Stack;

/// <summary>
/// Represents a TCP connection endpoint.
/// </summary>
public struct TcpEndpoint
{
    public uint IP;
    public ushort Port;

    public TcpEndpoint(uint ip, ushort port)
    {
        IP = ip;
        Port = port;
    }
}

/// <summary>
/// TCP connection state and data management.
/// </summary>
public unsafe class TcpConnection
{
    // Connection endpoints
    private TcpEndpoint _local;
    private TcpEndpoint _remote;

    // IPv6 endpoints (valid when _isV6). Public fields: the stack takes
    // their addresses for pointer-based JIT-safe calls.
    public Ipv6Address LocalV6;
    public Ipv6Address RemoteV6;
    private bool _isV6;

    // Connection state
    private TcpState _state;

    // Sequence numbers
    private uint _sendNext;      // Next sequence number to send (SND.NXT)
    private uint _sendUnack;     // Oldest unacknowledged sequence (SND.UNA)
    private uint _sendWindow;    // Send window size (SND.WND)
    private uint _recvNext;      // Next expected sequence number (RCV.NXT)
    private ushort _recvWindow;  // Receive window size (RCV.WND)

    // Initial sequence numbers (for connection setup)
    private uint _iss;           // Initial send sequence number
    private uint _irs;           // Initial receive sequence number

    // Receive buffer (simple ring buffer)
    private const int RecvBufferSize = 8192;
    private byte[] _recvBuffer;
    private int _recvHead;
    private int _recvTail;
    private int _recvCount;

    // Send buffer
    private const int SendBufferSize = 8192;
    private byte[] _sendBuffer;
    private int _sendHead;
    private int _sendTail;
    private int _sendCount;

    // Connection flags
    private bool _finSent;
    private bool _finReceived;

    // Listener reference for server-side connections
    private TcpListener? _listener;

    /// <summary>
    /// Get or set the listener that created this connection (for server-side connections).
    /// </summary>
    internal TcpListener? Listener
    {
        get => _listener;
        set => _listener = value;
    }

    /// <summary>
    /// Index of this connection in the owning stack's connection table
    /// (-1 for untracked connections). Set by NetworkStack; lets sockets
    /// created for accepted connections send data and close the
    /// connection through the stack (Phase 6).
    /// </summary>
    internal int StackIndex = -1;

    /// <summary>
    /// Uptime (milliseconds) of the last packet processed on this
    /// connection; used by the stack to reap stuck closing states.
    /// </summary>
    public long LastActivityMs;

    /// <summary>
    /// Get the local endpoint.
    /// </summary>
    public TcpEndpoint LocalEndpoint => _local;

    /// <summary>
    /// Get the remote endpoint.
    /// </summary>
    public TcpEndpoint RemoteEndpoint => _remote;

    /// <summary>True when this connection runs over IPv6.</summary>
    public bool IsV6 => _isV6;

    /// <summary>
    /// Get the current connection state.
    /// </summary>
    public TcpState State => _state;

    /// <summary>
    /// Check if connection is established.
    /// </summary>
    public bool IsConnected => _state == TcpState.Established;

    /// <summary>
    /// Check if connection is closed.
    /// </summary>
    public bool IsClosed => _state == TcpState.Closed || _state == TcpState.TimeWait;

    /// <summary>
    /// Get bytes available to read.
    /// </summary>
    public int Available => _recvCount;

    /// <summary>
    /// Get the next sequence number to send.
    /// </summary>
    public uint SendNext => _sendNext;

    /// <summary>
    /// Get the next expected receive sequence.
    /// </summary>
    public uint RecvNext => _recvNext;

    /// <summary>
    /// Get the receive window size.
    /// </summary>
    public ushort RecvWindow => _recvWindow;

    /// <summary>
    /// Create a new TCP connection (for outgoing connections).
    /// </summary>
    public TcpConnection(uint localIP, ushort localPort, uint remoteIP, ushort remotePort)
    {
        _local = new TcpEndpoint(localIP, localPort);
        _remote = new TcpEndpoint(remoteIP, remotePort);
        _state = TcpState.Closed;

        // Initialize sequence number (should be random in production)
        _iss = GenerateISN();
        _sendNext = _iss;
        _sendUnack = _iss;

        // Default window sizes
        _recvWindow = (ushort)RecvBufferSize;
        _sendWindow = RecvBufferSize;

        // Allocate buffers
        _recvBuffer = new byte[RecvBufferSize];
        _sendBuffer = new byte[SendBufferSize];
    }

    /// <summary>
    /// Create a TCP connection from an incoming SYN (for listening sockets).
    /// </summary>
    public TcpConnection(uint localIP, ushort localPort, uint remoteIP, ushort remotePort,
                         uint remoteSeq, ushort remoteWindow)
    {
        _local = new TcpEndpoint(localIP, localPort);
        _remote = new TcpEndpoint(remoteIP, remotePort);
        _state = TcpState.SynReceived;

        // Initialize our sequence number
        _iss = GenerateISN();
        _sendNext = _iss;
        _sendUnack = _iss;

        // Record remote's initial sequence
        _irs = remoteSeq;
        _recvNext = remoteSeq + 1; // SYN consumes one sequence number

        // Window sizes
        _recvWindow = (ushort)RecvBufferSize;
        _sendWindow = remoteWindow;

        // Allocate buffers
        _recvBuffer = new byte[RecvBufferSize];
        _sendBuffer = new byte[SendBufferSize];
    }

    /// <summary>Create an outgoing IPv6 connection.</summary>
    public TcpConnection(Ipv6Address* localIP, ushort localPort, Ipv6Address* remoteIP, ushort remotePort)
    {
        _isV6 = true;
        LocalV6 = *localIP;
        RemoteV6 = *remoteIP;
        _local = new TcpEndpoint(0, localPort);
        _remote = new TcpEndpoint(0, remotePort);
        _state = TcpState.Closed;

        _iss = GenerateISN();
        _sendNext = _iss;
        _sendUnack = _iss;

        _recvWindow = (ushort)RecvBufferSize;
        _sendWindow = RecvBufferSize;

        _recvBuffer = new byte[RecvBufferSize];
        _sendBuffer = new byte[SendBufferSize];
    }

    /// <summary>Create an IPv6 connection from an incoming SYN (server side).</summary>
    public TcpConnection(Ipv6Address* localIP, ushort localPort, Ipv6Address* remoteIP, ushort remotePort,
                         uint remoteSeq, ushort remoteWindow)
    {
        _isV6 = true;
        LocalV6 = *localIP;
        RemoteV6 = *remoteIP;
        _local = new TcpEndpoint(0, localPort);
        _remote = new TcpEndpoint(0, remotePort);
        _state = TcpState.SynReceived;

        _iss = GenerateISN();
        _sendNext = _iss;
        _sendUnack = _iss;

        _irs = remoteSeq;
        _recvNext = remoteSeq + 1;

        _recvWindow = (ushort)RecvBufferSize;
        _sendWindow = remoteWindow;

        _recvBuffer = new byte[RecvBufferSize];
        _sendBuffer = new byte[SendBufferSize];
    }

    /// <summary>
    /// Generate an initial sequence number.
    /// In a real implementation, this should be based on a secure random source.
    /// </summary>
    private static uint GenerateISN()
    {
        // Simple ISN based on timer - NOT secure, just for testing
        // Real implementations should use RFC 6528 algorithm
        return (uint)(Timer.GetTickCount() & 0xFFFFFFFF);
    }

    /// <summary>
    /// Initiate an active open (client connect).
    /// Returns the SYN packet to send.
    /// </summary>
    public int InitiateConnect(byte* buffer)
    {
        if (_state != TcpState.Closed)
            return 0;

        _state = TcpState.SynSent;

        // Build SYN packet
        int len = BuildSegment(buffer, _sendNext, 0, TcpFlags.SYN, _recvWindow, null, 0);

        // SYN consumes one sequence number
        _sendNext++;

        return len;
    }

    /// <summary>
    /// Build a SYN-ACK response (for server-side connections in SYN_RECEIVED state).
    /// Returns the packet length.
    /// </summary>
    public int BuildSynAck(byte* buffer)
    {
        if (_state != TcpState.SynReceived)
            return 0;

        int len = BuildSegment(buffer, _sendNext, _recvNext,
            (byte)(TcpFlags.SYN | TcpFlags.ACK), _recvWindow, null, 0);

        // SYN consumes one sequence number
        _sendNext++;

        return len;
    }

    /// <summary>
    /// Process a received TCP packet.
    /// Returns the response packet length (0 if no response needed).
    /// </summary>
    public int ProcessPacket(TcpPacket* packet, byte* responseBuffer)
    {
        LastActivityMs = (long)Timer.GetUptimeMilliseconds();
        int responseLen = 0;

        switch (_state)
        {
            case TcpState.Closed:
                // Send RST for any packet to closed connection
                if (!packet->IsRst)
                {
                    responseLen = BuildSegment(responseBuffer, packet->AckNum, 0,
                                               TcpFlags.RST, 0, null, 0);
                }
                break;

            case TcpState.Listen:
                responseLen = ProcessListen(packet, responseBuffer);
                break;

            case TcpState.SynSent:
                responseLen = ProcessSynSent(packet, responseBuffer);
                break;

            case TcpState.SynReceived:
                responseLen = ProcessSynReceived(packet, responseBuffer);
                break;

            case TcpState.Established:
                responseLen = ProcessEstablished(packet, responseBuffer);
                break;

            case TcpState.FinWait1:
                responseLen = ProcessFinWait1(packet, responseBuffer);
                break;

            case TcpState.FinWait2:
                responseLen = ProcessFinWait2(packet, responseBuffer);
                break;

            case TcpState.CloseWait:
                responseLen = ProcessCloseWait(packet, responseBuffer);
                break;

            case TcpState.Closing:
                responseLen = ProcessClosing(packet, responseBuffer);
                break;

            case TcpState.LastAck:
                responseLen = ProcessLastAck(packet, responseBuffer);
                break;

            case TcpState.TimeWait:
                // In TIME_WAIT, just ACK any valid segment
                if (IsValidSequence(packet->SeqNum))
                {
                    responseLen = BuildAckHere(responseBuffer);
                }
                break;
        }

        return responseLen;
    }

    private int ProcessListen(TcpPacket* packet, byte* responseBuffer)
    {
        // Only accept SYN in Listen state
        if (packet->IsSyn && !packet->IsAck)
        {
            _irs = packet->SeqNum;
            _recvNext = packet->SeqNum + 1;
            _sendWindow = packet->Window;
            _state = TcpState.SynReceived;

            // Send SYN-ACK
            int len = BuildSegment(responseBuffer, _sendNext, _recvNext,
                (byte)(TcpFlags.SYN | TcpFlags.ACK), _recvWindow, null, 0);
            _sendNext++; // SYN consumes one sequence
            return len;
        }

        return 0;
    }

    private int ProcessSynSent(TcpPacket* packet, byte* responseBuffer)
    {
        // Expecting SYN-ACK
        if (packet->IsSyn && packet->IsAck)
        {
            // Verify ACK acknowledges our SYN
            if (packet->AckNum != _sendNext)
            {
                // Invalid ACK, send RST
                return BuildSegment(responseBuffer, packet->AckNum, 0,
                                    TcpFlags.RST, 0, null, 0);
            }

            _irs = packet->SeqNum;
            _recvNext = packet->SeqNum + 1;
            _sendUnack = packet->AckNum;
            _sendWindow = packet->Window;
            _state = TcpState.Established;

            Debug.WriteLine("[TCP] Connection established");

            // Send ACK
            return BuildAckHere(responseBuffer);
        }
        else if (packet->IsSyn && !packet->IsAck)
        {
            // Simultaneous open - rare case
            _irs = packet->SeqNum;
            _recvNext = packet->SeqNum + 1;
            _state = TcpState.SynReceived;

            return BuildSegment(responseBuffer, _sendNext - 1, _recvNext,
                                (byte)(TcpFlags.SYN | TcpFlags.ACK), _recvWindow, null, 0);
        }

        return 0;
    }

    private int ProcessSynReceived(TcpPacket* packet, byte* responseBuffer)
    {
        if (packet->IsRst)
        {
            _state = TcpState.Listen; // Or Closed if not a server
            return 0;
        }

        if (packet->IsAck)
        {
            if (packet->AckNum == _sendNext)
            {
                _sendUnack = packet->AckNum;
                _state = TcpState.Established;
                Debug.WriteLine("[TCP] Connection established (server)");

                // Notify listener that connection is established
                if (_listener != null)
                {
                    _listener.ConnectionEstablished(this);
                }
            }
        }

        return 0;
    }

    private int ProcessEstablished(TcpPacket* packet, byte* responseBuffer)
    {
        if (packet->IsRst)
        {
            _state = TcpState.Closed;
            return 0;
        }

        // Process ACK
        if (packet->IsAck)
        {
            // Update send window
            if (IsValidAck(packet->AckNum))
            {
                _sendUnack = packet->AckNum;
                _sendWindow = packet->Window;
            }
        }

        // Process incoming data
        if (packet->PayloadLength > 0)
        {
            if (packet->SeqNum == _recvNext)
            {
                // In-order data - copy to receive buffer
                int copied = CopyToRecvBuffer(packet->Payload, packet->PayloadLength);
                _recvNext += (uint)copied;

                // Send ACK
                return BuildAckHere(responseBuffer);
            }
            else
            {
                // Out of order - send duplicate ACK
                return BuildAckHere(responseBuffer);
            }
        }

        // Process FIN
        if (packet->IsFin)
        {
            _recvNext++; // FIN consumes one sequence
            _finReceived = true;
            _state = TcpState.CloseWait;

            return BuildAckHere(responseBuffer);
        }

        return 0;
    }

    private int ProcessFinWait1(TcpPacket* packet, byte* responseBuffer)
    {
        if (packet->IsAck && packet->AckNum == _sendNext)
        {
            _sendUnack = packet->AckNum;

            if (packet->IsFin)
            {
                _recvNext++;
                _state = TcpState.TimeWait;
                return BuildAckHere(responseBuffer);
            }
            else
            {
                _state = TcpState.FinWait2;
            }
        }
        else if (packet->IsFin)
        {
            _recvNext++;
            _state = TcpState.Closing;
            return BuildAckHere(responseBuffer);
        }

        return 0;
    }

    private int ProcessFinWait2(TcpPacket* packet, byte* responseBuffer)
    {
        if (packet->IsFin)
        {
            _recvNext++;
            _state = TcpState.TimeWait;
            return BuildAckHere(responseBuffer);
        }

        // Can still receive data
        if (packet->PayloadLength > 0 && packet->SeqNum == _recvNext)
        {
            int copied = CopyToRecvBuffer(packet->Payload, packet->PayloadLength);
            _recvNext += (uint)copied;
            return BuildAckHere(responseBuffer);
        }

        return 0;
    }

    private int ProcessCloseWait(TcpPacket* packet, byte* responseBuffer)
    {
        // Waiting for application to close
        // Just ACK any data
        if (packet->IsAck)
        {
            _sendUnack = packet->AckNum;
        }
        return 0;
    }

    private int ProcessClosing(TcpPacket* packet, byte* responseBuffer)
    {
        if (packet->IsAck && packet->AckNum == _sendNext)
        {
            _state = TcpState.TimeWait;
        }
        return 0;
    }

    private int ProcessLastAck(TcpPacket* packet, byte* responseBuffer)
    {
        if (packet->IsAck && packet->AckNum == _sendNext)
        {
            _state = TcpState.Closed;
        }
        return 0;
    }

    /// <summary>
    /// Initiate connection close.
    /// Returns the FIN packet to send.
    /// </summary>
    public int InitiateClose(byte* buffer)
    {
        if (_state == TcpState.Established)
        {
            _state = TcpState.FinWait1;
            int len = BuildSegment(buffer, _sendNext, _recvNext,
                (byte)(TcpFlags.FIN | TcpFlags.ACK), _recvWindow, null, 0);
            _sendNext++; // FIN consumes one sequence
            _finSent = true;
            return len;
        }
        else if (_state == TcpState.CloseWait)
        {
            _state = TcpState.LastAck;
            int len = BuildSegment(buffer, _sendNext, _recvNext,
                (byte)(TcpFlags.FIN | TcpFlags.ACK), _recvWindow, null, 0);
            _sendNext++;
            _finSent = true;
            return len;
        }

        return 0;
    }

    /// <summary>
    /// Build a data packet to send.
    /// Returns packet length, or 0 if no data to send.
    /// </summary>
    public int BuildDataPacket(byte* buffer, byte* data, int dataLength)
    {
        if (_state != TcpState.Established || dataLength <= 0)
            return 0;

        // Limit to send window
        int maxSend = (int)(_sendUnack + _sendWindow - _sendNext);
        if (maxSend <= 0)
            return 0;

        int sendLen = dataLength < maxSend ? dataLength : maxSend;

        int len = BuildSegment(buffer, _sendNext, _recvNext,
            (byte)(TcpFlags.PSH | TcpFlags.ACK), _recvWindow, data, sendLen);

        if (len > 0)
        {
            _sendNext += (uint)sendLen;
        }

        return len;
    }

    /// <summary>
    /// Read data from receive buffer.
    /// </summary>
    public int Read(byte* buffer, int maxLength)
    {
        if (_recvCount == 0)
            return 0;

        int toRead = _recvCount < maxLength ? _recvCount : maxLength;
        int read = 0;

        while (read < toRead)
        {
            buffer[read] = _recvBuffer[_recvHead];
            _recvHead = (_recvHead + 1) % RecvBufferSize;
            read++;
        }

        _recvCount -= read;
        return read;
    }

    private int CopyToRecvBuffer(byte* data, int length)
    {
        int space = RecvBufferSize - _recvCount;
        int toCopy = length < space ? length : space;

        for (int i = 0; i < toCopy; i++)
        {
            _recvBuffer[_recvTail] = data[i];
            _recvTail = (_recvTail + 1) % RecvBufferSize;
        }

        _recvCount += toCopy;
        return toCopy;
    }

    private bool IsValidSequence(uint seq)
    {
        // Simplified check - just verify it's in expected range
        return true; // TODO: Proper sequence number validation
    }

    private bool IsValidAck(uint ack)
    {
        // ACK should be for data we've sent but not yet acknowledged
        // SND.UNA < ACK <= SND.NXT
        return ack > _sendUnack && ack <= _sendNext;
    }

    /// <summary>
    /// Check if this connection matches the given endpoints.
    /// </summary>
    public bool Matches(uint remoteIP, ushort remotePort, ushort localPort)
    {
        return !_isV6 &&
               _remote.IP == remoteIP &&
               _remote.Port == remotePort &&
               _local.Port == localPort;
    }

    /// <summary>IPv6 match (ports + remote address).</summary>
    public bool Matches6(Ipv6Address* remoteIP, ushort remotePort, ushort localPort)
    {
        return _isV6 &&
               RemoteV6.Hi == remoteIP->Hi && RemoteV6.Lo == remoteIP->Lo &&
               _remote.Port == remotePort &&
               _local.Port == localPort;
    }

    /// <summary>
    /// Build a TCP segment on this connection with the right pseudo-header
    /// (IPv4 or IPv6 checksum). Single construction point for all responses.
    /// </summary>
    private int BuildSegment(byte* buffer, uint seqNum, uint ackNum, byte flags,
        ushort window, byte* payload, int payloadLength)
    {
        if (_isV6)
        {
            Ipv6Address l = LocalV6;
            Ipv6Address r = RemoteV6;
            return TCP.BuildPacketWithChecksum6(buffer, _local.Port, _remote.Port,
                seqNum, ackNum, flags, window, payload, payloadLength, &l, &r);
        }
        return TCP.BuildPacketWithChecksum(buffer, _local.Port, _remote.Port,
            seqNum, ackNum, flags, window, payload, payloadLength, _local.IP, _remote.IP);
    }

    /// <summary>Plain ACK with the connection's current sequence state.</summary>
    private int BuildAckHere(byte* buffer)
    {
        return BuildSegment(buffer, _sendNext, _recvNext, TcpFlags.ACK, _recvWindow, null, 0);
    }

    /// <summary>
    /// Set to Listen state (for server sockets).
    /// </summary>
    public void Listen()
    {
        if (_state == TcpState.Closed)
        {
            _state = TcpState.Listen;
        }
    }
}
