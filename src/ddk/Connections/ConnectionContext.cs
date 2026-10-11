// NeutrinoOS DDK - connection context (Kestrel port milestone M2)
//
// The Kestrel shape: a server-side connection is a ConnectionContext whose
// Transport is an IDuplexPipe (input + output pipes). The socket-backed
// implementation lives in SocketConnectionContext; the HTTP layer (M3) is
// written against this abstraction only.

using System;
using System.IO.Pipelines;
using System.Threading.Tasks;

namespace NeutrinoOS.DDK.Connections;

/// <summary>One server-side connection: duplex transport + identity + abort.</summary>
public abstract class ConnectionContext
{
    /// <summary>Opaque identifier (unique per listener), for logs/diagnostics.</summary>
    public abstract string ConnectionId { get; set; }

    /// <summary>The application-facing duplex pipe pair.</summary>
    public abstract IDuplexPipe Transport { get; set; }

    /// <summary>
    /// Abort the connection: the transport is closed immediately and any
    /// in-flight data is discarded. The abort reason is surfaced to the
    /// transport's pipe completions.
    /// </summary>
    public abstract void Abort(Exception? abortReason = null);

    /// <summary>Aborts and releases the connection's resources.</summary>
    public virtual ValueTask DisposeAsync()
    {
        Abort();
        return default;
    }
}
