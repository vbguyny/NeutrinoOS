// NeutrinoOS korlib - IDuplexPipe (Kestrel port milestone M2)
//
// Matches the BCL shape exactly (System.IO.Pipelines.IDuplexPipe).
// On-device MemberRef resolution matches types by name + signature, so the
// namespace and member names here are load-bearing for any DDK/utility
// code compiled against the BCL that uses IDuplexPipe at runtime.

namespace System.IO.Pipelines
{
    /// <summary>
    /// A pair of pipe endpoints (input + output) representing one end of a
    /// duplex transport, e.g. a socket connection. Kestrel calls the server
    /// side of a connection an <c>IDuplexPipe</c> transport.
    /// </summary>
    public interface IDuplexPipe
    {
        /// <summary>Data read from the remote end.</summary>
        PipeReader Input { get; }

        /// <summary>Data written towards the remote end.</summary>
        PipeWriter Output { get; }
    }
}
