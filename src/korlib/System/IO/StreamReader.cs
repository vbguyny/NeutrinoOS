// NeutrinoOS korlib - System.IO.StreamReader
//
// Phase 4: a text reader over a byte stream. The reader decodes lazily:
// the first Read/ReadLine/ReadToEnd call pulls the remaining bytes from
// the underlying stream and decodes them with the configured encoding
// (UTF-8 by default), then serves subsequent calls from the decoded
// string. This matches observable ReadLine/ReadToEnd behavior for the
// console-scale files NeutrinoOS targets.
//
// Deviations from the official BCL: no async methods, no DetectEncoding
// (the encoding is fixed at construction), no BufferedReader state
// introspection (Peek advances nothing but may trigger the full read).

namespace System.IO;

/// <summary>Implements a TextReader that reads characters from a byte stream in a particular encoding.</summary>
public class StreamReader : TextReader
{
    private readonly Stream _stream;
    private readonly Text.Encoding _encoding;
    private string? _text;
    private int _position;
    private bool _disposed;

    /// <summary>Initializes a reader for the stream using UTF-8.</summary>
    public StreamReader(Stream stream)
        : this(stream, Text.Encoding.UTF8)
    {
    }

    /// <summary>Initializes a reader for the stream using the specified encoding.</summary>
    public StreamReader(Stream stream, Text.Encoding encoding)
    {
        if (stream == null)
            throw new ArgumentNullException("stream");
        if (encoding == null)
            throw new ArgumentNullException("encoding");
        _stream = stream;
        _encoding = encoding;
    }

    /// <summary>Opens a file for reading and decodes UTF-8 (see the file header).</summary>
    public static StreamReader OpenText(string path)
        => new StreamReader(File.OpenRead(path));

    private string EnsureText()
    {
        if (_disposed)
            throw new IOException("Reader is closed");
        if (_text == null)
        {
            byte[] bytes = ReadAllRemaining();
            _text = _encoding.GetString(bytes, 0, bytes.Length);
        }
        return _text;
    }

    private byte[] ReadAllRemaining()
    {
        Collections.Generic.List<byte> bytes = new Collections.Generic.List<byte>();
        byte[] chunk = new byte[512];
        int read;
        while ((read = _stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                bytes.Add(chunk[i]);
        }
        byte[] result = new byte[bytes.Count];
        for (int i = 0; i < bytes.Count; i++)
            result[i] = bytes[i];
        return result;
    }

    /// <summary>Reads the next character without advancing, or -1 at the end.</summary>
    public override int Peek()
    {
        string text = EnsureText();
        return _position < text.Length ? text[_position] : -1;
    }

    /// <summary>Reads the next character and advances, or -1 at the end.</summary>
    public override int Read()
    {
        string text = EnsureText();
        if (_position >= text.Length)
            return -1;
        return text[_position++];
    }

    /// <summary>
    /// Reads a line terminated by '\n' (a trailing '\r' is stripped), or
    /// null at the end of the stream.
    /// </summary>
    public override string? ReadLine()
    {
        string text = EnsureText();
        if (_position >= text.Length)
            return null;
        int start = _position;
        while (_position < text.Length && text[_position] != '\n')
            _position++;
        int end = _position;
        if (_position < text.Length)
            _position++; // consume '\n'
        if (end > start && text[end - 1] == '\r')
            end--;
        return text.Substring(start, end - start);
    }

    /// <summary>Reads all remaining characters as a single string ("" at the end).</summary>
    public override string ReadToEnd()
    {
        string text = EnsureText();
        if (_position >= text.Length)
            return "";
        string remaining = text.Substring(_position);
        _position = text.Length;
        return remaining;
    }

    /// <summary>Reads up to count characters into the buffer; returns the number read (0 at the end).</summary>
    public override int Read(char[] buffer, int index, int count)
    {
        if (buffer == null)
            throw new ArgumentNullException("buffer");
        if (index < 0 || count < 0 || index + count > buffer.Length)
            throw new ArgumentOutOfRangeException("index");
        string text = EnsureText();
        int available = text.Length - _position;
        if (available <= 0)
            return 0;
        if (count > available)
            count = available;
        for (int i = 0; i < count; i++)
            buffer[index + i] = text[_position + i];
        _position += count;
        return count;
    }

    /// <inheritdoc/>
    public override void Close()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stream.Close();
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        if (!_disposed)
            Close();
    }
}
