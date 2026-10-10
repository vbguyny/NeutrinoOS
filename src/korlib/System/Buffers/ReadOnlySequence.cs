// NeutrinoOS korlib - ReadOnlySequence<T> (Kestrel port milestone M2)
//
// BCL-shaped sequence over one or more memory segments, used by
// System.IO.Pipelines and future Kestrel request buffers.
//
// Two modes:
//   - contiguous: built from a ReadOnlyMemory<T> (no segment chain),
//   - segmented: built from a linked ReadOnlySequenceSegment<T> chain.
// Length/positions are computed by walking the chain (no RunningIndex
// dependence) - simple and correct for the cooperative runtime.

using System.Collections.Generic;

namespace System.Buffers
{
    // NOTE: SequencePosition lives in the root System namespace (BCL layout) -
    // see System/SequencePosition.cs.

    public abstract class ReadOnlySequenceSegment<T>
    {
        public ReadOnlyMemory<T> Memory { get; protected set; }

        public ReadOnlySequenceSegment<T>? Next { get; protected set; }

        public long RunningIndex { get; protected set; }
    }

    public readonly struct ReadOnlySequence<T> : IEquatable<ReadOnlySequence<T>>
    {
        private readonly ReadOnlySequenceSegment<T>? _startSegment;
        private readonly int _startIndex;
        private readonly ReadOnlySequenceSegment<T>? _endSegment;
        private readonly int _endIndex;
        private readonly ReadOnlyMemory<T> _memory;
        private readonly bool _isMemory;

        public ReadOnlySequence(T[] array)
        {
            _memory = new ReadOnlyMemory<T>(array);
            _isMemory = true;
            _startSegment = null;
            _startIndex = 0;
            _endSegment = null;
            _endIndex = 0;
        }

        public ReadOnlySequence(ReadOnlyMemory<T> memory)
        {
            _memory = memory;
            _isMemory = true;
            _startSegment = null;
            _startIndex = 0;
            _endSegment = null;
            _endIndex = 0;
        }

        public ReadOnlySequence(
            ReadOnlySequenceSegment<T> startSegment, int startIndex,
            ReadOnlySequenceSegment<T> endSegment, int endIndex)
        {
            if (startSegment == null)
                throw new ArgumentNullException("startSegment");
            if (endSegment == null)
                throw new ArgumentNullException("endSegment");
            if (startSegment == endSegment && endIndex < startIndex)
                throw new ArgumentOutOfRangeException("endIndex");
            _startSegment = startSegment;
            _startIndex = startIndex;
            _endSegment = endSegment;
            _endIndex = endIndex;
            _memory = default;
            _isMemory = false;
        }

        public bool IsSingleSegment => _isMemory || ReferenceEquals(_startSegment, _endSegment);

        public ReadOnlyMemory<T> First =>
            _isMemory ? _memory : _startSegment!.Memory.Slice(_startIndex);

        public ReadOnlySpan<T> FirstSpan => First.Span;

        public bool IsEmpty => Length == 0;

        public long Length
        {
            get
            {
                if (_isMemory)
                    return _memory.Length;
                long len = 0;
                var seg = _startSegment;
                int off = _startIndex;
                while (seg != null)
                {
                    if (ReferenceEquals(seg, _endSegment))
                    {
                        len += _endIndex - off;
                        break;
                    }
                    len += seg.Memory.Length - off;
                    off = 0;
                    seg = seg.Next;
                }
                return len;
            }
        }

        public SequencePosition Start =>
            _isMemory
                ? new SequencePosition(_memory, 0)
                : new SequencePosition(_startSegment, _startIndex);

        public SequencePosition End =>
            _isMemory
                ? new SequencePosition(_memory, _memory.Length)
                : new SequencePosition(_endSegment, _endIndex);

        public SequencePosition GetPosition(long offset)
        {
            if (offset < 0 || offset > Length)
                throw new ArgumentOutOfRangeException("offset");
            if (_isMemory)
                return new SequencePosition(_memory, (int)offset);

            long remaining = offset;
            var seg = _startSegment;
            int off = _startIndex;
            while (seg != null)
            {
                int available = (ReferenceEquals(seg, _endSegment) ? _endIndex : seg.Memory.Length) - off;
                if (remaining <= available)
                    return new SequencePosition(seg, off + (int)remaining);
                remaining -= available;
                seg = seg.Next;
                off = 0;
            }
            return End;
        }

        public ReadOnlySequence<T> Slice(long start) => Slice(start, Length - start);

        public ReadOnlySequence<T> Slice(long start, long length)
        {
            long total = Length;
            if (start < 0 || start > total)
                throw new ArgumentOutOfRangeException("start");
            if (length < 0 || length > total - start)
                throw new ArgumentOutOfRangeException("length");
            var begin = GetPosition(start);
            var end = GetPosition(start + length);
            return Slice(begin, end);
        }

        public ReadOnlySequence<T> Slice(int start) => Slice((long)start);

        public ReadOnlySequence<T> Slice(int start, int length) => Slice((long)start, (long)length);

        public ReadOnlySequence<T> Slice(SequencePosition start) => Slice(start, End);

        public ReadOnlySequence<T> Slice(SequencePosition start, SequencePosition end)
        {
            if (_isMemory)
            {
                int s = start.GetInteger();
                int e = end.GetInteger();
                if (s < 0) s = 0;
                if (e > _memory.Length) e = _memory.Length;
                if (e < s) e = s;
                return new ReadOnlySequence<T>(_memory.Slice(s, e - s));
            }
            var sSeg = (ReadOnlySequenceSegment<T>?)start.GetObject() ?? _startSegment!;
            var eSeg = (ReadOnlySequenceSegment<T>?)end.GetObject() ?? _endSegment!;
            return new ReadOnlySequence<T>(sSeg, start.GetInteger(), eSeg, end.GetInteger());
        }

        public ReadOnlySequence<T> Slice(SequencePosition start, int length)
        {
            if (_isMemory)
                return new ReadOnlySequence<T>(_memory.Slice(start.GetInteger(), length));

            var sSeg = (ReadOnlySequenceSegment<T>?)start.GetObject() ?? _startSegment!;
            int sIdx = start.GetInteger();
            long remaining = length;
            var seg = sSeg;
            int off = sIdx;
            while (seg != null)
            {
                int available = (ReferenceEquals(seg, _endSegment) ? _endIndex : seg.Memory.Length) - off;
                if (remaining <= available)
                    return new ReadOnlySequence<T>(sSeg, sIdx, seg, off + (int)remaining);
                remaining -= available;
                seg = seg.Next;
                off = 0;
            }
            throw new ArgumentOutOfRangeException("length");
        }

        public bool TryGet(ref SequencePosition position, out ReadOnlyMemory<T> memory, bool advance = true)
        {
            memory = default;
            if (_isMemory)
            {
                int idx = position.GetInteger();
                if (idx < 0 || idx > _memory.Length)
                    return false;
                memory = _memory.Slice(idx);
                if (advance)
                    position = new SequencePosition(_memory, _memory.Length);
                return true;
            }

            var seg = position.GetObject() as ReadOnlySequenceSegment<T>;
            if (seg == null)
                return false;
            int si = position.GetInteger();
            int end = ReferenceEquals(seg, _endSegment) ? _endIndex : seg.Memory.Length;
            if (si < 0 || si > end)
                return false;
            memory = seg.Memory.Slice(si, end - si);
            if (advance)
                position = ReferenceEquals(seg, _endSegment)
                    ? new SequencePosition(_endSegment, _endIndex)
                    : new SequencePosition(seg.Next, 0);
            return true;
        }

        public T[] ToArray()
        {
            var result = new T[Length];
            int pos = 0;
            foreach (var memory in this)
            {
                int n = memory.Length;
                for (int i = 0; i < n; i++)
                    result[pos + i] = memory[i];
                pos += n;
            }
            return result;
        }

        public Enumerator GetEnumerator() => new Enumerator(this);

        public bool Equals(ReadOnlySequence<T> other)
        {
            if (Length != other.Length)
                return false;
            if (_isMemory && other._isMemory)
                return _memory.Equals(other._memory);
            var e1 = GetEnumerator();
            var e2 = other.GetEnumerator();
            while (e1.MoveNext() && e2.MoveNext())
            {
                var m1 = e1.Current;
                var m2 = e2.Current;
                if (m1.Length != m2.Length)
                    return false;
                for (int i = 0; i < m1.Length; i++)
                {
                    if (!EqualityComparer<T>.Default.Equals(m1[i], m2[i]))
                        return false;
                }
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is ReadOnlySequence<T> other && Equals(other);

        public override int GetHashCode() => Start.GetHashCode() ^ End.GetHashCode();

        public struct Enumerator
        {
            private readonly ReadOnlySequence<T> _sequence;
            private ReadOnlySequenceSegment<T>? _segment;
            private int _index;
            private bool _isMemory;
            private bool _memoryDone;
            private ReadOnlyMemory<T> _current;

            internal Enumerator(ReadOnlySequence<T> sequence)
            {
                _sequence = sequence;
                _isMemory = sequence._isMemory;
                _segment = sequence._startSegment;
                _index = sequence._startIndex;
                _memoryDone = false;
                _current = default;
            }

            public ReadOnlyMemory<T> Current => _current;

            public bool MoveNext()
            {
                if (_isMemory)
                {
                    if (_memoryDone)
                        return false;
                    _memoryDone = true;
                    _current = _sequence._memory;
                    return !_current.IsEmpty;
                }

                while (_segment != null)
                {
                    int end = ReferenceEquals(_segment, _sequence._endSegment)
                        ? _sequence._endIndex
                        : _segment.Memory.Length;
                    if (end > _index)
                    {
                        _current = _segment.Memory.Slice(_index, end - _index);
                        _segment = ReferenceEquals(_segment, _sequence._endSegment)
                            ? null
                            : _segment.Next;
                        _index = 0;
                        return true;
                    }
                    _segment = ReferenceEquals(_segment, _sequence._endSegment)
                        ? null
                        : _segment.Next;
                    _index = 0;
                }
                return false;
            }
        }
    }
}
