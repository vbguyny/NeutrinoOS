// NeutrinoOS kernel - VGA text console device (/dev/vga0) (Phase 3)
//
// IConsoleDevice implementation over VgaTextDriver. Interprets the ANSI
// subset the console stack emits (SGR colors, clear, cursor addressing,
// line erase, relative moves, save/restore), tracks the prompt tail for
// line redraws, and maps console colors to VGA attribute bytes.
//
// Input: the VGA console shares the global LineDiscipline with the
// serial console. The PS/2 keyboard driver synthesizes the same ANSI
// byte sequences a serial terminal would send (printable ASCII, ESC[A
// for arrows, 0x7F for backspace, 0x03/0x04 for Ctrl+C/D, ...) and
// feeds them into the discipline, so editing, history, ReadKey and raw
// mode behave identically on both consoles by construction.

using System;
using ProtonOS.X64;

namespace ProtonOS.Platform;

/// <summary>
/// Console device backed by the VGA text framebuffer (registered as /dev/vga0).
/// </summary>
public sealed class VgaConsoleDevice : IConsoleDevice
{
    /// <summary>The device path this console is registered under.</summary>
    public const string DevicePath = "/dev/vga0";

    private const int PromptTailCapacity = 64;

    private readonly char[] _promptTail = new char[PromptTailCapacity];
    private int _promptTailLength;

    private int _foreground = -1;
    private int _background = -1;

    // ANSI parser state: 0 = ground, 1 = after ESC, 2 = inside CSI.
    private int _escState;
    private int _param0, _param1, _param2, _param3;
    private int _paramCount;        // number of completed params
    private bool _inParam;          // currently accumulating a param
    private bool _paramSeen;        // explicit param seen for the current slot
    private bool _privateMarker;    // '?' / '>' / '<' prefix seen (ignored)

    private int _savedX, _savedY;

    /// <summary>Human-readable device name ("vga0").</summary>
    public string Name => "vga0";

    /// <summary>The device path ("/dev/vga0").</summary>
    public string Path => DevicePath;

    /// <summary>Whether the 80x50 text mode is active.</summary>
    public bool IsMode80x50 => VgaTextDriver.IsMode80x50;

    // ==================== Initialization ====================

    /// <summary>
    /// Initializes the underlying VGA driver in 80x25 (false) or 80x50
    /// (true) mode and clears the screen.
    /// </summary>
    public void Initialize(bool mode80x50)
    {
        VgaTextDriver.Initialize(mode80x50);
        _promptTailLength = 0;
        _foreground = -1;
        _background = -1;
    }

    // ==================== Output ====================

    /// <summary>
    /// Writes one character, interpreting the supported ANSI escape
    /// sequences. Printable characters are written as CP437 cells; the
    /// Unicode box-drawing block is mapped to the CP437 glyph codes.
    /// </summary>
    public void Write(char c)
    {
        switch (_escState)
        {
            case 1:
                if (c == '[')
                {
                    ResetParams();
                    _escState = 2;
                }
                else
                {
                    _escState = 0;      // single-char escape: ignored
                }
                return;

            case 2:
                ParseCsi(c);
                return;
        }

        if (c == '\x1b')
        {
            _escState = 1;
            return;
        }

        switch (c)
        {
            case '\n':
                VgaTextDriver.WriteRawByte(0x0A);
                TrackChar(c);
                return;
            case '\r':
                VgaTextDriver.WriteRawByte(0x0D);
                TrackChar(c);
                return;
            case '\b':
                VgaTextDriver.WriteRawByte(0x08);
                TrackChar(c);
                return;
            case '\t':
                WriteTab();
                return;
            case '\0':
                return;
        }

        if (c < 0x20 || c == 0x7F)
            return;     // other control chars: swallowed

        byte b = (c < 0x100) ? (byte)c : UnicodeToCp437(c);
        VgaTextDriver.WriteRawByte(b);
        TrackChar(c);
    }

    private void WriteTab()
    {
        int column = VgaTextDriver.CursorX;
        int spaces = 8 - (column & 7);
        for (int i = 0; i < spaces; i++)
            VgaTextDriver.WriteRawByte(0x20);
    }

    /// <summary>Maps common Unicode box-drawing/punctuation chars to CP437 codes.</summary>
    private static byte UnicodeToCp437(char c)
    {
        switch (c)
        {
            case '\u2500': return 0xC4;   // horizontal
            case '\u2502': return 0xB3;   // vertical
            case '\u250C': return 0xDA;   // top-left
            case '\u2510': return 0xBF;   // top-right
            case '\u2514': return 0xC0;   // bottom-left
            case '\u2518': return 0xD9;   // bottom-right
            case '\u251C': return 0xC3;   // tee right
            case '\u2524': return 0xB4;   // tee left
            case '\u252C': return 0xC2;   // tee down
            case '\u2534': return 0xC1;   // tee up
            case '\u253C': return 0xC5;   // cross
            case '\u2550': return 0xCD;   // double horizontal
            case '\u2551': return 0xBA;   // double vertical
            case '\u2554': return 0xC9;   // double top-left
            case '\u2557': return 0xBB;   // double top-right
            case '\u255A': return 0xC8;   // double bottom-left
            case '\u255D': return 0xBC;   // double bottom-right
            case '\u2560': return 0xCC;   // double tee right
            case '\u2563': return 0xB9;   // double tee left
            case '\u2566': return 0xCB;   // double tee down
            case '\u2569': return 0xCA;   // double tee up
            case '\u256C': return 0xCE;   // double cross
            case '\u00B0': return 0xF8;   // degree
            case '\u00B1': return 0xF1;   // plus-minus
            case '\u00B7': return 0xFA;   // middle dot
            case '\u00A0': return 0x20;   // non-breaking space
            default: return (byte)'?';
        }
    }

    /// <summary>Writes a span of characters.</summary>
    public void Write(ReadOnlySpan<char> s)
    {
        for (int i = 0; i < s.Length; i++)
            Write(s[i]);
    }

    /// <summary>
    /// Raw byte echo entry used by the line discipline (no ANSI parsing).
    /// Called from interrupt context, so it must not block; VGA writes are
    /// plain framebuffer memory stores.
    /// </summary>
    public void EchoRawByte(byte b)
    {
        VgaTextDriver.WriteRawByte(b);
        TrackChar((char)b);
    }

    /// <summary>
    /// Raw byte entry used by the early boot-log mirror: same as
    /// <see cref="EchoRawByte"/> but without the per-character hardware
    /// cursor update (see <see cref="VgaTextDriver.WriteRawMirrorByte"/>),
    /// so mirroring hundreds of thousands of boot-log bytes stays cheap.
    /// </summary>
    public void MirrorRawByte(byte b)
    {
        VgaTextDriver.WriteRawMirrorByte(b);
        TrackChar((char)b);
    }

    private void TrackChar(char c)
    {
        // Prompt tail: text since the last newline (line redraw support).
        if (c == '\n')
        {
            _promptTailLength = 0;
            return;
        }
        if (c == '\r')
            return;

        if (_promptTailLength >= PromptTailCapacity)
        {
            for (int i = 1; i < PromptTailCapacity; i++)
                _promptTail[i - 1] = _promptTail[i];
            _promptTail[PromptTailCapacity - 1] = c;
        }
        else
        {
            _promptTail[_promptTailLength++] = c;
        }
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

    // ==================== ANSI CSI parsing ====================

    private void ResetParams()
    {
        _param0 = 0; _param1 = 0; _param2 = 0; _param3 = 0;
        _paramCount = 0;
        _inParam = false;
        _paramSeen = false;
        _privateMarker = false;
    }

    private void PushParam()
    {
        int value = _inParam ? CurrentParam() : 0;
        switch (_paramCount)
        {
            case 0: _param0 = value; break;
            case 1: _param1 = value; break;
            case 2: _param2 = value; break;
            case 3: _param3 = value; break;
            default: break;     // extra params ignored
        }
        if (_paramCount < 4)
            _paramCount++;
        _inParam = false;
        _paramSeen = true;
    }

    private int CurrentParam()
    {
        // The param being accumulated is held in the next slot after the
        // completed ones (kept in _param{count}).
        switch (_paramCount)
        {
            case 0: return _param0;
            case 1: return _param1;
            case 2: return _param2;
            default: return _param3;
        }
    }

    private void AccumulateDigit(int digit)
    {
        int value = CurrentParam();
        value = value * 10 + digit;
        switch (_paramCount)
        {
            case 0: _param0 = value; break;
            case 1: _param1 = value; break;
            case 2: _param2 = value; break;
            default: _param3 = value; break;
        }
        _inParam = true;
    }

    private void ParseCsi(char c)
    {
        if (c >= '0' && c <= '9')
        {
            AccumulateDigit(c - '0');
            return;
        }

        if (c == ';')
        {
            PushParam();
            return;
        }

        if (c == '?' || c == '>' || c == '<' || c == '=')
        {
            _privateMarker = true;
            return;
        }

        if (c >= 0x40 && c <= 0x7E)
        {
            // Final byte: complete the pending param, dispatch, end sequence.
            if (_inParam || _paramCount == 0)
                PushParam();
            DispatchCsi(c);
            _escState = 0;
            return;
        }

        // Intermediate byte (0x20-0x2F): ignored; anything else aborts.
        if (c < 0x20 || c > 0x2F)
            _escState = 0;
    }

    private int Param(int index, int defaultValue)
    {
        int value;
        switch (index)
        {
            case 0: value = _param0; break;
            case 1: value = _param1; break;
            case 2: value = _param2; break;
            default: value = _param3; break;
        }
        return value == 0 ? defaultValue : value;
    }

    private void DispatchCsi(char final)
    {
        switch (final)
        {
            case 'H':
            case 'f':
            {
                int row = Param(0, 1);
                int col = Param(1, 1);
                VgaTextDriver.MoveTo(col - 1, row - 1);
                return;
            }
            case 'A':
                VgaTextDriver.MoveRelative(0, -Param(0, 1));
                return;
            case 'B':
                VgaTextDriver.MoveRelative(0, Param(0, 1));
                return;
            case 'C':
                VgaTextDriver.MoveRelative(Param(0, 1), 0);
                return;
            case 'D':
                VgaTextDriver.MoveRelative(-Param(0, 1), 0);
                return;
            case 'G':
                VgaTextDriver.MoveTo(Param(0, 1) - 1, VgaTextDriver.CursorY);
                return;
            case 'd':
                VgaTextDriver.MoveTo(VgaTextDriver.CursorX, Param(0, 1) - 1);
                return;
            case 'J':
                EraseInDisplay(_paramCount > 0 ? _param0 : 0);
                return;
            case 'K':
                EraseInLine(_paramCount > 0 ? _param0 : 0);
                return;
            case 'm':
                ApplySgr();
                return;
            case 's':
                _savedX = VgaTextDriver.CursorX;
                _savedY = VgaTextDriver.CursorY;
                return;
            case 'u':
                VgaTextDriver.MoveTo(_savedX, _savedY);
                return;
            default:
                // 'h'/'l' (modes), 'r' (scroll region), 'n', 'c', ... ignored.
                return;
        }
    }

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0: VgaTextDriver.ClearToEndOfScreen(); return;
            case 1: VgaTextDriver.ClearToStartOfScreen(); return;
            case 2:
                VgaTextDriver.Clear();
                _promptTailLength = 0;
                return;
            default: return;
        }
    }

    private void EraseInLine(int mode)
    {
        switch (mode)
        {
            case 0: VgaTextDriver.ClearToEndOfLine(); return;
            case 1: VgaTextDriver.ClearToStartOfLine(); return;
            case 2:
            {
                int x = VgaTextDriver.CursorX;
                int y = VgaTextDriver.CursorY;
                VgaTextDriver.MoveTo(0, y);
                VgaTextDriver.ClearToEndOfLine();
                VgaTextDriver.MoveTo(x, y);
                return;
            }
            default: return;
        }
    }

    private void ApplySgr()
    {
        if (_paramCount == 0)
        {
            ResetColorsInternal();
            return;
        }

        for (int i = 0; i < _paramCount; i++)
        {
            int code;
            switch (i)
            {
                case 0: code = _param0; break;
                case 1: code = _param1; break;
                case 2: code = _param2; break;
                default: code = _param3; break;
            }

            switch (code)
            {
                case 0:
                    ResetColorsInternal();
                    break;
                case 1:
                {
                    // Bold / bright foreground: set attribute bit 3.
                    byte attr = VgaTextDriver.Attribute;
                    VgaTextDriver.SetAttribute((byte)(attr | 0x08));
                    break;
                }
                case 7:
                {
                    // Reverse video: swap foreground and background nibbles.
                    byte attr = VgaTextDriver.Attribute;
                    int fg = (attr >> 4) & 0x07;
                    int bg = attr & 0x0F;
                    VgaTextDriver.SetAttribute((byte)(fg | (bg << 4)));
                    break;
                }
                case 22:
                    VgaTextDriver.SetAttribute((byte)(VgaTextDriver.Attribute & unchecked((byte)~0x08)));
                    break;
                case 39:
                    ApplyForeground(7);
                    break;
                case 49:
                    ApplyBackground(0);
                    break;
                default:
                    if (code >= 30 && code <= 37)
                        ApplyForeground(code - 30);
                    else if (code >= 40 && code <= 47)
                        ApplyBackground(code - 40);
                    else if (code >= 90 && code <= 97)
                        ApplyForeground((code - 90) | 0x08);
                    else if (code >= 100 && code <= 107)
                        ApplyBackground((code - 100) | 0x08);
                    break;
            }
        }
    }

    private void ApplyForeground(int color)
    {
        _foreground = color;
        _background = _background < 0 ? 0 : _background;
        VgaTextDriver.SetAttribute(VgaTextDriver.ComposeAttribute(_foreground, _background));
    }

    private void ApplyBackground(int color)
    {
        _background = color;
        _foreground = _foreground < 0 ? 7 : _foreground;
        VgaTextDriver.SetAttribute(VgaTextDriver.ComposeAttribute(_foreground, _background));
    }

    private void ResetColorsInternal()
    {
        _foreground = -1;
        _background = -1;
        VgaTextDriver.ResetAttribute();
    }

    // ==================== Input ====================

    /// <summary>Whether a decoded key press is waiting (shared discipline).</summary>
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

    /// <summary>Reads a line through the shared line discipline.</summary>
    public int ReadLine(Span<char> destination)
    {
        Flush();
        return LineDiscipline.ReadLine(destination);
    }

    /// <summary>Switches the line discipline mode.</summary>
    public void SetRawMode(bool rawMode)
        => LineDiscipline.SetRawMode(rawMode);

    // ==================== Screen ====================

    /// <summary>Clears the screen and homes the cursor.</summary>
    public void Clear()
    {
        VgaTextDriver.Clear();
        _promptTailLength = 0;
    }

    /// <summary>Sets the hardware cursor position.</summary>
    public void SetCursorPosition(int left, int top)
    {
        VgaTextDriver.MoveTo(left, top);
    }

    /// <summary>Gets the current cursor position.</summary>
    public void GetCursorPosition(out int left, out int top)
    {
        left = VgaTextDriver.CursorX;
        top = VgaTextDriver.CursorY;
    }

    /// <summary>Flushes output (no-op: framebuffer writes are immediate).</summary>
    public void Flush()
    {
    }

    /// <summary>VGA console input is never redirected.</summary>
    public bool IsInputRedirected => false;

    /// <summary>VGA console output is never redirected.</summary>
    public bool IsOutputRedirected => false;

    /// <summary>Console width in columns (80).</summary>
    public int WindowWidth => VgaTextDriver.Columns;

    /// <summary>Console height in rows (25, or 50 in 80x50 mode).</summary>
    public int WindowHeight => VgaTextDriver.Rows;

    // ==================== Colors ====================

    /// <summary>
    /// Sets foreground/background console colors (0-15) by composing a
    /// VGA attribute byte. -1 restores the defaults (light grey on black).
    /// Backgrounds above 7 switch off attribute blink so the bright
    /// background bit is usable (classic DOS behavior).
    /// </summary>
    public void SetColors(int foreground, int background)
    {
        if (foreground == _foreground && background == _background)
            return;

        _foreground = foreground;
        _background = background;

        if (foreground < 0 && background < 0)
        {
            VgaTextDriver.ResetAttribute();
            return;
        }

        VgaTextDriver.SetAttribute(VgaTextDriver.ComposeAttribute(foreground, background));
    }
}
