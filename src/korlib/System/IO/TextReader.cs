// NeutrinoOS korlib - System.IO.TextReader
//
// Phase 2: minimal TextReader base class used by Console.In and by
// console applications. Reading is always through the console line
// discipline (or the raw byte path for Read()).

using System.Text;

namespace System.IO;

/// <summary>
/// A reader for a sequence of characters. NeutrinoOS implements the
/// commonly used subset: Read(), ReadLine() and ReadToEnd().
/// </summary>
public abstract class TextReader : IDisposable
{
    /// <summary>
    /// A synchronized wrapper around a TextReader (single-threaded console
    /// in Phase 2, so this returns the same instance).
    /// </summary>
    public static TextReader Synchronized(TextReader reader)
    {
        return reader;
    }

    /// <summary>
    /// Reads the next character without consuming it, or -1 at end of input.
    /// Not implemented in Phase 2 (returns -1); query Console.KeyAvailable
    /// instead.
    /// </summary>
    public virtual int Peek()
    {
        return -1;
    }

    /// <summary>Reads the next character, or -1 at end of input.</summary>
    public abstract int Read();

    /// <summary>Reads the next line of characters, or null at end of input.</summary>
    public abstract string? ReadLine();

    /// <summary>Reads all remaining characters until end of input.</summary>
    public virtual string ReadToEnd()
    {
        var sb = new StringBuilder();
        int c;
        while ((c = Read()) >= 0)
            sb.Append((char)c);
        return sb.ToString();
    }

    /// <summary>Closes the reader (no-op in Phase 2).</summary>
    public virtual void Close() { }

    /// <summary>Disposes the reader (no-op in Phase 2).</summary>
    public virtual void Dispose() { }
}
