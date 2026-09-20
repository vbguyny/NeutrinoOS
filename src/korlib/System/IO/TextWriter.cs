// NeutrinoOS korlib - System.IO.TextWriter
//
// Phase 2: minimal TextWriter base class used by Console.Out / Console.Error
// and by console applications. Buffering behavior is implemented by the
// internal Console writer (flush on newline, on Flush(), or when the 512
// character buffer is full / about to be reused); the base class itself is
// unbuffered like the official BCL.

namespace System.IO;

/// <summary>
/// A writer for a sequence of characters. NeutrinoOS implements the
/// commonly used subset: the Write/WriteLine overloads listed below and
/// Flush. Other BCL members (encoding configuration, async APIs) are not
/// implemented in Phase 2.
/// </summary>
public abstract class TextWriter : IDisposable
{
    /// <summary>The newline string NeutrinoOS uses ("\n"; the serial driver translates to CRLF).</summary>
    public static string CoreNewLine => "\n";

    /// <summary>
    /// A synchronized wrapper around a TextWriter. NeutrinoOS has no
    /// preemptive protection guarantees around arbitrary writers, so this
    /// simply returns the same instance (single-threaded console in Phase 2).
    /// </summary>
    public static TextWriter Synchronized(TextWriter writer)
    {
        return writer;
    }

    /// <summary>
    /// Gets or sets the line terminator string. NeutrinoOS Phase 2 always
    /// writes "\n" (the console device translates it to CRLF); the setter
    /// is accepted but ignored, matching the fixed serial line discipline.
    /// </summary>
    public virtual string NewLine
    {
        get => "\n";
        set { }
    }

    /// <summary>Gets the character encoding in which the output is written.</summary>
    public virtual Text.Encoding Encoding => Text.Encoding.UTF8;

    /// <summary>Gets the string used for formatting (not used in Phase 2).</summary>
    public virtual IFormatProvider? FormatProvider => null;

    /// <summary>Writes a single character.</summary>
    public abstract void Write(char value);

    /// <summary>Writes a string.</summary>
    public virtual void Write(string? value)
    {
        if (value == null) return;
        for (int i = 0; i < value.Length; i++)
            Write(value[i]);
    }

    /// <summary>Writes a character array.</summary>
    public virtual void Write(char[]? buffer)
    {
        if (buffer == null) return;
        for (int i = 0; i < buffer.Length; i++)
            Write(buffer[i]);
    }

    /// <summary>Writes a range of the character array.</summary>
    public virtual void Write(char[] buffer, int index, int count)
    {
        for (int i = index; i < index + count; i++)
            Write(buffer[i]);
    }

    /// <summary>Writes the text representation of a boolean.</summary>
    public virtual void Write(bool value) => Write(value ? "True" : "False");

    /// <summary>Writes the text representation of a 32-bit signed integer.</summary>
    public virtual void Write(int value) => Write(value.ToString());

    /// <summary>Writes the text representation of an object.</summary>
    public virtual void Write(object? value)
    {
        if (value == null) return;
        Write(value.ToString());
    }

    /// <summary>Writes the line terminator.</summary>
    public virtual void WriteLine() => Write('\n');

    /// <summary>Writes a character followed by the line terminator.</summary>
    public virtual void WriteLine(char value)
    {
        Write(value);
        Write('\n');
    }

    /// <summary>Writes a string followed by the line terminator.</summary>
    public virtual void WriteLine(string? value)
    {
        if (value != null)
        {
            for (int i = 0; i < value.Length; i++)
                Write(value[i]);
        }
        Write('\n');
    }

    /// <summary>Writes an object followed by the line terminator.</summary>
    public virtual void WriteLine(object? value)
    {
        Write(value);
        Write('\n');
    }

    /// <summary>Clears all buffers.</summary>
    public abstract void Flush();

    /// <summary>Closes the writer and releases resources (flush only in Phase 2).</summary>
    public virtual void Close() => Flush();

    /// <summary>Disposes the writer (flush only in Phase 2).</summary>
    public virtual void Dispose() => Flush();
}
