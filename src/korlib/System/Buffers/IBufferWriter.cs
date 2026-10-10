// NeutrinoOS korlib - IBufferWriter<T> (Kestrel port milestone M2)
//
// Minimal receiver-side interface used by PipeWriter and future
// serialization code (the shape System.Text.Json and Kestrel expect).

namespace System.Buffers
{
    public interface IBufferWriter<T>
    {
        /// <summary>Notifies the writer that <paramref name="count"/> items were written.</summary>
        void Advance(int count);

        /// <summary>Returns a Memory<T> to write to.</summary>
        Memory<T> GetMemory(int sizeHint = 0);

        /// <summary>Returns a Span<T> to write to.</summary>
        Span<T> GetSpan(int sizeHint = 0);
    }
}
