// NeutrinoOS korlib - System.IO.MemoryStream
//
// Phase 4: an in-memory stream over a growable byte buffer. Behaves like
// the official MemoryStream for the member subset implemented here: the
// buffer starts empty (or wraps a caller array), reads return 0 at the
// end, writes grow the buffer (doubling), and expanding past the end
// zero-fills the gap.
//
// Deviations from the official BCL: no IBufferWriter/Span-based methods,
// no GetBuffer (only ToArray, which copies), no capacity introspection
// beyond Capacity.

namespace System.IO;

/// <summary>Creates a stream whose backing store is memory.</summary>
public class MemoryStream : Stream
{
    private byte[] _buffer;
    private int _length;
    private int _position;
    private readonly bool _writable;

    /// <summary>Initializes an empty, expandable, writable stream.</summary>
    public MemoryStream() : this(0) { }

    /// <summary>Initializes an empty, expandable, writable stream with the given initial capacity.</summary>
    public MemoryStream(int capacity)
    {
        if (capacity < 0)
            throw new ArgumentOutOfRangeException("capacity");
        _buffer = new byte[capacity];
        _length = 0;
        _writable = true;
    }

    /// <summary>
    /// Initializes a the stream over the provided array. The stream is
    /// fixed-size over the whole array and writable (matching the
    /// official MemoryStream(byte[]) constructor).
    /// </summary>
    public MemoryStream(byte[] buffer)
    {
        if (buffer == null)
            throw new ArgumentNullException("buffer");
        _buffer = buffer;
        _length = buffer.Length;
        _writable = true;
    }

    /// <summary>Initializes the stream over a slice of the provided array.</summary>
    public MemoryStream(byte[] buffer, int index, int count)
    {
        if (buffer == null)
            throw new ArgumentNullException("buffer");
        if (index < 0 || count < 0 || index + count > buffer.Length)
            throw new ArgumentOutOfRangeException("index");
        _buffer = new byte[count];
        for (int i = 0; i < count; i++)
            _buffer[i] = buffer[index + i];
        _length = count;
        _writable = true;
    }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => true;

    /// <inheritdoc/>
    public override bool CanWrite => _writable;

    /// <inheritdoc/>
    public override long Length => _length;

    /// <inheritdoc/>
    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException("value");
            _position = (int)value;
        }
    }

    /// <summary>Gets or sets the number of bytes allocated for this stream (never below Length).</summary>
    public int Capacity
    {
        get => _buffer.Length;
        set
        {
            if (value < _length)
                throw new ArgumentOutOfRangeException("value");
            if (value == _buffer.Length)
                return;
            byte[] larger = new byte[value];
            for (int i = 0; i < _length; i++)
                larger[i] = _buffer[i];
            _buffer = larger;
        }
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (buffer == null)
            throw new ArgumentNullException("buffer");
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException("offset");
        int available = _length - _position;
        if (available <= 0)
            return 0;
        if (count > available)
            count = available;
        for (int i = 0; i < count; i++)
            buffer[offset + i] = _buffer[_position + i];
        _position += count;
        return count;
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (!_writable)
            throw new NotSupportedException("Stream is not writable");
        if (buffer == null)
            throw new ArgumentNullException("buffer");
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException("offset");
        if (count == 0)
            return;

        if (_position > _length)
        {
            EnsureCapacity(_position);
            for (int i = _length; i < _position; i++)
                _buffer[i] = 0;
            _length = _position;
        }
        EnsureCapacity(_position + count);
        for (int i = 0; i < count; i++)
            _buffer[_position + i] = buffer[offset + i];
        _position += count;
        if (_position > _length)
            _length = _position;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
            return;
        int capacity = _buffer.Length == 0 ? 64 : _buffer.Length * 2;
        if (capacity < required)
            capacity = required;
        Capacity = capacity;
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        long target;
        switch (origin)
        {
            case SeekOrigin.Begin:
                target = offset;
                break;
            case SeekOrigin.Current:
                target = _position + offset;
                break;
            case SeekOrigin.End:
                target = _length + offset;
                break;
            default:
                throw new ArgumentOutOfRangeException("origin");
        }
        if (target < 0)
            throw new IOException("Cannot seek before the beginning of the stream");
        _position = (int)target;
        return _position;
    }

    /// <inheritdoc/>
    public override void SetLength(long value)
    {
        if (!_writable)
            throw new NotSupportedException("Stream is not writable");
        if (value < 0)
            throw new ArgumentOutOfRangeException("value");
        int newLength = (int)value;
        EnsureCapacity(newLength);
        if (newLength > _length)
        {
            for (int i = _length; i < newLength; i++)
                _buffer[i] = 0;
        }
        _length = newLength;
        if (_position > newLength)
            _position = newLength;
    }

    /// <summary>Copy of the stream's contents as a byte array.</summary>
    public byte[] ToArray()
    {
        byte[] result = new byte[_length];
        for (int i = 0; i < _length; i++)
            result[i] = _buffer[i];
        return result;
    }

    /// <summary>Writes the entire buffer to another stream.</summary>
    public void WriteTo(Stream destination)
    {
        if (destination == null)
            throw new ArgumentNullException("destination");
        destination.Write(_buffer, 0, _length);
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        // Nothing to flush: the backing store is memory.
    }
}
