// NeutrinoOS DDK - NetworkStack TCP over IPv6 (Phase 9 Task 2)
//
// Receive/transmit plumbing for TCPv6: connection lookup (with the same
// loopback "healing" the v4 path uses), listener SYN handling, and
// client connects. The segment builders live on TcpConnection, which
// picks the right pseudo-header for its address family.

using System;
using NeutrinoOS.DDK.Kernel;

namespace NeutrinoOS.DDK.Network.Stack;

public unsafe partial class NetworkStack
{
    /// <summary>Find a v6 connection by (remote address, remote port, local port).</summary>
    private TcpConnection FindConnection6(Ipv6Address* remoteIP, ushort remotePort, ushort localPort)
    {
        for (int i = 0; i < MaxTcpConnections; i++)
        {
            var conn = _tcpConnections[i];
            if (conn != null && conn.Matches6(remoteIP, remotePort, localPort))
                return conn;
        }
        return null;
    }

    /// <summary>Process a received TCP segment carried over IPv6.</summary>
    private void ProcessTcp6(byte* data, int length, Ipv6Address* srcIP, Ipv6Address* destIP)
    {
        TcpPacket packet;
        if (!TCP.Parse(data, length, out packet))
        {
            if (!Quiet)
                Debug.WriteLine("[NetStack] Failed to parse TCPv6 packet");
            return;
        }

        if (!TCP.VerifyChecksum6(data, length, srcIP, destIP))
        {
            if (!Quiet)
                Debug.WriteLine("[NetStack] TCPv6 checksum fail");
            return;
        }

        if (!Quiet)
        {
            Debug.Write("[NetStack] TCPv6 from ");
            Debug.Write(srcIP->ToString());
            Debug.Write(":");
            Debug.WriteDecimal(packet.SourcePort);
            Debug.Write(" to port ");
            Debug.WriteDecimal(packet.DestPort);
            Debug.WriteLine();
        }

        var conn = FindConnection6(srcIP, packet.SourcePort, packet.DestPort);

        // Loopback healing (mirrors the v4 path): connections created to
        // a loopback destination record ::1 as the remote, while the
        // peer's frames arrive with a local address as source.
        if (conn == null && IsLocalV6(srcIP))
        {
            for (int i = 0; i < MaxTcpConnections && conn == null; i++)
            {
                var c = _tcpConnections[i];
                if (c == null || !c.IsV6)
                    continue;
                if (c.LocalEndpoint.Port == packet.DestPort &&
                    c.RemoteEndpoint.Port == packet.SourcePort)
                {
                    Ipv6Address r = c.RemoteV6;
                    if (IsLocalV6(&r) && (r.Hi != srcIP->Hi || r.Lo != srcIP->Lo))
                        conn = c;
                }
            }
        }

        if (conn == null)
        {
            if (packet.IsSyn && !packet.IsAck)
            {
                var listener = FindListener(packet.DestPort);
                if (listener != null)
                {
                    listener.HandleIncomingSyn6(srcIP, destIP, packet.SourcePort, packet.DestPort,
                                                packet.SeqNum, packet.Window);
                    return;
                }
            }
            if (!Quiet)
                Debug.WriteLine("[NetStack] No TCPv6 connection found for packet");
            return;
        }

        byte* responseBuffer = stackalloc byte[TcpHeader.MaxSize + 64];
        int responseLen = conn.ProcessPacket(&packet, responseBuffer);

        if (responseLen > 0)
        {
            // Reply from the connection's recorded local address to its
            // recorded remote address (consistent with the checksum used
            // when the segment was built).
            Ipv6Address l = conn.LocalV6;
            Ipv6Address r = conn.RemoteV6;
            int frameLen = BuildIpv6Frame(&l, &r, Ipv6NextHeader.Tcp, responseBuffer, responseLen);
            if (frameLen > 0)
            {
                QueuePendingTx(frameLen);
                _tcp6Sent++;
            }
        }
    }

    /// <summary>
    /// Initiate a TCP connection over IPv6.
    /// Returns the connection index, -1 on error, -2 when NDP resolution
    /// is required first.
    /// </summary>
    public int TcpConnect6(Ipv6Address* destIP, ushort destPort)
    {
        if (_tcpConnectionCount >= MaxTcpConnections)
        {
            Debug.WriteLine("[NetStack] TCP connection table full");
            return -1;
        }

        if (!_v6LinkLocalValid)
            ConfigureV6LinkLocal();

        // Loopback source (no chained struct returns).
        Ipv6Address srcIP;
        if (destIP->Hi == 0 && destIP->Lo == 1)
        {
            srcIP = default;
            srcIP.Lo = 1;
        }
        else
        {
            srcIP = SelectSourceV6(destIP);
        }

        ushort localPort = AllocateEphemeralPort();
        var conn = new TcpConnection(&srcIP, localPort, destIP, destPort);

        int index = AddConnection(conn);
        if (index < 0)
            return -1;

        byte* synBuffer = stackalloc byte[TcpHeader.MinSize];
        int synLen = conn.InitiateConnect(synBuffer);
        if (synLen == 0)
        {
            RemoveConnection(index);
            return -1;
        }

        int frameLen = BuildIpv6Frame(&srcIP, destIP, Ipv6NextHeader.Tcp, synBuffer, synLen);
        if (frameLen == 0)
        {
            Debug.Write("[NetStack] TCPv6 connect to ");
            Debug.Write(destIP->ToString());
            Debug.WriteLine(" - need NDP first");
            RemoveConnection(index);
            return -2;
        }

        QueuePendingTx(frameLen);
        _tcp6Sent++;

        Debug.Write("[NetStack] TCPv6 connecting to [");
        Debug.Write(destIP->ToString());
        Debug.Write("]:");
        Debug.WriteDecimal(destPort);
        Debug.WriteLine();

        return index;
    }
}
