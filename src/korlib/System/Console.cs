// NeutrinoOS korlib - System.Console
//
// Phase 2: console input/output for bare-metal .NET 10 console
// applications on NeutrinoOS. All I/O is routed through the kernel's
// Console Abstraction Layer (CAL) to /dev/ttyS0 (the UART serial console).
//
// Deviations from the official .NET BCL (documented per the Phase 2 spec):
//   - Colors/cursor/clear are implemented with ANSI escape sequences
//     (SGR 30-37/90-97, 40-47/100-107; ESC[2J ESC[H; ESC[row;colH).
//   - CursorLeft/CursorTop are tracked locally (shadow cursor); the serial
//     terminal cannot report cursor position.
//   - WindowWidth/WindowHeight return the configured console size
//     (default 80x50).
//   - Console.ReadLine() returns null when the line is cancelled with
//     Ctrl+C (check Console.LastReadLineCanceled to distinguish it from
//     Ctrl+D end-of-input, which also returns null); the BCL would throw
//     OperationCanceledException instead, which is not used because AOT
//     exception unwinding is not reliable on the kernel console path.
//   - Console.Beep, Title, CursorVisible, cursor-size and window-region
//     APIs are no-ops on the serial console.
//
// The kernel bridge for JIT-compiled code: the #if KORLIB_IL stubs below
// are bound by method token to the kernel's [UnmanagedCallersOnly] console
// exports (Kernel.BuildConsoleTokenRegistry), so a JIT-compiled
// application's Console calls execute the same CAL / line-discipline path
// as the AOT kernel shell.

using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace System;

/// <summary>
/// Represents the standard input, output, and error streams for console
/// applications on NeutrinoOS. The console is the serial port (ttyS0).
/// </summary>
public static class Console
{
    // ==================== Kernel exports ====================
#if KORLIB_IL
    // IL metadata stubs. The AOT kernel provides the real implementations;
    // JIT-compiled code resolves Console methods to the AOT versions.
    private static unsafe void ConsoleWriteChars(char* chars, int count) => throw new PlatformNotSupportedException();
    private static unsafe int ConsoleReadLine(char* buffer, int capacity) => throw new PlatformNotSupportedException();
    private static unsafe int ConsoleReadKey(int blocking, int echo, char* keyChar, int* key, int* modifiers) => throw new PlatformNotSupportedException();
    private static int ConsoleKeyAvailable() => throw new PlatformNotSupportedException();
    private static int ConsoleReadChar() => throw new PlatformNotSupportedException();
    private static void ConsoleClear() => throw new PlatformNotSupportedException();
    private static void ConsoleSetCursor(int left, int top) => throw new PlatformNotSupportedException();
    private static unsafe void ConsoleGetCursor(int* left, int* top) => throw new PlatformNotSupportedException();
    private static unsafe void ConsoleGetSize(int* width, int* height) => throw new PlatformNotSupportedException();
    private static void ConsoleFlush() => throw new PlatformNotSupportedException();
    private static void ConsoleSetColors(int foreground, int background) => throw new PlatformNotSupportedException();
    private static void ConsoleSetLineMode(int rawMode) => throw new PlatformNotSupportedException();
    private static void ConsoleSetCtrlCAsInput(int asInput) => throw new PlatformNotSupportedException();
    private static unsafe void ConsoleIsRedirected(int* inputRedirected, int* outputRedirected) => throw new PlatformNotSupportedException();
    private static uint ConsoleGetTickMs() => throw new PlatformNotSupportedException();
#else
    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe void ConsoleWriteChars(char* chars, int count);

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int ConsoleReadLine(char* buffer, int capacity);

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int ConsoleReadKey(int blocking, int echo, char* keyChar, int* key, int* modifiers);

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ConsoleKeyAvailable();

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ConsoleReadChar();

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ConsoleClear();

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ConsoleSetCursor(int left, int top);

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe void ConsoleGetCursor(int* left, int* top);

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe void ConsoleGetSize(int* width, int* height);

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ConsoleFlush();

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ConsoleSetColors(int foreground, int background);

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ConsoleSetLineMode(int rawMode);

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ConsoleSetCtrlCAsInput(int asInput);

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe void ConsoleIsRedirected(int* inputRedirected, int* outputRedirected);

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint ConsoleGetTickMs();
#endif

    // ==================== Stream properties ====================

    // Phase 5: the default writers/reader are swappable so the shell can
    // implement pipes (<cmd> | <cmd>) and redirection (>, >>, <, 2>) in
    // process. All Console.Write/WriteLine/ReadLine calls funnel through
    // the _outWriter/_errorWriter/_inReader fields below, so swapping the
    // field is all that is needed - JIT-compiled applications resolve
    // their Console calls to these same AOT methods and are redirected
    // transparently.
    private static readonly ConsoleTextWriter _defaultOut = new ConsoleTextWriter();
    private static readonly ConsoleTextWriter _defaultError = new ConsoleTextWriter();
    private static readonly ConsoleTextReader _defaultIn = new ConsoleTextReader();

    private static TextWriter _outWriter = _defaultOut;
    private static TextWriter _errorWriter = _defaultError;
    private static TextReader _inReader = _defaultIn;

    private static readonly object _syncObject = new object();

    /// <summary>
    /// Gets the standard output writer (line-buffered, 512 characters,
    /// flushed on newline, on Flush, or after 50 ms of pending output).
    /// Returns the current redirection target when one is installed
    /// (<see cref="SetOut"/>).
    /// </summary>
    public static TextWriter Out => _outWriter;

    /// <summary>
    /// Gets the standard error writer. In Phase 2 it targets the same
    /// serial device as <see cref="Out"/>; the distinction is preserved at
    /// the API level for later redirection support.
    /// </summary>
    public static TextWriter Error => _errorWriter;

    /// <summary>
    /// Gets the standard input reader (delegates to the line discipline).
    /// Returns the current redirection source when one is installed
    /// (<see cref="SetIn"/>).
    /// </summary>
    public static TextReader In => _inReader;

    /// <summary>
    /// NeutrinoOS Phase 5 extension: redirects standard output to
    /// <paramref name="writer"/> (pass null to restore the console).
    /// The shell uses this to implement pipes and output redirection;
    /// the change is visible to JIT-compiled applications because their
    /// Console calls resolve to this same implementation.
    /// </summary>
    public static void SetOut(TextWriter? writer) => _outWriter = writer ?? _defaultOut;

    /// <summary>
    /// NeutrinoOS Phase 5 extension: redirects standard error to
    /// <paramref name="writer"/> (pass null to restore the console).
    /// </summary>
    public static void SetError(TextWriter? writer) => _errorWriter = writer ?? _defaultError;

    /// <summary>
    /// NeutrinoOS Phase 5 extension: redirects standard input to
    /// <paramref name="reader"/> (pass null to restore the console).
    /// </summary>
    public static void SetIn(TextReader? reader) => _inReader = reader ?? _defaultIn;

    /// <summary>
    /// NeutrinoOS Phase 5 extension: true when output/error or input has
    /// been redirected with <see cref="SetOut"/>/<see cref="SetIn"/>.
    /// </summary>
    public static bool IsRedirected =>
        !ReferenceEquals(_outWriter, _defaultOut) ||
        !ReferenceEquals(_errorWriter, _defaultError) ||
        !ReferenceEquals(_inReader, _defaultIn);

    /// <summary>Gets an object that can be used to synchronize console I/O.</summary>
    public static object SyncRoot => _syncObject;

    // ==================== Output ====================

    /// <summary>Writes a single character to the console.</summary>
    public static void Write(char value)
    {
        _outWriter.Write(value);
    }

    /// <summary>Writes the specified string to the console.</summary>
    public static void Write(string? value)
    {
        _outWriter.Write(value);
    }

    /// <summary>Writes the text representation of the object to the console.</summary>
    public static void Write(object? value)
    {
        _outWriter.Write(value);
    }

    /// <summary>Writes the text representation of the boolean to the console.</summary>
    public static void Write(bool value)
    {
        _outWriter.Write(value);
    }

    /// <summary>Writes the text representation of the integer to the console.</summary>
    public static void Write(int value)
    {
        _outWriter.Write(value);
    }

    /// <summary>Writes the text representation of the integer to the console.</summary>
    public static void Write(long value)
    {
        _outWriter.Write(value.ToString());
    }

    /// <summary>Writes the text representation of the unsigned integer to the console.</summary>
    public static void Write(uint value)
    {
        _outWriter.Write(value.ToString());
    }

    /// <summary>Writes the text representation of the unsigned integer to the console.</summary>
    public static void Write(ulong value)
    {
        _outWriter.Write(value.ToString());
    }

    /// <summary>Writes a formatted string (delegates to string.Format).</summary>
    public static void Write(string format, object? arg0)
    {
        _outWriter.Write(string.Format(format, arg0));
    }

    /// <summary>Writes a formatted string (delegates to string.Format).</summary>
    public static void Write(string format, object? arg0, object? arg1)
    {
        _outWriter.Write(string.Format(format, arg0, arg1));
    }

    /// <summary>Writes a formatted string (delegates to string.Format).</summary>
    public static void Write(string format, object? arg0, object? arg1, object? arg2)
    {
        _outWriter.Write(string.Format(format, arg0, arg1, arg2));
    }

    /// <summary>Writes the line terminator to the console.</summary>
    public static void WriteLine()
    {
        _outWriter.WriteLine();
    }

    /// <summary>Writes a character followed by the line terminator.</summary>
    public static void WriteLine(char value)
    {
        _outWriter.WriteLine(value);
    }

    /// <summary>Writes a string followed by the line terminator.</summary>
    public static void WriteLine(string? value)
    {
        _outWriter.WriteLine(value);
    }

    /// <summary>Writes an object followed by the line terminator.</summary>
    public static void WriteLine(object? value)
    {
        _outWriter.WriteLine(value);
    }

    /// <summary>Writes a boolean followed by the line terminator.</summary>
    public static void WriteLine(bool value)
    {
        _outWriter.Write(value);
        _outWriter.WriteLine();
    }

    /// <summary>Writes an integer followed by the line terminator.</summary>
    public static void WriteLine(int value)
    {
        _outWriter.Write(value);
        _outWriter.WriteLine();
    }

    /// <summary>Writes a long followed by the line terminator.</summary>
    public static void WriteLine(long value)
    {
        _outWriter.Write(value.ToString());
        _outWriter.WriteLine();
    }

    /// <summary>Writes a formatted string followed by the line terminator.</summary>
    public static void WriteLine(string format, object? arg0)
    {
        _outWriter.Write(string.Format(format, arg0));
        _outWriter.WriteLine();
    }

    /// <summary>Writes a formatted string followed by the line terminator.</summary>
    public static void WriteLine(string format, object? arg0, object? arg1)
    {
        _outWriter.Write(string.Format(format, arg0, arg1));
        _outWriter.WriteLine();
    }

    /// <summary>Writes a formatted string followed by the line terminator.</summary>
    public static void WriteLine(string format, object? arg0, object? arg1, object? arg2)
    {
        _outWriter.Write(string.Format(format, arg0, arg1, arg2));
        _outWriter.WriteLine();
    }

    /// <summary>Flushes the buffered console output to the serial device.</summary>
    public static void Flush()
    {
        _outWriter.Flush();
        _errorWriter.Flush();
    }

    // ==================== Input ====================

    /// <summary>
    /// Reads the next character from the console input, or -1 at end of
    /// input. Uses the raw byte stream (not the line editor). Honors
    /// <see cref="SetIn"/> redirection.
    /// </summary>
    public static int Read()
    {
        if (!ReferenceEquals(_inReader, (TextReader)_defaultIn))
            return _inReader.Read();

        Flush();
        return ConsoleReadChar();
    }

    /// <summary>
    /// Reads the next line from the console. Returns null at end of input
    /// (Ctrl+D on an empty line) or when the line is cancelled with Ctrl+C;
    /// check <see cref="LastReadLineCanceled"/> to distinguish the two
    /// (NeutrinoOS Phase 2 behavior - the full BCL throws
    /// OperationCanceledException from console pipelines that support it,
    /// which is not safe in the AOT kernel context). Honors
    /// <see cref="SetIn"/> redirection.
    /// </summary>
    public static string? ReadLine()
    {
        // Phase 5: redirected input (pipe / '<' file) bypasses the line
        // discipline entirely.
        if (!ReferenceEquals(_inReader, (TextReader)_defaultIn))
        {
            LastReadLineCanceled = false;
            return _inReader.ReadLine();
        }

        // Make sure any pending prompt is on the wire before waiting
        Flush();

        var sb = new StringBuilder(128);
        int cap = 256;
        char[] buffer = new char[cap];

        while (true)
        {
            int result;
            unsafe
            {
                fixed (char* p = buffer)
                {
                    result = ConsoleReadLine(p, cap);
                }
            }

            if (result >= 0)
            {
                // A chunk/full line: append and stop unless it filled the
                // buffer completely (then continue reading - extremely long
                // lines are returned in chunks).
                sb.Append(buffer, 0, result);
                if (result < cap)
                {
                    LastReadLineCanceled = false;
                    return sb.ToString();
                }
                continue;
            }

            LastReadLineCanceled = result == -2;
            return null;    // EOF or cancelled
        }
    }

    /// <summary>
    /// NeutrinoOS extension: true when the last <see cref="ReadLine()"/>
    /// returned null because the line was cancelled with Ctrl+C (false for
    /// a real end-of-input from Ctrl+D). The official BCL reports this by
    /// throwing from the underlying stream; NeutrinoOS reports it here.
    /// </summary>
    public static bool LastReadLineCanceled { get; private set; }

    /// <summary>
    /// Obtains the next key pressed by the user. The pressed key is
    /// displayed/echoed if <paramref name="intercept"/> is false (the
    /// line discipline echoes by default while reading a line; this method
    /// intercepts at the key-queue level). When input is redirected
    /// (<see cref="SetIn"/>), reads a character from the reader instead.
    /// </summary>
    public static ConsoleKeyInfo ReadKey(bool intercept = false)
    {
        if (!ReferenceEquals(_inReader, (TextReader)_defaultIn))
        {
            int ch = _inReader.Read();
            if (ch < 0)
                return new ConsoleKeyInfo('\0', 0, false, false, false);
            char c = (char)ch;
            ConsoleKey key = ConsoleKey.Enter;
            if (c >= 'a' && c <= 'z') key = (ConsoleKey)('A' + (c - 'a'));
            else if (c >= 'A' && c <= 'Z') key = (ConsoleKey)c;
            else if (c >= '0' && c <= '9') key = (ConsoleKey)c;
            else if (c != '\r' && c != '\n') key = 0;
            return new ConsoleKeyInfo(c, key, false, false, false);
        }

        Flush();
        while (true)
        {
            if (TryReadKeyInternal(intercept, out ConsoleKeyInfo key))
                return key;
        }
    }

    private static bool TryReadKeyInternal(bool intercept, out ConsoleKeyInfo key)
    {
        key = default;
        char keyChar = '\0';
        int keyCode = 0;
        int mods = 0;
        unsafe
        {
            // echo = !intercept: ReadKey(false) displays the key, ReadKey(true) does not
            int got = ConsoleReadKey(1, intercept ? 0 : 1, &keyChar, &keyCode, &mods);
            if (got == 0)
                return false;
        }
        key = new ConsoleKeyInfo(keyChar, (ConsoleKey)keyCode,
            (mods & (int)ConsoleModifiers.Shift) != 0,
            (mods & (int)ConsoleModifiers.Alt) != 0,
            (mods & (int)ConsoleModifiers.Control) != 0);
        return true;
    }

    /// <summary>
    /// Gets a value indicating whether a key press is available in the
    /// input buffer.
    /// </summary>
    public static bool KeyAvailable => ConsoleKeyAvailable() != 0;

    /// <summary>
    /// Gets or sets whether Ctrl+C is treated as ordinary input instead of
    /// cancelling the current line read.
    /// </summary>
    public static bool TreatControlCAsInput
    {
        get => _treatCtrlCAsInput;
        set
        {
            _treatCtrlCAsInput = value;
            ConsoleSetCtrlCAsInput(value ? 1 : 0);
        }
    }

    private static bool _treatCtrlCAsInput;

    // ==================== Buffer / cursor ====================

    /// <summary>Clears the console screen and homes the cursor (ESC[2J ESC[H).</summary>
    public static void Clear()
    {
        Flush();
        ConsoleClear();
    }

    /// <summary>Sets the cursor position using ANSI ESC[row;colH.</summary>
    public static void SetCursorPosition(int left, int top)
    {
        if (left < 0) left = 0;
        if (top < 0) top = 0;
        Flush();
        ConsoleSetCursor(left, top);
    }

    /// <summary>Gets or sets the column position of the cursor (shadow cursor).</summary>
    public static int CursorLeft
    {
        get
        {
            GetCursor(out int left, out _);
            return left;
        }
        set => SetCursorPosition(value, CursorTop);
    }

    /// <summary>Gets or sets the row position of the cursor (shadow cursor).</summary>
    public static int CursorTop
    {
        get
        {
            GetCursor(out _, out int top);
            return top;
        }
        set => SetCursorPosition(CursorLeft, value);
    }

    private static void GetCursor(out int left, out int top)
    {
        unsafe
        {
            int x, y;
            ConsoleGetCursor(&x, &y);
            left = x;
            top = y;
        }
    }

    private static int _windowWidth = 80;
    private static int _windowHeight = 50;
    private static bool _sizeQueried;

    /// <summary>Gets the console window width (configured size, default 80).</summary>
    public static int WindowWidth
    {
        get
        {
            QuerySize();
            return _windowWidth;
        }
    }

    /// <summary>Gets the console window height (configured size, default 50).</summary>
    public static int WindowHeight
    {
        get
        {
            QuerySize();
            return _windowHeight;
        }
    }

    private static void QuerySize()
    {
        if (_sizeQueried)
            return;
        unsafe
        {
            int w, h;
            ConsoleGetSize(&w, &h);
            if (w > 0) _windowWidth = w;
            if (h > 0) _windowHeight = h;
        }
        _sizeQueried = true;
    }

    /// <summary>Gets or sets the title of the console (no-op on serial).</summary>
    public static string Title { get; set; } = "NeutrinoOS";

    /// <summary>Cursor visibility (no-op on the serial console).</summary>
    public static bool CursorVisible { get; set; } = true;

    /// <summary>Beeps the console speaker (no-op on the serial console).</summary>
    public static void Beep() { }

    // ==================== Colors ====================

    private static ConsoleColor _foreground = ConsoleColor.Gray;
    private static ConsoleColor _background = ConsoleColor.Black;

    /// <summary>
    /// Gets or sets the foreground color (ANSI SGR 30-37 / 90-97).
    /// </summary>
    public static ConsoleColor ForegroundColor
    {
        get => _foreground;
        set
        {
            _foreground = value;
            ApplyColors();
        }
    }

    /// <summary>
    /// Gets or sets the background color (ANSI SGR 40-47 / 100-107).
    /// </summary>
    public static ConsoleColor BackgroundColor
    {
        get => _background;
        set
        {
            _background = value;
            ApplyColors();
        }
    }

    private static void ApplyColors()
    {
        Flush();
        ConsoleSetColors((int)_foreground, (int)_background);
    }

    /// <summary>Resets foreground and background to the terminal defaults (ESC[0m).</summary>
    public static void ResetColor()
    {
        _foreground = ConsoleColor.Gray;
        _background = ConsoleColor.Black;
        Flush();
        ConsoleSetColors(-1, -1);
    }

    // ==================== Encodings / redirection ====================

    /// <summary>Gets or sets the console output encoding (UTF-8 in Phase 2).</summary>
    public static Encoding OutputEncoding { get; set; } = Encoding.UTF8;

    /// <summary>Gets or sets the console input encoding (UTF-8 in Phase 2).</summary>
    public static Encoding InputEncoding { get; set; } = Encoding.UTF8;

    /// <summary>
    /// Gets a value indicating whether input is redirected. True when the
    /// shell has installed a reader with <see cref="SetIn"/>; otherwise
    /// false (the console is the serial device).
    /// </summary>
    public static bool IsInputRedirected
    {
        get
        {
            if (!ReferenceEquals(_inReader, (TextReader)_defaultIn))
                return true;
            unsafe
            {
                int inr, outr;
                ConsoleIsRedirected(&inr, &outr);
                return inr != 0;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether output is redirected. True when the
    /// shell has installed a writer with <see cref="SetOut"/>/<see cref="SetError"/>;
    /// otherwise false (the console is the serial device).
    /// </summary>
    public static bool IsOutputRedirected
    {
        get
        {
            if (!ReferenceEquals(_outWriter, (TextWriter)_defaultOut) ||
                !ReferenceEquals(_errorWriter, (TextWriter)_defaultError))
                return true;
            unsafe
            {
                int inr, outr;
                ConsoleIsRedirected(&inr, &outr);
                return outr != 0;
            }
        }
    }

    /// <summary>
    /// Switches the console input between canonical (line editing, echo)
    /// and raw mode. NeutrinoOS extension (the BCL decides this at the OS
    /// level; on NeutrinoOS the same tty serves both).
    /// </summary>
    public static void SetRawMode(bool rawMode)
    {
        ConsoleSetLineMode(rawMode ? 1 : 0);
    }

    // ==================== Internal writer/reader ====================

    /// <summary>
    /// Buffered console writer: batches characters into 512-character
    /// chunks and flushes on newline, explicit Flush, when the buffer is
    /// full, or after 50 ms of pending output (whichever comes first).
    /// </summary>
    private sealed class ConsoleTextWriter : TextWriter
    {
        private const int BufferSize = 512;
        private const uint FlushIntervalMs = 50;

        private readonly char[] _buffer = new char[BufferSize];
        private int _count;
        private uint _lastFlushTick;

        public ConsoleTextWriter()
        {
            _lastFlushTick = ConsoleGetTickMs();
        }

        public override void Write(char value)
        {
            _buffer[_count++] = value;

            if (value == '\n' || _count >= BufferSize)
            {
                Flush();
                return;
            }

            // Time-based flush for partial lines (e.g. prompts) - checked
            // lazily on each write so no background timer is required.
            if ((ConsoleGetTickMs() - _lastFlushTick) >= FlushIntervalMs)
                Flush();
        }

        public override void Flush()
        {
            if (_count == 0)
            {
                _lastFlushTick = ConsoleGetTickMs();
                return;
            }

            unsafe
            {
                fixed (char* p = _buffer)
                {
                    ConsoleWriteChars(p, _count);
                }
            }
            _count = 0;
            _lastFlushTick = ConsoleGetTickMs();

            // Also let the kernel flush its own layer (line discipline echo
            // uses the device directly; this is a belt-and-braces poke).
            ConsoleFlush();
        }
    }

    /// <summary>
    /// Console reader: Read() uses the raw byte path, ReadLine() delegates
    /// to the kernel line discipline.
    /// </summary>
    private sealed class ConsoleTextReader : TextReader
    {
        public override int Read()
        {
            return global::System.Console.Read();
        }

        public override string? ReadLine()
        {
            return global::System.Console.ReadLine();
        }
    }
}
