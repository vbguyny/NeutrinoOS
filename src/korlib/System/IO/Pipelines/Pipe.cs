// NeutrinoOS korlib - Pipe (Kestrel port milestone M2)
//
// Single-reader/single-writer pipe over pooled segments (ArrayPool), with
// BCL-compatible commit/consume semantics:
//
//   writer: GetMemory/GetSpan -> Advance -> FlushAsync (commits to reader)
//   reader: ReadAsync/TryRead -> inspect Buffer -> AdvanceTo (consumes)
//
// Backpressure: when unread committed bytes exceed PauseWriterThreshold,
// FlushAsync returns an incomplete ValueTask that completes once the reader
// advances below ResumeWriterThreshold. Cooperative single-threaded use
// (JIT tests, service tick) - see the thread-safety note in ArrayPool.cs.

using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace System.IO.Pipelines
{
    /// <summary>A pooled byte-buffer segment presented as a sequence segment.</summary>
    internal sealed class PipeSegment : ReadOnlySequenceSegment<byte>
    {
        internal byte[] Data;
        internal int Length;                 // bytes written (committed at flush time)

        internal PipeSegment(byte[] data)
        {
            Data = data;
        }

        internal void SetNextSegment(PipeSegment? next) => Next = next;

        internal void SetRunningIndex(long value) => RunningIndex = value;

        /// <summary>Publishes the committed extent (0..Length) to sequence consumers.</summary>
        internal void RefreshMemory() => Memory = new ReadOnlyMemory<byte>(Data, 0, Length);
    }

    public class Pipe
    {
        private readonly PipeOptions _options;
        private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Shared;
        private readonly WriterImpl _writer;
        private readonly ReaderImpl _reader;

        // Physical segment chain.
        private PipeSegment? _firstSegment;      // head of chain (may be partially consumed)
        private PipeSegment? _writeSegment;      // tail being written into
        private int _flushedLength;              // bytes of _writeSegment committed at last flush

        // Reader cursor (consumed position).
        private PipeSegment? _consumedSegment;
        private int _consumedIndex;

        // Committed window.
        private PipeSegment? _readTailSegment;
        private int _readTailIndex;
        private long _committedBytes;
        private long _consumedBytes;

        // Waiters and state.
        private TaskCompletionSource<ReadResult>? _readerWaiting;
        private TaskCompletionSource<FlushResult>? _writerWaiting;
        private bool _writerCompleted;
        private bool _readerCompleted;
        private bool _cancelReadPending;

        public Pipe() : this(PipeOptions.Default)
        {
        }

        public Pipe(PipeOptions options)
        {
            if (options == null)
                throw new ArgumentNullException("options");
            _options = options;
            _writer = new WriterImpl(this);
            _reader = new ReaderImpl(this);
        }

        public PipeWriter Writer => _writer;

        public PipeReader Reader => _reader;

        // ==================== writer side ====================

        private Memory<byte> GetMemoryCore(int sizeHint)
        {
            if (_readerCompleted)
                return new Memory<byte>(new byte[0]);

            int needed = sizeHint <= 0 ? 1 : sizeHint;
            var seg = _writeSegment;
            if (seg == null || seg.Data.Length - seg.Length < needed)
            {
                int size = needed > _options.MinimumSegmentSize ? needed : _options.MinimumSegmentSize;
                var next = new PipeSegment(_pool.Rent(size));
                if (seg != null)
                {
                    seg.SetNextSegment(next);
                    next.SetRunningIndex(seg.RunningIndex + seg.Data.Length);
                }
                else
                {
                    _firstSegment = next;
                    _consumedSegment = next;
                    _readTailSegment = next;
                    _readTailIndex = 0;
                }
                _writeSegment = next;
                _flushedLength = 0;
                seg = next;
            }
            return new Memory<byte>(seg.Data, seg.Length, seg.Data.Length - seg.Length);
        }

        private void AdvanceCore(int bytes)
        {
            var seg = _writeSegment;
            if (seg == null || bytes < 0 || bytes > seg.Data.Length - seg.Length)
                throw new InvalidOperationException("PipeWriter.Advance beyond the rented buffer");
            seg.Length += bytes;
        }

        private ValueTask<FlushResult> FlushCore(CancellationToken token)
        {
            if (token.IsCancellationRequested)
                return ValueTask<FlushResult>.FromCanceled(token);

            if (_readerCompleted)
                return new ValueTask<FlushResult>(new FlushResult(false, true));

            CommitPending();

            if (_writerCompleted)
                return new ValueTask<FlushResult>(new FlushResult(false, true));

            WakeReader();

            long unconsumed = _committedBytes - _consumedBytes;
            if (unconsumed > _options.PauseWriterThreshold)
            {
                _writerWaiting = new TaskCompletionSource<FlushResult>();
                return new ValueTask<FlushResult>(_writerWaiting.Task);
            }
            return new ValueTask<FlushResult>(new FlushResult(false, false));
        }

        private void CommitPending()
        {
            var seg = _writeSegment;
            if (seg != null && seg.Length > _flushedLength)
            {
                _committedBytes += seg.Length - _flushedLength;
                _flushedLength = seg.Length;
                _readTailSegment = seg;
                _readTailIndex = seg.Length;
                seg.RefreshMemory();
            }
        }

        private void CancelPendingFlushCore()
        {
            var ww = _writerWaiting;
            if (ww != null)
            {
                _writerWaiting = null;
                ww.TrySetResult(new FlushResult(true, false));
            }
        }

        private void WriterCompleteCore()
        {
            _writerCompleted = true;
            CommitPending();
            WakeReader();
            if (_readerCompleted)
                FreeAll();
        }

        // ==================== reader side ====================

        private bool TryReadCore(out ReadResult result)
        {
            if (_readTailSegment != null && _committedBytes > _consumedBytes)
            {
                result = MakeReadResult(false);
                return true;
            }
            if (_writerCompleted)
            {
                result = new ReadResult(default, false, true);
                return true;
            }
            result = default;
            return false;
        }

        private ValueTask<ReadResult> ReadAsyncCore(CancellationToken token)
        {
            if (token.IsCancellationRequested)
                return ValueTask<ReadResult>.FromCanceled(token);

            if (_cancelReadPending)
            {
                _cancelReadPending = false;
                return new ValueTask<ReadResult>(MakeReadResult(true));
            }

            if (TryReadCore(out var result))
                return new ValueTask<ReadResult>(result);

            _readerWaiting = new TaskCompletionSource<ReadResult>();
            return new ValueTask<ReadResult>(_readerWaiting.Task);
        }

        private void AdvanceToCore(SequencePosition consumed, SequencePosition examined)
        {
            if (_consumedSegment == null || _readTailSegment == null)
                return;

            var cSeg = consumed.GetObject() as PipeSegment;
            int cIdx = consumed.GetInteger();
            if (cSeg == null)
                return;

            long delta = Distance(_consumedSegment, _consumedIndex, cSeg, cIdx);
            if (delta < 0)
                return;                            // stale/foreign position: ignore

            _consumedBytes += delta;
            _consumedSegment = cSeg;
            _consumedIndex = cIdx;

            // Return fully consumed segments to the pool. The segment holding
            // the consumed position is retained (it may still be referenced).
            while (_firstSegment != null && !ReferenceEquals(_firstSegment, _consumedSegment))
            {
                var old = _firstSegment;
                var next = old.Next as PipeSegment;
                if (next == null)
                    break;
                _firstSegment = next;
                old.SetNextSegment(null);
                _pool.Return(old.Data);
            }

            long unconsumed = _committedBytes - _consumedBytes;
            var ww = _writerWaiting;
            if (ww != null && unconsumed <= _options.ResumeWriterThreshold)
            {
                _writerWaiting = null;
                ww.TrySetResult(new FlushResult(false, false));
            }
        }

        private void CancelPendingReadCore()
        {
            var rw = _readerWaiting;
            if (rw != null)
            {
                _readerWaiting = null;
                rw.TrySetResult(new ReadResult(default, true, false));
            }
            else
            {
                _cancelReadPending = true;
            }
        }

        private void ReaderCompleteCore()
        {
            _readerCompleted = true;
            var ww = _writerWaiting;
            if (ww != null)
            {
                _writerWaiting = null;
                ww.TrySetResult(new FlushResult(false, true));
            }
            FreeAll();
        }

        // ==================== shared helpers ====================

        public void Reset()
        {
            if (_committedBytes > _consumedBytes)
                throw new InvalidOperationException("Cannot reset a pipe with unread data");
            FreeAll();
            _writerCompleted = false;
            _readerCompleted = false;
            _cancelReadPending = false;
            _readerWaiting = null;
            _writerWaiting = null;
        }

        private void FreeAll()
        {
            var seg = _firstSegment;
            while (seg != null)
            {
                var next = seg.Next as PipeSegment;
                seg.SetNextSegment(null);
                _pool.Return(seg.Data);
                seg = next;
            }
            _firstSegment = null;
            _writeSegment = null;
            _consumedSegment = null;
            _readTailSegment = null;
            _readTailIndex = 0;
            _flushedLength = 0;
            _committedBytes = 0;
            _consumedBytes = 0;
        }

        private void WakeReader()
        {
            var rw = _readerWaiting;
            if (rw != null)
            {
                _readerWaiting = null;
                rw.TrySetResult(MakeReadResult(false));
            }
        }

        private ReadResult MakeReadResult(bool canceled)
        {
            return new ReadResult(BuildSequence(), canceled, _writerCompleted);
        }

        private ReadOnlySequence<byte> BuildSequence()
        {
            if (_consumedSegment == null || _readTailSegment == null)
                return default;
            if (ReferenceEquals(_consumedSegment, _readTailSegment) && _consumedIndex == _readTailIndex)
                return default;
            return new ReadOnlySequence<byte>(_consumedSegment, _consumedIndex, _readTailSegment, _readTailIndex);
        }

        /// <summary>Bytes between two positions of this pipe's chain.</summary>
        private static long Distance(PipeSegment from, int fromIdx, PipeSegment to, int toIdx)
        {
            if (ReferenceEquals(from, to))
                return toIdx - fromIdx;
            long d = from.Memory.Length - fromIdx;
            var s = from.Next;
            while (s != null && !ReferenceEquals(s, to))
            {
                d += s.Memory.Length;
                s = s.Next;
            }
            if (s == null)
                return -1;                          // not connected
            return d + toIdx;
        }

        // ==================== pipe end wrappers ====================

        private sealed class WriterImpl : PipeWriter
        {
            private readonly Pipe _pipe;

            internal WriterImpl(Pipe pipe)
            {
                _pipe = pipe;
            }

            public override void Advance(int bytes) => _pipe.AdvanceCore(bytes);

            public override Memory<byte> GetMemory(int sizeHint = 0) => _pipe.GetMemoryCore(sizeHint);

            public override Span<byte> GetSpan(int sizeHint = 0) => _pipe.GetMemoryCore(sizeHint).Span;

            public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) =>
                _pipe.FlushCore(cancellationToken);

            public override void CancelPendingFlush() => _pipe.CancelPendingFlushCore();

            public override void Complete(Exception? exception = null) => _pipe.WriterCompleteCore();
        }

        private sealed class ReaderImpl : PipeReader
        {
            private readonly Pipe _pipe;

            internal ReaderImpl(Pipe pipe)
            {
                _pipe = pipe;
            }

            public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
                _pipe.ReadAsyncCore(cancellationToken);

            public override bool TryRead(out ReadResult result) => _pipe.TryReadCore(out result);

            public override void AdvanceTo(SequencePosition consumed) => _pipe.AdvanceToCore(consumed, consumed);

            public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) =>
                _pipe.AdvanceToCore(consumed, examined);

            public override void CancelPendingRead() => _pipe.CancelPendingReadCore();

            public override void Complete(Exception? exception = null) => _pipe.ReaderCompleteCore();
        }
    }
}
