// NeutrinoOS korlib - PipeReader (Kestrel port milestone M2)

using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace System.IO.Pipelines
{
    /// <summary>Consumer side of a <see cref="Pipe"/>.</summary>
    public abstract class PipeReader
    {
        /// <summary>
        /// Asynchronously reads data. Completes when data is available, when the
        /// writer completes, or when the read is canceled.
        /// </summary>
        public abstract ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default);

        /// <summary>Synchronously reads data if available.</summary>
        public abstract bool TryRead(out ReadResult result);

        /// <summary>Moves the reader forward, marking <paramref name="consumed"/> bytes as processed.</summary>
        public abstract void AdvanceTo(SequencePosition consumed);

        /// <summary>Moves the reader forward; <paramref name="examined"/> may be ahead of <paramref name="consumed"/>.</summary>
        public abstract void AdvanceTo(SequencePosition consumed, SequencePosition examined);

        /// <summary>Cancels the pending read; the next ReadAsync returns a canceled result.</summary>
        public abstract void CancelPendingRead();

        public abstract void Complete(Exception? exception = null);
    }
}
