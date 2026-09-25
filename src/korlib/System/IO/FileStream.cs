// NeutrinoOS korlib - System.IO.FileStream (+ FileMode/FileAccess/FileShare)
//
// Phase 4: a file stream over the NeutrinoOS boot (FAT32) volume.
//
// DESIGN (whole-file buffering)
// -----------------------------
// The kernel bridge transfers complete files (read-all / write-all), so a
// FileStream keeps the file contents in a managed byte buffer while open:
//   * Open loads the file (or starts empty for the create modes).
//   * Read/Write/Seek/SetLength operate on the in-memory buffer.
//   * Flush/Close/Dispose writes the buffer back through the bridge in a
//     single truncate-and-rewrite operation.
// This preserves the observable stream semantics for the console-scale
// files NeutrinoOS targets (the official BCL streams incrementally; that
// distinction is not observable without concurrent writers, which the
// FAT bridge does not support either). A zero-length file is created on
// first flush even when nothing was written, so `Create` + `Dispose`
// materializes an empty file like the BCL.
//
// Deviations from the official BCL: no file sharing/locking enforcement
// (FileShare is accepted and ignored), no async methods, no SafeFileHandle.

namespace System.IO;

/// <summary>Specifies how the operating system should open a file.</summary>
public enum FileMode
{
    // Phase 8: values match the BCL (System.IO.FileMode) on purpose: code
    // compiled against the BCL (utilities, libraries) const-folds these
    // numeric values, so a 0-based enum here would mis-route switches and
    // range checks at run time under the JIT.
    /// <summary>Creates a new file; if the file exists, an IOException is thrown.</summary>
    CreateNew = 1,
    /// <summary>Creates a new file or truncates an existing one.</summary>
    Create = 2,
    /// <summary>Opens an existing file; FileNotFoundException when missing.</summary>
    Open = 3,
    /// <summary>Opens an existing file or creates a new one.</summary>
    OpenOrCreate = 4,
    /// <summary>Opens and truncates an existing file; FileNotFoundException when missing.</summary>
    Truncate = 5,
    /// <summary>Opens a file and seeks to its end (creating it when missing); writes always append.</summary>
    Append = 6,
}

/// <summary>Specifies the access allowed to a file.</summary>
public enum FileAccess
{
    /// <summary>Read access.</summary>
    Read = 1,
    /// <summary>Write access.</summary>
    Write = 2,
    /// <summary>Read and write access.</summary>
    ReadWrite = 3,
}

/// <summary>
/// Specifies how the file is shared. NeutrinoOS has no concurrent open
/// tracking; the value is accepted for API compatibility and ignored.
/// </summary>
public enum FileShare
{
    /// <summary>No sharing (treated as allow-all on NeutrinoOS).</summary>
    None = 0,
    /// <summary>Allow subsequent readers.</summary>
    Read = 1,
    /// <summary>Allow subsequent writers.</summary>
    Write = 2,
    /// <summary>Allow subsequent readers and writers.</summary>
    ReadWrite = 3,
    /// <summary>Allow subsequent deleters.</summary>
    Delete = 4,
    /// <summary>Allow all subsequent opens.</summary>
    ReadWriteDelete = 7,
}

/// <summary>
/// A seekable, readable, writable view of a file on the boot volume
/// (whole-file buffered - see the file header).
/// </summary>
public class FileStream : Stream
{
    private readonly string _path;
    private byte[] _buffer;
    private int _length;
    private int _position;
    private readonly bool _canRead;
    private readonly bool _canWrite;
    private readonly bool _appendMode;
    private bool _dirty;
    private bool _closed;

    /// <summary>Opens the specified path with the given mode and read/write access.</summary>
    public FileStream(string path, FileMode mode, FileAccess access)
        : this(path, mode, access, FileShare.Read)
    {
    }

    /// <summary>Opens the specified path; share is accepted and ignored (see FileShare).</summary>
    public FileStream(string path, FileMode mode, FileAccess access, FileShare share)
    {
        if (path == null)
            throw new ArgumentNullException("path");

        // Phase 5: relative paths resolve against the current directory.
        path = Path.GetFullPath(path);

        _path = path;
        _canRead = (access & FileAccess.Read) != 0;
        _canWrite = (access & FileAccess.Write) != 0;
        if (!_canRead && !_canWrite)
            throw new ArgumentException("FileAccess must include Read or Write");

        bool exists;
        switch (mode)
        {
            case FileMode.CreateNew:
                if (File.Exists(path))
                    throw new IOException("The file '" + path + "' already exists");
                _buffer = new byte[0];
                _dirty = true;
                break;
            case FileMode.Create:
                _buffer = new byte[0];
                _dirty = true;
                break;
            case FileMode.Open:
                exists = File.Exists(path);
                if (!exists)
                    throw new FileNotFoundException("Could not find file '" + path + "'");
                _buffer = File.ReadAllBytes(path);
                break;
            case FileMode.OpenOrCreate:
                exists = File.Exists(path);
                if (exists)
                {
                    _buffer = File.ReadAllBytes(path);
                }
                else
                {
                    _buffer = new byte[0];
                    _dirty = true;
                }
                break;
            case FileMode.Truncate:
                exists = File.Exists(path);
                if (!exists)
                    throw new FileNotFoundException("Could not find file '" + path + "'");
                _buffer = new byte[0];
                _dirty = true;
                break;
            case FileMode.Append:
                exists = File.Exists(path);
                _buffer = exists ? File.ReadAllBytes(path) : new byte[0];
                if (!exists)
                    _dirty = true;
                _appendMode = true;
                break;
            default:
                throw new ArgumentOutOfRangeException("mode");
        }

        _length = _buffer.Length;
        _position = _appendMode ? _length : 0;
    }

    /// <summary>Gets the path the stream was opened with.</summary>
    public string Name => _path;

    /// <inheritdoc/>
    public override bool CanRead => _canRead && !_closed;

    /// <inheritdoc/>
    public override bool CanSeek => !_closed;

    /// <inheritdoc/>
    public override bool CanWrite => _canWrite && !_closed;

    /// <inheritdoc/>
    public override long Length
    {
        get
        {
            ThrowIfClosed();
            return _length;
        }
    }

    /// <inheritdoc/>
    public override long Position
    {
        get
        {
            ThrowIfClosed();
            return _position;
        }
        set
        {
            ThrowIfClosed();
            if (value < 0)
                throw new ArgumentOutOfRangeException("value");
            _position = (int)value;
        }
    }

    private void ThrowIfClosed()
    {
        if (_closed)
            throw new IOException("Stream is closed");
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
            return;
        int capacity = _buffer.Length == 0 ? 64 : _buffer.Length * 2;
        if (capacity < required)
            capacity = required;
        byte[] larger = new byte[capacity];
        for (int i = 0; i < _length; i++)
            larger[i] = _buffer[i];
        _buffer = larger;
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ThrowIfClosed();
        if (!_canRead)
            throw new NotSupportedException("Stream does not support reading");
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
        ThrowIfClosed();
        if (!_canWrite)
            throw new NotSupportedException("Stream does not support writing");
        if (buffer == null)
            throw new ArgumentNullException("buffer");
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException("offset");
        if (count == 0)
            return;

        // Append mode always writes at the end regardless of Position,
        // matching FileMode.Append semantics.
        if (_appendMode)
            _position = _length;

        // Writing past the end zero-fills the gap.
        if (_position > _length)
        {
            EnsureCapacity(_position);
            for (int i = _length; i < _position; i++)
                _buffer[i] = 0;
            if (_position > _length)
                _length = _position;
        }

        EnsureCapacity(_position + count);
        for (int i = 0; i < count; i++)
            _buffer[_position + i] = buffer[offset + i];
        _position += count;
        if (_position > _length)
            _length = _position;
        _dirty = true;
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfClosed();
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
        ThrowIfClosed();
        if (!_canWrite)
            throw new NotSupportedException("Stream does not support writing");
        if (value < 0)
            throw new ArgumentOutOfRangeException("value");
        int newLength = (int)value;
        if (newLength > _length)
        {
            EnsureCapacity(newLength);
            for (int i = _length; i < newLength; i++)
                _buffer[i] = 0;
        }
        _length = newLength;
        if (_position > newLength)
            _position = newLength;
        _dirty = true;
    }

    /// <summary>
    /// Writes the buffered contents back to the file when there are
    /// pending changes (truncate-and-rewrite - see the file header).
    /// </summary>
    public override void Flush()
    {
        ThrowIfClosed();
        if (!_canWrite || !_dirty)
            return;

        byte[] content = _buffer;
        if (_length != content.Length)
        {
            content = new byte[_length];
            for (int i = 0; i < _length; i++)
                content[i] = _buffer[i];
        }
        File.WriteAllBytes(_path, content);
        _dirty = false;
    }

    /// <summary>Flushes to the bridge (the flushToDisk flag is ignored; the bridge is synchronous).</summary>
    public virtual void Flush(bool flushToDisk) => Flush();

    /// <inheritdoc/>
    public override void Close()
    {
        if (_closed)
            return;
        Flush();
        _closed = true;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_closed)
            Close();
        base.Dispose(disposing);
    }
}
