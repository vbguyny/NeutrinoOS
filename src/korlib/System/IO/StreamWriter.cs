// NeutrinoOS korlib - System.IO.StreamWriter
//
// Phase 4: a text writer over a byte stream. Writes are buffered in a
// StringBuilder and encoded (UTF-8 by default) to the underlying stream
// on Flush, on Close/Dispose, and after every write when AutoFlush is
// set. `using`-scoped writers therefore produce complete files.
//
// Deviations from the official BCL: no async methods, no NewLine
// customization beyond the base default ('\n'), no BOM emission.

namespace System.IO;

/// <summary>Implements a TextWriter for writing characters to a stream in a particular encoding.</summary>
public class StreamWriter : TextWriter
{
    private readonly Stream _stream;
    private readonly Text.Encoding _encoding;
    private readonly bool _leaveOpen;
    private readonly Text.StringBuilder _buffer = new Text.StringBuilder();
    private bool _disposed;

    /// <summary>Initializes a writer over the stream using UTF-8.</summary>
    public StreamWriter(Stream stream)
        : this(stream, Text.Encoding.UTF8, false)
    {
    }

    /// <summary>Initializes a writer over the stream using the specified encoding.</summary>
    public StreamWriter(Stream stream, Text.Encoding encoding)
        : this(stream, encoding, false)
    {
    }

    /// <summary>Initializes a writer over the stream; leaveOpen keeps the stream open on Dispose.</summary>
    public StreamWriter(Stream stream, Text.Encoding encoding, bool leaveOpen)
    {
        if (stream == null)
            throw new ArgumentNullException("stream");
        if (encoding == null)
            throw new ArgumentNullException("encoding");
        _stream = stream;
        _encoding = encoding;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Creates or overwrites the file and writes UTF-8 (see the file header).</summary>
    public StreamWriter(string path)
        : this(path, false)
    {
    }

    /// <summary>Creates (append = true appends) the file and writes UTF-8 text to it.</summary>
    public StreamWriter(string path, bool append)
        : this(File.Open(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write))
    {
    }

    /// <summary>When true, every write flushes the buffer to the stream.</summary>
    public bool AutoFlush { get; set; }

    /// <inheritdoc/>
    public override Text.Encoding Encoding => _encoding;

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new IOException("Writer is closed");
    }

    /// <inheritdoc/>
    public override void Write(char value)
    {
        ThrowIfDisposed();
        _buffer.Append(value);
        if (AutoFlush)
            Flush();
    }

    /// <inheritdoc/>
    public override void Write(string? value)
    {
        ThrowIfDisposed();
        if (value != null)
            _buffer.Append(value);
        if (AutoFlush)
            Flush();
    }

    /// <inheritdoc/>
    public override void Write(char[]? buffer)
    {
        ThrowIfDisposed();
        if (buffer != null)
        {
            for (int i = 0; i < buffer.Length; i++)
                _buffer.Append(buffer[i]);
        }
        if (AutoFlush)
            Flush();
    }

    /// <inheritdoc/>
    public override void Write(char[] buffer, int index, int count)
    {
        ThrowIfDisposed();
        for (int i = index; i < index + count; i++)
            _buffer.Append(buffer[i]);
        if (AutoFlush)
            Flush();
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        ThrowIfDisposed();
        if (_buffer.Length == 0)
            return;
        string text = _buffer.ToString();
        _buffer.Clear();
        byte[] bytes = _encoding.GetBytes(text);
        if (bytes.Length > 0)
            _stream.Write(bytes, 0, bytes.Length);
        _stream.Flush();
    }

    /// <inheritdoc/>
    public override void Close()
    {
        if (_disposed)
            return;
        Flush();
        _disposed = true;
        if (!_leaveOpen)
            _stream.Close();
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        if (!_disposed)
            Close();
    }
}
