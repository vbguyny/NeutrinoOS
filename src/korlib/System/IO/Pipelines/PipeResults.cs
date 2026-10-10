// NeutrinoOS korlib - ReadResult and FlushResult (Kestrel port milestone M2)

using System.Buffers;

namespace System.IO.Pipelines
{
    /// <summary>The result of a PipeReader.ReadAsync or TryRead call.</summary>
    public readonly struct ReadResult
    {
        private readonly ReadOnlySequence<byte> _buffer;
        private readonly bool _isCanceled;
        private readonly bool _isCompleted;

        public ReadResult(ReadOnlySequence<byte> buffer, bool isCanceled, bool isCompleted)
        {
            _buffer = buffer;
            _isCanceled = isCanceled;
            _isCompleted = isCompleted;
        }

        public ReadOnlySequence<byte> Buffer => _buffer;

        public bool IsCanceled => _isCanceled;

        public bool IsCompleted => _isCompleted;
    }

    /// <summary>The result of a PipeWriter.FlushAsync call.</summary>
    public readonly struct FlushResult
    {
        private readonly bool _isCanceled;
        private readonly bool _isCompleted;

        public FlushResult(bool isCanceled, bool isCompleted)
        {
            _isCanceled = isCanceled;
            _isCompleted = isCompleted;
        }

        public bool IsCanceled => _isCanceled;

        public bool IsCompleted => _isCompleted;
    }
}
