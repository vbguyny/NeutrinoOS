// NeutrinoOS korlib - System.IO.StringWriter (Phase 5)
//
// A TextWriter that buffers output into a StringBuilder. This is the
// standard BCL StringWriter subset; NeutrinoOS Phase 5 adds it because
// the shell needs an in-memory capture target for pipes (cmd1 | cmd2)
// and applications/tests can use it to capture console output via
// Console.SetOut.

using System.Text;

namespace System.IO;

/// <summary>
/// A TextWriter that writes its output to a <see cref="StringBuilder"/>.
/// Used by the shell's pipe implementation: the left command runs with
/// <c>Console.SetOut(new StringWriter())</c>, and the captured text is
/// then fed to the right command through <c>Console.SetIn(new StringReader(...))</c>.
/// </summary>
public class StringWriter : TextWriter
{
    private readonly StringBuilder _sb;

    /// <summary>Creates a writer over a new StringBuilder.</summary>
    public StringWriter()
    {
        _sb = new StringBuilder();
    }

    /// <summary>Creates a writer over the given builder.</summary>
    public StringWriter(StringBuilder sb)
    {
        _sb = sb ?? new StringBuilder();
    }

    /// <summary>Returns the underlying StringBuilder.</summary>
    public StringBuilder GetStringBuilder() => _sb;

    /// <summary>Writes a single character to the builder.</summary>
    public override void Write(char value)
    {
        _sb.Append(value);
    }

    /// <summary>Writes a string to the builder.</summary>
    public override void Write(string? value)
    {
        if (value != null)
            _sb.Append(value);
    }

    /// <summary>Writes a character array to the builder.</summary>
    public override void Write(char[]? buffer)
    {
        if (buffer != null)
            _sb.Append(buffer, 0, buffer.Length);
    }

    /// <summary>Writes a character range to the builder.</summary>
    public override void Write(char[] buffer, int index, int count)
    {
        _sb.Append(buffer, index, count);
    }

    /// <summary>No-op: the builder is always complete.</summary>
    public override void Flush()
    {
    }

    /// <summary>Returns the accumulated text.</summary>
    public override string ToString() => _sb.ToString();
}
