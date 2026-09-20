// NeutrinoOS kernel - Console Abstraction Layer: IConsoleDevice
//
// Phase 2: a console device is a text surface with input. The serial
// console (/dev/ttyS0) is the only device in Phase 2; a VGA text device
// may be added in a later phase without changing the CALL or korlib.

using System;

namespace ProtonOS.Platform;

/// <summary>
/// A console device: character output plus key/line input.
/// Implementations route to a physical console (serial UART in Phase 2).
/// </summary>
public interface IConsoleDevice
{
    /// <summary>Human-readable device name (e.g. "ttyS0").</summary>
    string Name { get; }

    /// <summary>Writes a single character.</summary>
    void Write(char c);

    /// <summary>Writes a span of characters.</summary>
    void Write(ReadOnlySpan<char> s);

    /// <summary>Attempts to dequeue a decoded key press (non-blocking).</summary>
    bool TryReadKey(out System.ConsoleKeyInfo key);

    /// <summary>Blocks until a key press is available and returns it.</summary>
    System.ConsoleKeyInfo ReadKey(bool intercept);

    /// <summary>Clears the screen and homes the cursor.</summary>
    void Clear();

    /// <summary>Sets the cursor position.</summary>
    void SetCursorPosition(int left, int top);

    /// <summary>Gets the (shadow) cursor position.</summary>
    void GetCursorPosition(out int left, out int top);

    /// <summary>Sets foreground/background colors; -1 restores defaults.</summary>
    void SetColors(int foreground, int background);

    /// <summary>Flushes any buffered output.</summary>
    void Flush();

    /// <summary>Whether input is redirected from a non-console source.</summary>
    bool IsInputRedirected { get; }

    /// <summary>Whether output is redirected to a non-console sink.</summary>
    bool IsOutputRedirected { get; }

    /// <summary>Configured console width in columns.</summary>
    int WindowWidth { get; }

    /// <summary>Configured console height in rows.</summary>
    int WindowHeight { get; }

    /// <summary>Whether a decoded key press is waiting.</summary>
    bool KeyAvailable { get; }

    /// <summary>
    /// Reads a full line through the device's line discipline.
    /// Returns the number of characters written to the destination,
    /// -1 for EOF (Ctrl+D on an empty line), or -2 when cancelled (Ctrl+C).
    /// </summary>
    int ReadLine(Span<char> destination);

    /// <summary>Switches the line discipline between canonical and raw mode.</summary>
    void SetRawMode(bool rawMode);

    /// <summary>
    /// Copies the text since the last newline (the current prompt) into the
    /// destination for line-redraw purposes. Returns the number of chars.
    /// </summary>
    int GetPromptTail(Span<char> destination);
}
