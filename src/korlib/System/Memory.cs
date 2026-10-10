// NeutrinoOS korlib - Memory<T> and ReadOnlyMemory<T> (Kestrel port milestone M2)
//
// Array-backed implementations matching the BCL surface used by
// System.IO.Pipelines and the Kestrel port. A Memory<T> always references a
// managed array plus a start index and length; the owner-based model
// (MemoryManager<T>, custom owners) is intentionally not implemented.
//
// The Span properties build their spans via MemoryMarshal.CreateSpan /
// CreateReadOnlySpan, which the Tier-0 JIT provides as an intrinsic (the
// Span<T>(T[],int,int) constructor itself is not JIT-supported).

using System.Runtime.InteropServices;

namespace System
{
    public readonly struct Memory<T> : IEquatable<Memory<T>>
    {
        private readonly T[]? _array;
        private readonly int _index;
        private readonly int _length;

        public Memory(T[]? array)
        {
            if (array == null)
            {
                this = default;
                return;
            }
            _array = array;
            _index = 0;
            _length = array.Length;
        }

        public Memory(T[]? array, int start, int length)
        {
            if (array == null)
            {
                if (start != 0 || length != 0)
                    throw new ArgumentOutOfRangeException("start");
                this = default;
                return;
            }
            if ((uint)start > (uint)array.Length || (uint)length > (uint)(array.Length - start))
                throw new ArgumentOutOfRangeException("length");
            _array = array;
            _index = start;
            _length = length;
        }

        public static Memory<T> Empty => default;

        public int Length => _length;
        public bool IsEmpty => _length == 0;

        /// <summary>View of this memory as a span.</summary>
        public Span<T> Span
        {
            get
            {
                if (_array == null || _length == 0)
                    return default;
                return MemoryMarshal.CreateSpan(ref _array[_index], _length);
            }
        }

        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_length)
                    throw new IndexOutOfRangeException();
                return _array![_index + index];
            }
            set
            {
                if ((uint)index >= (uint)_length)
                    throw new IndexOutOfRangeException();
                _array![_index + index] = value;
            }
        }

        public Memory<T> Slice(int start)
        {
            if ((uint)start > (uint)_length)
                throw new ArgumentOutOfRangeException("start");
            return new Memory<T>(_array, _index + start, _length - start);
        }

        public Memory<T> Slice(int start, int length)
        {
            if ((uint)start > (uint)_length || (uint)length > (uint)(_length - start))
                throw new ArgumentOutOfRangeException("length");
            return new Memory<T>(_array, _index + start, length);
        }

        public T[] ToArray()
        {
            var result = new T[_length];
            if (_length > 0)
                Array.Copy(_array!, _index, result, 0, _length);
            return result;
        }

        public void CopyTo(Memory<T> destination)
        {
            if (_length > destination._length)
                throw new ArgumentException("Destination is too short");
            if (_length > 0)
                Array.Copy(_array!, _index, destination._array!, destination._index, _length);
        }

        public bool TryCopyTo(Memory<T> destination)
        {
            if (_length > destination._length)
                return false;
            CopyTo(destination);
            return true;
        }

        public void Clear()
        {
            for (int i = 0; i < _length; i++)
                _array![_index + i] = default!;
        }

        public static implicit operator Memory<T>(T[]? array) => new Memory<T>(array);

        public static implicit operator ReadOnlyMemory<T>(Memory<T> memory) =>
            new ReadOnlyMemory<T>(memory._array, memory._index, memory._length);

        public bool Equals(Memory<T> other) =>
            _array == other._array && _index == other._index && _length == other._length;

        public override bool Equals(object? obj) => obj is Memory<T> other && Equals(other);

        public override int GetHashCode() =>
            _array == null ? 0 : _array.GetHashCode() ^ _index ^ _length;

        public static bool operator ==(Memory<T> left, Memory<T> right) => left.Equals(right);
        public static bool operator !=(Memory<T> left, Memory<T> right) => !left.Equals(right);
    }

    public readonly struct ReadOnlyMemory<T> : IEquatable<ReadOnlyMemory<T>>
    {
        private readonly T[]? _array;
        private readonly int _index;
        private readonly int _length;

        public ReadOnlyMemory(T[]? array)
        {
            if (array == null)
            {
                this = default;
                return;
            }
            _array = array;
            _index = 0;
            _length = array.Length;
        }

        public ReadOnlyMemory(T[]? array, int start, int length)
        {
            if (array == null)
            {
                if (start != 0 || length != 0)
                    throw new ArgumentOutOfRangeException("start");
                this = default;
                return;
            }
            if ((uint)start > (uint)array.Length || (uint)length > (uint)(array.Length - start))
                throw new ArgumentOutOfRangeException("length");
            _array = array;
            _index = start;
            _length = length;
        }

        public static ReadOnlyMemory<T> Empty => default;

        public int Length => _length;
        public bool IsEmpty => _length == 0;

        /// <summary>View of this memory as a read-only span.</summary>
        public ReadOnlySpan<T> Span
        {
            get
            {
                if (_array == null || _length == 0)
                    return default;
                return MemoryMarshal.CreateReadOnlySpan(ref _array[_index], _length);
            }
        }

        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_length)
                    throw new IndexOutOfRangeException();
                return _array![_index + index];
            }
        }

        public ReadOnlyMemory<T> Slice(int start)
        {
            if ((uint)start > (uint)_length)
                throw new ArgumentOutOfRangeException("start");
            return new ReadOnlyMemory<T>(_array, _index + start, _length - start);
        }

        public ReadOnlyMemory<T> Slice(int start, int length)
        {
            if ((uint)start > (uint)_length || (uint)length > (uint)(_length - start))
                throw new ArgumentOutOfRangeException("length");
            return new ReadOnlyMemory<T>(_array, _index + start, length);
        }

        public T[] ToArray()
        {
            var result = new T[_length];
            if (_length > 0)
                Array.Copy(_array!, _index, result, 0, _length);
            return result;
        }

        public void CopyTo(Memory<T> destination)
        {
            if (_length > destination.Length)
                throw new ArgumentException("Destination is too short");
            for (int i = 0; i < _length; i++)
                destination[i] = _array![_index + i];
        }

        public bool TryCopyTo(Memory<T> destination)
        {
            if (_length > destination.Length)
                return false;
            CopyTo(destination);
            return true;
        }

        public static implicit operator ReadOnlyMemory<T>(T[]? array) => new ReadOnlyMemory<T>(array);

        public bool Equals(ReadOnlyMemory<T> other) =>
            _array == other._array && _index == other._index && _length == other._length;

        public override bool Equals(object? obj) => obj is ReadOnlyMemory<T> other && Equals(other);

        public override int GetHashCode() =>
            _array == null ? 0 : _array.GetHashCode() ^ _index ^ _length;

        public static bool operator ==(ReadOnlyMemory<T> left, ReadOnlyMemory<T> right) => left.Equals(right);
        public static bool operator !=(ReadOnlyMemory<T> left, ReadOnlyMemory<T> right) => !left.Equals(right);
    }
}
