// NeutrinoOS DDK - socket-backed connection transport (Kestrel port M2)
//
// One accepted TcpSocket exposed as a Kestrel-shaped connection: the
// Transport is an IDuplexPipe whose input pipe carries socket->app bytes
// and whose output pipe carries app->socket bytes. The pump is cooperative
// (no threads): Tick() moves data both ways in bounded slices and is
// driven by whoever owns the connection (WebHost, a test harness) from the
// kernel idle tick.
//
// Flow control:
//  - socket -> input pipe respects Pipe backpressure: when the pipe's
//    FlushAsync is incomplete (unread bytes above PauseWriterThreshold) the
//    socket read side pauses until the application advances its reader.
//  - app -> socket output pipe is drained until the TCP send window fills
//    (TcpSocket.Send returns a short count); the remainder stays in the
//    pipe and is retried on the next Tick.
//
// End-of-stream:
//  - peer FIN (CloseWait/Closed) completes the input pipe writer, so the
//    application sees ReadResult.IsCompleted after the last buffered bytes.
//  - the application completing its output writer drains the pipe and then
//    sends FIN via TcpSocket.Shutdown().
//
// Constructed by SocketConnectionListener (one per accepted socket).

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks;
using NeutrinoOS.DDK.Kernel;
using NeutrinoOS.DDK.Network.Sockets;
using NeutrinoOS.DDK.Network.Stack;
using NeutrinoOS.DDK.Tls;

namespace NeutrinoOS.DDK.Connections;

/// <summary>A TcpSocket exposed as a Kestrel-style duplex-pipe connection.</summary>
public sealed class SocketConnectionContext : ConnectionContext
{
    /// <summary>Bytes moved per direction per Tick (keeps slices bounded).</summary>
    private const int DefaultBudget = 64 * 1024;

    /// <summary>Scratch size for the pointer-based socket calls (below LOH threshold).</summary>
    private const int ScratchSize = 16 * 1024;

    private readonly TcpSocket _socket;
    private readonly Pipe _input;    // socket -> application
    private readonly Pipe _output;   // application -> socket
    private readonly DuplexPipe _transport;
    private readonly byte[] _rxScratch = new byte[ScratchSize];
    private readonly byte[] _txScratch = new byte[ScratchSize];

    private ValueTask<FlushResult> _pendingInputFlush;
    private bool _inputFlushPending;
    private bool _inputDone;
    private bool _outputDone;
    private bool _aborted;

    // M5: optional TLS 1.3 layer (set by the listener before the first
    // Tick when the endpoint is an HTTPS endpoint). The app-facing pipes
    // carry PLAINTEXT; the handshake and record layer sit between the
    // pipes and the socket.
    private Tls13Connection? _tls;
    private bool _tlsReady;

    /// <summary>True when this connection runs over TLS 1.3.</summary>
    public bool TlsEnabled => _tls != null;

    /// <summary>True once the TLS handshake completed.</summary>
    public bool TlsConnected => _tlsReady;

    /// <summary>ALPN protocol negotiated by the TLS client ("h2"/"http/1.1"/null).</summary>
    public string? TlsAlpn => _tls != null ? _tls.AlpnSelected : null;

    /// <summary>Source IP of the peer.</summary>
    public uint PeerIp => _socket.RemoteAddress;

    /// <summary>
    /// Wrap this connection in a TLS 1.3 server session (M5). Call right
    /// after construction, before any Tick: the first Tick drives the
    /// handshake and the input pipe stays empty until it completes.
    /// </summary>
    public void EnableTls(byte[] certDer, byte[] keySeed)
    {
        EnableTls(certDer, keySeed, null, null);
    }

    /// <summary>
    /// TLS 1.3 variant with the optional P-256 pair (used for clients
    /// that do not offer ed25519 - e.g. Windows Schannel).
    /// </summary>
    public void EnableTls(byte[] certDer, byte[] keySeed, byte[] p256CertDer, byte[] p256Key)
    {
        if (certDer == null || keySeed == null)
            return;
        _tls = new Tls13Connection(_socket, certDer, keySeed, p256CertDer, p256Key);
        _tlsReady = false;
    }

    public SocketConnectionContext(TcpSocket socket, string connectionId)
    {
        _socket = socket;
        _input = new Pipe();
        _output = new Pipe();
        _transport = new DuplexPipe(_input.Reader, _output.Writer);
        ConnectionId = connectionId;
        Transport = _transport;
        LastActivityMs = Timer.GetUptimeMilliseconds();
    }

    /// <inheritdoc/>
    public override string ConnectionId { get; set; }

    /// <inheritdoc/>
    public override IDuplexPipe Transport { get; set; }

    /// <summary>Application read side of the transport (socket -> app).</summary>
    public PipeReader InputReader => _input.Reader;

    /// <summary>Application write side of the transport (app -> socket).</summary>
    public PipeWriter OutputWriter => _output.Writer;

    /// <summary>Uptime at the last byte moved (either direction).</summary>
    public ulong LastActivityMs { get; private set; }

    // ---- Temporary M2 bring-up diagnostics (safe to keep: monotonic counters) ----

    /// <summary>Ticks where the socket reported buffered inbound bytes.</summary>
    public int DebugAvailSeen;

    /// <summary>Raw bytes read from the socket into the input pipe.</summary>
    public int DebugSocketReadBytes;

    /// <summary>Raw bytes written to the socket from the output pipe.</summary>
    public int DebugSocketSentBytes;

    /// <summary>PumpOutput iterations that saw buffered output-pipe data.</summary>
    public int DebugOutTryReads;

    /// <summary>PumpOutput send attempts that accepted zero bytes.</summary>
    public int DebugOutSendZero;

    /// <summary>
    /// True once both directions are done (EOF propagated and FIN sent) or
    /// the connection was aborted; the owner can release the connection.
    /// </summary>
    public bool IsFinished => _aborted || (_inputDone && _outputDone);

    /// <summary>True when the peer's FIN has been seen (input completed).</summary>
    public bool PeerClosed => _inputDone;

    /// <summary>
    /// One cooperative slice: move up to <paramref name="budget"/> bytes in
    /// each direction. Cheap when there is no traffic.
    /// </summary>
    public void Tick(int budget = DefaultBudget)
    {
        if (_aborted)
            return;
        PumpInput(budget);
        PumpOutput(budget);
    }

    /// <inheritdoc/>
    public override void Abort(Exception? abortReason = null)
    {
        if (_aborted)
            return;
        _aborted = true;
        _tls?.Close();
        _socket.Close();
        _input.Writer.Complete(abortReason);
        _output.Reader.Complete(abortReason);
        _output.Writer.Complete(abortReason);
    }

    // ====================================================================
    // socket -> input pipe
    // ====================================================================

    private unsafe void PumpInput(int budget)
    {
        if (_inputDone)
            return;

        // Resume a back-pressured flush before reading anything new.
        if (_inputFlushPending)
        {
            if (!_pendingInputFlush.IsCompleted)
                return;
            _inputFlushPending = false;
        }

        // M5: drive the TLS 1.3 handshake first. Nothing reaches the input
        // pipe until it completes; a closed/errored handshake ends the
        // stream (the app sees ReadResult.IsCompleted).
        if (_tls != null && !_tlsReady)
        {
            _tls.Step();
            if (_tls.Closed)
            {
                _input.Writer.Complete();
                _inputDone = true;
                return;
            }
            if (!_tls.Connected)
                return;   // handshake still in flight
            _tlsReady = true;
        }

        if (_tls != null)
        {
            // TLS mode: decrypted records -> input pipe (same backpressure
            // rules as the raw path; EOF only via ReadApp -1, i.e. the
            // peer's close_notify or a fatal record error - the mirror of
            // WebConnection's proven TLS read loop).
            int tlsMoved = 0;
            while (tlsMoved < budget)
            {
                int want = _rxScratch.Length;
                if (budget - tlsMoved < want)
                    want = budget - tlsMoved;
                int tn = _tls.ReadApp(_rxScratch, 0, want);
                if (tn < 0)
                {
                    _input.Writer.Complete();
                    _inputDone = true;
                    return;
                }
                if (tn == 0)
                    return;

                Span<byte> tspan = _input.Writer.GetSpan(tn);
                for (int i = 0; i < tn; i++)
                    tspan[i] = _rxScratch[i];
                _input.Writer.Advance(tn);
                tlsMoved += tn;
                LastActivityMs = Timer.GetUptimeMilliseconds();

                ValueTask<FlushResult> tflush = _input.Writer.FlushAsync();
                if (!tflush.IsCompleted)
                {
                    _pendingInputFlush = tflush;
                    _inputFlushPending = true;
                    return;
                }
                if (tflush.Result.IsCompleted)
                {
                    _inputDone = true;
                    return;
                }
            }
            return;
        }

        int moved = 0;
        while (moved < budget)
        {
            TcpState state = _socket.State;
            // States in which the peer's FIN has been received (recv side
            // closed): CloseWait (peer FIN first), Closing (simultaneous),
            // LastAck (our FIN first, peer FIN + its ACK), TimeWait (our
            // Connection: close FIN went first, peer FIN followed - the
            // common curl pattern), and Closed.
            bool peerGone = state == TcpState.CloseWait || state == TcpState.Closed ||
                            state == TcpState.Closing || state == TcpState.LastAck ||
                            state == TcpState.TimeWait;
            if (peerGone && _socket.Available == 0)
            {
                // FIN seen and everything buffered is moved: EOF.
                _input.Writer.Complete();
                _inputDone = true;
                return;
            }

            int avail = _socket.Available;
            if (avail <= 0)
                return;   // nothing to move right now
            DebugAvailSeen++;

            int want = avail < _rxScratch.Length ? avail : _rxScratch.Length;
            int n;
            fixed (byte* p = _rxScratch)
            {
                n = _socket.Receive(p, want);
            }
            if (n <= 0)
                return;
            DebugSocketReadBytes += n;

            Span<byte> span = _input.Writer.GetSpan(n);
            for (int i = 0; i < n; i++)
                span[i] = _rxScratch[i];
            _input.Writer.Advance(n);
            moved += n;
            LastActivityMs = Timer.GetUptimeMilliseconds();

            ValueTask<FlushResult> flush = _input.Writer.FlushAsync();
            if (!flush.IsCompleted)
            {
                // Pipe backpressure: wait for the application to advance.
                _pendingInputFlush = flush;
                _inputFlushPending = true;
                return;
            }
            if (flush.Result.IsCompleted)
            {
                // Application completed its reader: drop the input side.
                _inputDone = true;
                return;
            }
        }
    }

    // ====================================================================
    // output pipe -> socket
    // ====================================================================

    private unsafe void PumpOutput(int budget)
    {
        if (_outputDone)
            return;

        // M5: the handshake must complete before application records go
        // out (PumpInput drives it).
        if (_tls != null && !_tlsReady)
            return;

        int moved = 0;
        while (moved < budget)
        {
            if (!_output.Reader.TryRead(out ReadResult result))
                return;
            DebugOutTryReads++;

            ReadOnlySequence<byte> buffer = result.Buffer;
            bool fullyConsumed = buffer.IsEmpty;
            if (!buffer.IsEmpty)
            {
                // Copy the first memory only and advance the reader by exactly
                // the sent amount. Full segment iteration (TryGet) is still
                // affected by Tier-0 JIT cast bugs during M2 bring-up; the
                // next TryRead after a partial advance surfaces the remainder.
                ReadOnlyMemory<byte> mem = buffer.First;
                long sentTotal = 0;
                if (mem.Length > 0)
                {
                    ReadOnlySpan<byte> ms = mem.Span;
                    int off = 0;
                    while (off < ms.Length)
                    {
                        int len = ms.Length - off;
                        if (len > _txScratch.Length)
                            len = _txScratch.Length;
                        for (int i = 0; i < len; i++)
                            _txScratch[i] = ms[off + i];
                        int sent;
                        if (_tls != null)
                        {
                            // Encrypted transport: WriteApp seals the chunk
                            // (records <=16 KB) and send-alls it. A closed
                            // session ends the output side.
                            _tls.WriteApp(_txScratch, 0, len);
                            sent = _tls.Closed ? 0 : len;
                            if (sent == 0)
                            {
                                _outputDone = true;
                                return;
                            }
                        }
                        else
                        {
                            fixed (byte* p = _txScratch)
                            {
                                sent = _socket.Send(p, len);
                            }
                        }
                        if (sent > 0)
                            DebugSocketSentBytes += sent;
                        else
                            DebugOutSendZero++;
                        sentTotal += sent;
                        moved += sent;
                        if (sent < len)
                            break;   // send window full: retry next Tick
                        off += len;
                    }
                }

                if (sentTotal == mem.Length && (long)mem.Length == buffer.Length)
                {
                    // The whole buffer was a single memory; it is on the wire.
                    _output.Reader.AdvanceTo(buffer.End);
                    fullyConsumed = true;
                    if (sentTotal > 0)
                        LastActivityMs = Timer.GetUptimeMilliseconds();
                }
                else if (sentTotal > 0)
                {
                    _output.Reader.AdvanceTo(buffer.GetPosition(sentTotal));
                    LastActivityMs = Timer.GetUptimeMilliseconds();
                    if (sentTotal < mem.Length)
                        return;   // window full mid-memory: retry next Tick
                    // First memory fully sent but more memories remain:
                    // re-loop to move the next one (bounded by the budget).
                }
                else
                {
                    return;   // socket accepted nothing this pass
                }
            }

            if (result.IsCompleted && fullyConsumed)
            {
                // Application finished writing and all bytes are on the
                // wire: half-close send (FIN). The receive direction stays
                // open for the peer's remaining bytes. TLS sessions send
                // close_notify first; the FIN follows immediately, and a
                // hard close would race the alert/FIN off the wire.
                if (_tls != null)
                {
                    _tls.SendCloseNotify();
                    _socket.Shutdown();
                }
                else
                {
                    _socket.Shutdown();
                }
                _outputDone = true;
                return;
            }

            if (buffer.IsEmpty && !result.IsCompleted)
                return;   // no data buffered and not completed: nothing to do
        }
    }

    /// <summary>The IDuplexPipe view handed to applications.</summary>
    private sealed class DuplexPipe : IDuplexPipe
    {
        public DuplexPipe(PipeReader input, PipeWriter output)
        {
            Input = input;
            Output = output;
        }

        public PipeReader Input { get; }

        public PipeWriter Output { get; }
    }
}
