// NeutrinoOS korlib - System.IO.StringReader (Phase 5)
//
// A TextReader over an in-memory string. This is the standard BCL
// StringReader subset; NeutrinoOS Phase 5 adds it as the input side of
// the shell's pipe implementation (Console.SetIn(new StringReader(...)))
// and for utility-application tests.

namespace System.IO;

/// <summary>
/// A TextReader that reads from a string. Line endings are \n or \r\n
/// (both consumed as a single newline, matching the rest of korlib).
/// </summary>
public class StringReader : TextReader
{
    private readonly string _s;
    private int _pos;

    /// <summary>Creates a reader over the given string (null is treated as empty).</summary>
    public StringReader(string s)
    {
        _s = s ?? "";
    }

    private bool AtEnd => _pos >= _s.Length;

    /// <summary>Peeks at the next character, or -1 at end of input.</summary>
    public override int Peek()
    {
        return AtEnd ? -1 : _s[_pos];
    }

    /// <summary>Reads the next character, or -1 at end of input.</summary>
    public override int Read()
    {
        return AtEnd ? -1 : _s[_pos++];
    }

    /// <summary>
    /// Reads the next line (without the terminator), or null at end of
    /// input. \r\n is treated as a single terminator.
    /// </summary>
    public override string? ReadLine()
    {
        if (AtEnd)
            return null;

        int start = _pos;
        while (_pos < _s.Length && _s[_pos] != '\n' && _s[_pos] != '\r')
            _pos++;

        string line = _s.Substring(start, _pos - start);

        // Consume the terminator: "\r\n" counts as a single newline.
        if (_pos < _s.Length && _s[_pos] == '\r')
        {
            _pos++;
            if (_pos < _s.Length && _s[_pos] == '\n')
                _pos++;
        }
        else if (_pos < _s.Length && _s[_pos] == '\n')
        {
            _pos++;
        }

        return line;
    }

    /// <summary>Reads all remaining characters and returns them.</summary>
    public override string ReadToEnd()
    {
        if (AtEnd)
            return "";
        string rest = _s.Substring(_pos, _s.Length - _pos);
        _pos = _s.Length;
        return rest;
    }

    /// <summary>Returns the underlying string (not the remaining input).</summary>
    public override string ToString() => _s;
}
