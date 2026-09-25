// NeutrinoOS kernel - serial console device (/dev/ttyS0)
//
// IConsoleDevice implementation over the UART 16550 driver. Handles
// UTF-8 encoding, LF->CRLF translation, ANSI SGR color output, a shadow
// cursor (the serial terminal cannot report cursor position), screen
// clearing, and the prompt-tail tracking used by line redraws.

using System;
using ProtonOS.Arch;

namespace ProtonOS.Platform;

/// <summary>
/// Console device backed by the COM1 UART (registered as /dev/ttyS0).
/// </summary>
public sealed class SerialConsoleDevice : IConsoleDevice
{
    /// <summary>The device path this console is registered under.</summary>
    public const string DevicePath = "/dev/ttyS0";

    private const int PromptTailCapacity = 64;

    private readonly char[] _promptTail = new char[PromptTailCapacity];
    private int _promptTailLength;

    private int _cursorLeft;
    private int _cursorTop;

    private int _foreground = -1;   // -1 = terminal default
    private int _background = -1;
    private bool _lastWasCr;

    /// <summary>Human-readable device name ("ttyS0").</summary>
    public string Name => "ttyS0";

    /// <summary>The device path ("/dev/ttyS0").</summary>
    public string Path => DevicePath;

    // ==================== Output ====================

    /// <summary>Writes a single character, encoding non-ASCII as UTF-8.</summary>
    public void Write(char c)
    {
        if (c < 0x80)
        {
            WriteAscii((byte)c);
            return;
        }

        // UTF-8 encode (BMP only; surrogate halves are encoded individually
        // in Phase 2 - document as a deviation).
        // 2-byte form: 110xxxxx 10xxxxxx
        if (c < 0x800)
        {
            Uart16550.WriteByte((byte)(0xC0 | (c >> 6)));
            Uart16550.WriteByte((byte)(0x80 | (c & 0x3F)));
            TrackChar(c);
            return;
        }

        // 3-byte form: 1110xxxx 10xxxxxx 10xxxxxx
        Uart16550.WriteByte((byte)(0xE0 | (c >> 12)));
        Uart16550.WriteByte((byte)(0x80 | ((c >> 6) & 0x3F)));
        Uart16550.WriteByte((byte)(0x80 | (c & 0x3F)));
        TrackChar(c);
    }

    private void WriteAscii(byte b)
    {
        if (b == 0x0A && !_lastWasCr)
        {
            Uart16550.WriteByte(0x0D);  // CR before LF for serial terminals
            TrackChar('\r');
        }

        Uart16550.WriteByte(b);
        _lastWasCr = b == 0x0D;

        // Shadow-cursor bookkeeping
        switch (b)
        {
            case 0x0D: _cursorLeft = 0; break;
            case 0x0A: _cursorLeft = 0; _cursorTop++; break;
            case 0x08: if (_cursorLeft > 0) _cursorLeft--; break;
            default:
                if (b >= 0x20)
                    _cursorLeft++;
                break;
        }

        TrackChar((char)b);
    }

    private void TrackChar(char c)
    {
        // The prompt tail is the text since the last newline: used by the
        // line discipline to redraw the current line (history recall, ^U).
        if (c == '\n')
        {
            _promptTailLength = 0;
            return;
        }
        if (c == '\r')
            return;

        if (_promptTailLength >= PromptTailCapacity)
        {
            // Keep the most recent characters (drop oldest)
            for (int i = 1; i < PromptTailCapacity; i++)
                _promptTail[i - 1] = _promptTail[i];
            _promptTail[PromptTailCapacity - 1] = c;
        }
        else
        {
            _promptTail[_promptTailLength++] = c;
        }
    }

    /// <summary>Writes a span of characters.</summary>
    public void Write(ReadOnlySpan<char> s)
    {
        for (int i = 0; i < s.Length; i++)
            Write(s[i]);
    }

    /// <summary>Gets the text since the last newline (the current prompt).</summary>
    public int GetPromptTail(Span<char> destination)
    {
        int count = _promptTailLength;
        if (count > destination.Length)
            count = destination.Length;
        for (int i = 0; i < count; i++)
            destination[i] = _promptTail[i];
        return count;
    }

    // ==================== Input ====================

    /// <summary>Whether a decoded key press is waiting.</summary>
    public bool KeyAvailable => LineDiscipline.KeyAvailable;

    /// <summary>Attempts to dequeue a decoded key press.</summary>
    public bool TryReadKey(out System.ConsoleKeyInfo key)
        => LineDiscipline.TryDequeueKey(out key);

    /// <summary>Blocks until a key press is available.</summary>
    public System.ConsoleKeyInfo ReadKey(bool intercept)
    {
        if (LineDiscipline.TryDequeueKey(out var key))
            return key;

        LineDiscipline.BeginKeyRead(echo: !intercept);
        try
        {
            while (true)
            {
                if (LineDiscipline.TryDequeueKey(out key))
                    return key;
                CPU.Halt();
                LineDiscipline.PollTimeouts();
            }
        }
        finally
        {
            LineDiscipline.EndKeyRead();
        }
    }

    /// <summary>Reads a line through the line discipline.</summary>
    public int ReadLine(Span<char> destination)
    {
        Flush();
        return LineDiscipline.ReadLine(destination);
    }

    /// <summary>Switches the line discipline mode.</summary>
    public void SetRawMode(bool rawMode)
        => LineDiscipline.SetRawMode(rawMode);

    // ==================== Screen ====================

    /// <summary>Clears the screen and homes the cursor (ESC[2J ESC[H).</summary>
    public void Clear()
    {
        ReadOnlySpan<char> seq = "\x1b[2J\x1b[H".AsSpan();
        Write(seq);
        _cursorLeft = 0;
        _cursorTop = 0;
        // The prompt tail is invalidated by the clear
        _promptTailLength = 0;
    }

    /// <summary>Sets the cursor position (ESC[row;colH, 1-based).</summary>
    public void SetCursorPosition(int left, int top)
    {
        if (left < 0) left = 0;
        if (top < 0) top = 0;

        Write('\x1b');
        Write('[');
        WriteDecimal(top + 1);
        Write(';');
        WriteDecimal(left + 1);
        Write('H');

        _cursorLeft = left;
        _cursorTop = top;
    }

    /// <summary>Gets the shadow cursor position.</summary>
    public void GetCursorPosition(out int left, out int top)
    {
        left = _cursorLeft;
        top = _cursorTop;
    }

    private void WriteDecimal(int value)
    {
        if (value >= 10)
            WriteDecimal(value / 10);
        Write((char)('0' + (value % 10)));
    }

    // ==================== Colors ====================

    /// <summary>
    /// Sets foreground/background colors as ANSI SGR sequences.
    /// -1 restores the terminal defaults (ESC[0m). A leading reset (0;) is
    /// included so earlier attributes cannot leak between color changes.
    /// The palette uses the standard console-index mapping (e.g. DarkRed=4
    /// -> SGR 31, Red=12 -> SGR 91); see <see cref="FgSgrCode"/>.
    /// </summary>
    public void SetColors(int foreground, int background)
    {
        if (foreground == _foreground && background == _background)
            return;

        _foreground = foreground;
        _background = background;

        if (foreground < 0 && background < 0)
        {
            WriteAnsi("0m");
            return;
        }

        Write('\x1b');
        Write('[');
        Write('0');     // reset first so earlier attributes do not leak

        if (foreground >= 0)
        {
            Write(';');
            WriteDecimal(FgSgrCode(foreground));
        }

        if (background >= 0)
        {
            Write(';');
            WriteDecimal(FgSgrCode(background) + 10);   // 30-37 -> 40-47, 90-97 -> 100-107
        }

        Write('m');
    }

    /// <summary>
    /// Maps a ConsoleColor index (0-15) to its ANSI SGR foreground code
    /// (dark colors 30-37, bright colors 90-97). The console palette is not
    /// in ANSI order - DarkBlue=1 is SGR 34 while DarkRed=4 is SGR 31 - so
    /// this is an explicit table rather than an offset.
    /// </summary>
    private static int FgSgrCode(int color)
    {
        switch (color & 0x0F)
        {
            case 0: return 30;   // black
            case 1: return 34;   // dark blue
            case 2: return 32;   // dark green
            case 3: return 36;   // dark cyan
            case 4: return 31;   // dark red
            case 5: return 35;   // dark magenta
            case 6: return 33;   // dark yellow
            case 7: return 37;   // gray
            case 8: return 90;   // dark gray
            case 9: return 94;   // blue
            case 10: return 92;  // green
            case 11: return 96;  // cyan
            case 12: return 91;  // red
            case 13: return 95;  // magenta
            case 14: return 93;  // yellow
            default: return 97;  // white
        }
    }

    private void WriteAnsi(string tail)
    {
        Write('\x1b');
        Write('[');
        for (int i = 0; i < tail.Length; i++)
            Write(tail[i]);
    }

    /// <summary>Flushes output (UART writes are synchronous; nothing buffered).</summary>
    public void Flush()
    {
        // The UART TX ring drains via interrupts/polling; writes are
        // effectively synchronous from the caller's perspective.
    }

    // ==================== Configuration ====================

    /// <summary>The serial console is never redirected in Phase 2.</summary>
    public bool IsInputRedirected => false;

    /// <summary>The serial console is never redirected in Phase 2.</summary>
    public bool IsOutputRedirected => false;

    /// <summary>Configured console width (default 80).</summary>
    public int WindowWidth => ConsoleAbstractionLayer.DefaultWidth;

    /// <summary>Configured console height (default 50).</summary>
    public int WindowHeight => ConsoleAbstractionLayer.DefaultHeight;
}
