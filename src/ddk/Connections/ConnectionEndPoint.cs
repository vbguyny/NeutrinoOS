// NeutrinoOS DDK - connection transport endpoint (Kestrel port milestone M2)
//
// The DDK network stack binds a port on every interface (the local address
// is not part of listener lookup), so the endpoint mostly carries the port;
// Address is kept for Kestrel shape and future interface-scoped binding
// (0 = any interface).

namespace NeutrinoOS.DDK.Connections;

/// <summary>Local bind endpoint for a socket-backed connection listener.</summary>
public readonly struct ConnectionEndPoint
{
    /// <summary>Local IP address (host byte order); 0 = any interface.</summary>
    public readonly uint Address;

    /// <summary>Local TCP port.</summary>
    public readonly ushort Port;

    /// <summary>Bind any interface on <paramref name="port"/>.</summary>
    public ConnectionEndPoint(ushort port)
    {
        Address = 0;
        Port = port;
    }

    /// <summary>Bind a specific local address.</summary>
    public ConnectionEndPoint(uint address, ushort port)
    {
        Address = address;
        Port = port;
    }

    /// <summary>True when the endpoint binds every local interface.</summary>
    public bool IsAny => Address == 0;
}
