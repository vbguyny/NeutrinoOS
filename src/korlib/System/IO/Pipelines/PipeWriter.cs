// NeutrinoOS korlib - PipeWriter (Kestrel port milestone M2)

using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace System.IO.Pipelines
{
    /// <summary>Producer side of a <see cref="Pipe"/>.</summary>
    public abstract class PipeWriter : IBufferWriter<byte>
    {
        public abstract void Advance(int bytes);

        public abstract Memory<byte> GetMemory(int sizeHint = 0);

        public abstract Span<byte> GetSpan(int sizeHint = 0);

        public abstract ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default);

        /// <summary>Cancels a pending flush. No-op when nothing is pending.</summary>
        public virtual void CancelPendingFlush()
        {
        }

        public abstract void Complete(Exception? exception = null);

        /// <summary>Copies <paramref name="source"/> into the pipe and commits it as written.</summary>
        public virtual void Write(ReadOnlySpan<byte> source)
        {
            if (source.Length == 0)
                return;
            var span = GetSpan(source.Length);
            for (int i = 0; i < source.Length; i++)
                span[i] = source[i];
            Advance(source.Length);
        }
    }
}
