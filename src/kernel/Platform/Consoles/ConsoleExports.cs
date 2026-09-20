// NeutrinoOS kernel - console exports for korlib (System.Console)
//
// [UnmanagedCallersOnly] entry points resolved by the korlib DllImports
// (System.Console, System.Environment). All output flows through the
// Console Abstraction Layer; all input through the line discipline.

using System;
using System.Runtime.InteropServices;
using ProtonOS.X64;

namespace ProtonOS.Platform;

/// <summary>
/// Kernel-side implementations of the System.Console kernel exports.
/// </summary>
public static unsafe class ConsoleExports
{
    /// <summary>Writes UTF-16 characters to the console (encoded as UTF-8).</summary>
    [UnmanagedCallersOnly(EntryPoint = "ConsoleWriteChars")]
    public static void ConsoleWriteChars(char* chars, int count)
    {
        if (chars == null || count <= 0)
            return;
        ConsoleAbstractionLayer.Write(new ReadOnlySpan<char>(chars, count));
    }

    /// <summary>
    /// Reads a line into the buffer. Returns the character count, -1 for
    /// EOF (Ctrl+D on an empty line), or -2 when cancelled (Ctrl+C).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "ConsoleReadLine")]
    public static int ConsoleReadLine(char* buffer, int capacity)
    {
        if (buffer == null || capacity <= 0)
            return -1;
        return ConsoleAbstractionLayer.ReadLine(new Span<char>(buffer, capacity));
    }

    /// <summary>
    /// Reads a key event. When blocking is non-zero, waits for a key.
    /// echo controls whether the key is echoed to the console (0 for
    /// intercepting readers). Returns 1 and fills the outputs when a key
    /// was produced, 0 otherwise.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "ConsoleReadKey")]
    public static int ConsoleReadKey(int blocking, int echo, char* keyChar, int* key, int* modifiers)
    {
        if (keyChar == null || key == null || modifiers == null)
            return 0;

        if (LineDiscipline.TryDequeueKey(out ConsoleKeyInfo info))
        {
            *keyChar = info.KeyChar;
            *key = (int)info.Key;
            *modifiers = (int)info.Modifiers;
            return 1;
        }

        if (blocking == 0)
            return 0;

        LineDiscipline.BeginKeyRead(echo: echo != 0);
        try
        {
            while (true)
            {
                if (LineDiscipline.TryDequeueKey(out info))
                {
                    *keyChar = info.KeyChar;
                    *key = (int)info.Key;
                    *modifiers = (int)info.Modifiers;
                    return 1;
                }
                CPU.Halt();
                LineDiscipline.PollTimeouts();
            }
        }
        finally
        {
            LineDiscipline.EndKeyRead();
        }
    }

    /// <summary>Returns 1 when a decoded key event is waiting.</summary>
    [UnmanagedCallersOnly(EntryPoint = "ConsoleKeyAvailable")]
    public static int ConsoleKeyAvailable()
        => LineDiscipline.KeyAvailable ? 1 : 0;

    /// <summary>Reads one character via the key path (Console.Read).</summary>
    [UnmanagedCallersOnly(EntryPoint = "ConsoleReadChar")]
    public static int ConsoleReadChar()
        => ConsoleAbstractionLayer.ReadChar();

    /// <summary>Clears the screen and homes the cursor.</summary>
    [UnmanagedCallersOnly(EntryPoint = "ConsoleClear")]
    public static void ConsoleClear()
        => ConsoleAbstractionLayer.Devices.Clear();

    /// <summary>Sets the cursor position.</summary>
    [UnmanagedCallersOnly(EntryPoint = "ConsoleSetCursor")]
    public static void ConsoleSetCursor(int left, int top)
        => ConsoleAbstractionLayer.Devices.SetCursorPosition(left, top);

    /// <summary>Gets the shadow cursor position.</summary>
    [UnmanagedCallersOnly(EntryPoint = "ConsoleGetCursor")]
    public static void ConsoleGetCursor(int* left, int* top)
    {
        ConsoleAbstractionLayer.Devices.GetCursorPosition(out int l, out int t);
        if (left != null) *left = l;
        if (top != null) *top = t;
    }

    /// <summary>Gets the configured console size (80x50 default).</summary>
    [UnmanagedCallersOnly(EntryPoint = "ConsoleGetSize")]
    public static void ConsoleGetSize(int* width, int* height)
    {
        if (width != null) *width = ConsoleAbstractionLayer.DefaultWidth;
        if (height != null) *height = ConsoleAbstractionLayer.DefaultHeight;
    }

    /// <summary>Flushes console output.</summary>
    [UnmanagedCallersOnly(EntryPoint = "ConsoleFlush")]
    public static void ConsoleFlush()
        => ConsoleAbstractionLayer.Flush();

    /// <summary>Sets foreground/background colors (-1 = terminal default).</summary>
    [UnmanagedCallersOnly(EntryPoint = "ConsoleSetColors")]
    public static void ConsoleSetColors(int foreground, int background)
        => ConsoleAbstractionLayer.Devices.SetColors(foreground, background);

    /// <summary>Switches the line discipline between canonical and raw mode.</summary>
    [UnmanagedCallersOnly(EntryPoint = "ConsoleSetLineMode")]
    public static void ConsoleSetLineMode(int rawMode)
        => LineDiscipline.SetRawMode(rawMode != 0);

    /// <summary>Sets whether Ctrl+C is delivered as input.</summary>
    [UnmanagedCallersOnly(EntryPoint = "ConsoleSetCtrlCAsInput")]
    public static void ConsoleSetCtrlCAsInput(int asInput)
        => LineDiscipline.SetTreatControlCAsInput(asInput != 0);

    /// <summary>Reports input/output redirection (always false in Phase 2).</summary>
    [UnmanagedCallersOnly(EntryPoint = "ConsoleIsRedirected")]
    public static void ConsoleIsRedirected(int* inputRedirected, int* outputRedirected)
    {
        if (inputRedirected != null) *inputRedirected = 0;
        if (outputRedirected != null) *outputRedirected = 0;
    }

    /// <summary>Milliseconds since boot (APIC timer tick count).</summary>
    [UnmanagedCallersOnly(EntryPoint = "ConsoleGetTickMs")]
    public static uint ConsoleGetTickMs()
        => (uint)APIC.TickCount;
}

/// <summary>
/// Kernel-side implementations of the System.Environment kernel exports.
/// </summary>
public static unsafe class EnvironmentExports
{
    /// <summary>
    /// Terminates the calling context. On the kernel boot shell there is
    /// no init process to return to, so the CPU halts (Phase 2 behavior).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "EnvExit")]
    public static void EnvExit(int exitCode)
    {
        ConsoleAbstractionLayer.Write("[ENV] exit(");
        ConsoleAbstractionLayer.Write(exitCode.ToString());
        ConsoleAbstractionLayer.WriteLine(")");
        ConsoleAbstractionLayer.Flush();
        CPU.HaltForever();
    }

    /// <summary>
    /// Copies the current working directory into the buffer and returns its
    /// length. Phase 2 has no VFS working directory for the boot shell, so
    /// "/" is returned (documented deviation).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "EnvGetCurrentDirectory")]
    public static int EnvGetCurrentDirectory(char* buffer, int capacity)
    {
        if (buffer == null || capacity < 2)
            return 0;
        buffer[0] = '/';
        return 1;
    }
}
