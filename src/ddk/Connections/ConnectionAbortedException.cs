// NeutrinoOS DDK - connection abort reason (Kestrel port milestone M2)

using System;

namespace NeutrinoOS.DDK.Connections;

/// <summary>
/// Kestrel-shaped abort reason. Recorded when a connection is aborted
/// before it completes (owner cancelled, transport failure, service stop).
/// </summary>
public sealed class ConnectionAbortedException : Exception
{
    public ConnectionAbortedException()
        : base("The connection was aborted.")
    {
    }

    public ConnectionAbortedException(string message)
        : base(message)
    {
    }
}
