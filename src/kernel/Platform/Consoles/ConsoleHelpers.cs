// NeutrinoOS kernel - JIT console bridge helpers
//
// Bridge targets for JIT-compiled applications: the AOT method registry
// (AotMethodRegistry.RegisterConsoleMethods) maps System.Console /
// System.Environment members to these helpers, which forward to the kernel's
// AOT-compiled korlib console implementation (the same code path the kernel
// shell uses: CAL -> line discipline -> UART).
//
// This keeps JIT console calls off the Tier-0 JIT pipeline entirely - the
// korlib IL bodies (buffered TextWriter subclass, cctor, virtual dispatch)
// are never compiled for applications; calls bind directly to this AOT code.
//
// Conventions: methods whose IL signature uses small value types (bool,
// enums) take int in the helper (the JIT passes 32-bit values); ReadKey uses
// the hidden-return-buffer convention (registered with ReturnStructSize=17).

namespace ProtonOS.Platform;

/// <summary>
/// Forwarding helpers bridging JIT-compiled System.Console calls to the
/// kernel's AOT console implementation.
/// </summary>
public static unsafe class ConsoleHelpers
{
    // ==================== Output ====================

    public static void WriteChar(char value) => System.Console.Write(value);

    public static void WriteString(string? value) => System.Console.Write(value);

    public static void WriteBool(bool value) => System.Console.Write(value);

    public static void WriteInt(int value) => System.Console.Write(value);

    public static void WriteLong(long value) => System.Console.Write(value);

    public static void WriteObject(object? value) => System.Console.Write(value);

    public static void WriteFormat1(string format, object? arg0)
        => System.Console.Write(format, arg0);

    public static void WriteFormat2(string format, object? arg0, object? arg1)
        => System.Console.Write(format, arg0, arg1);

    public static void WriteFormat3(string format, object? arg0, object? arg1, object? arg2)
        => System.Console.Write(format, arg0, arg1, arg2);

    public static void WriteLineEmpty() => System.Console.WriteLine();

    public static void WriteLineChar(char value) => System.Console.WriteLine(value);

    public static void WriteLineString(string? value) => System.Console.WriteLine(value);

    public static void WriteLineBool(bool value) => System.Console.WriteLine(value);

    public static void WriteLineInt(int value) => System.Console.WriteLine(value);

    public static void WriteLineLong(long value) => System.Console.WriteLine(value);

    public static void WriteLineObject(object? value) => System.Console.WriteLine(value);

    public static void WriteLineFormat1(string format, object? arg0)
        => System.Console.WriteLine(format, arg0);

    public static void WriteLineFormat2(string format, object? arg0, object? arg1)
        => System.Console.WriteLine(format, arg0, arg1);

    public static void WriteLineFormat3(string format, object? arg0, object? arg1, object? arg2)
        => System.Console.WriteLine(format, arg0, arg1, arg2);

    public static void Flush() => System.Console.Flush();

    // ==================== Input ====================

    public static string? ReadLine() => System.Console.ReadLine();

    public static int ReadChar() => System.Console.Read();

    public static int KeyAvailable() => System.Console.KeyAvailable ? 1 : 0;

    /// <summary>
    /// Console.ReadKey bridge. Registered with ReturnStructSize=17 so the JIT
    /// passes the hidden return buffer in RCX (matching the Windows x64 ABI
    /// for the 12-byte ConsoleKeyInfo), then the intercept flag in RDX.
    /// </summary>
    public static void ReadKey(nint result, int intercept)
        => *(System.ConsoleKeyInfo*)result = System.Console.ReadKey(intercept != 0);

    // ==================== Screen / cursor ====================

    public static void Clear() => System.Console.Clear();

    public static void SetCursorPosition(int left, int top)
        => System.Console.SetCursorPosition(left, top);

    public static int GetCursorLeft() => System.Console.CursorLeft;

    public static int GetCursorTop() => System.Console.CursorTop;

    public static int GetWindowWidth() => System.Console.WindowWidth;

    public static int GetWindowHeight() => System.Console.WindowHeight;

    // ==================== Colors ====================

    public static int GetForegroundColor() => (int)System.Console.ForegroundColor;

    public static void SetForegroundColor(int color)
        => System.Console.ForegroundColor = (System.ConsoleColor)color;

    public static int GetBackgroundColor() => (int)System.Console.BackgroundColor;

    public static void SetBackgroundColor(int color)
        => System.Console.BackgroundColor = (System.ConsoleColor)color;

    public static void ResetColor() => System.Console.ResetColor();

    // ==================== Redirection / modes ====================

    public static int IsInputRedirected() => System.Console.IsInputRedirected ? 1 : 0;

    public static int IsOutputRedirected() => System.Console.IsOutputRedirected ? 1 : 0;

    public static System.Text.Encoding GetOutputEncoding() => System.Console.OutputEncoding;

    public static System.Text.Encoding GetInputEncoding() => System.Console.InputEncoding;

    public static void SetRawMode(int rawMode) => System.Console.SetRawMode(rawMode != 0);

    public static int GetTreatControlCAsInput()
        => System.Console.TreatControlCAsInput ? 1 : 0;

    public static void SetTreatControlCAsInput(int value)
        => System.Console.TreatControlCAsInput = value != 0;

    // ==================== Environment ====================

    public static string GetNewLine() => System.Environment.NewLine;

    public static string GetCurrentDirectory() => System.Environment.CurrentDirectory;

    // ==================== Text.Encoding ====================

    public static System.Text.Encoding GetUTF8() => System.Text.Encoding.UTF8;

    public static byte[] GetBytes(System.Text.Encoding encoding, string s)
        => encoding.GetBytes(s);

    public static string GetString(System.Text.Encoding encoding, byte[] bytes)
        => encoding.GetString(bytes);
}
