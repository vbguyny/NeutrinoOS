// NeutrinoOS kernel - debug console facade
//
// Phase 2: the actual serial hardware is driven by Uart16550 and all
// output is routed through the Console Abstraction Layer once it is
// initialized (after the scheduler starts). This class is kept as the
// stable logging API used across the kernel; during early boot (before
// the CAL exists) it falls back to writing to the UART driver directly.

using System.Runtime.InteropServices;

namespace ProtonOS.Platform;

/// <summary>
/// NeutrinoOS serial console: UART 16550 output on COM1 (115200 8N1).
/// This is the only console device in Phase 1 (console-only fork - no
/// framebuffer, no graphics). Logically the system's ttyS0.
/// </summary>
public static unsafe class DebugConsole
{
    // COM1 port addresses
    private const ushort COM1 = 0x3F8;
    private const ushort COM1_DATA = COM1 + 0;      // Data register
    private const ushort COM1_IER = COM1 + 1;       // Interrupt Enable Register
    private const ushort COM1_FCR = COM1 + 2;       // FIFO Control Register
    private const ushort COM1_LCR = COM1 + 3;       // Line Control Register
    private const ushort COM1_MCR = COM1 + 4;       // Modem Control Register
    private const ushort COM1_LSR = COM1 + 5;       // Line Status Register
    private const ushort COM1_DLL = COM1 + 0;       // Divisor Latch Low (DLAB=1)
    private const ushort COM1_DLH = COM1 + 1;       // Divisor Latch High (DLAB=1)

    // Import nernel port I/O functions
    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern void outb(ushort port, byte value);

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern byte inb(ushort port);

    /// <summary>
    /// Initialize COM1 at 115200 baud, 8N1 via the production UART driver.
    /// </summary>
    public static void Init()
    {
        Uart16550.Initialize(115200, 0);
    }

    /// <summary>
    /// Check if data is available to read
    /// </summary>
    public static bool IsDataAvailable()
    {
        if (Uart16550.InterruptsEnabled)
            return Uart16550.BytesAvailable > 0;

        // Polled mode (early boot): LSR bit 0 = Data Ready
        return (inb(COM1_LSR) & 0x01) != 0;
    }

    /// <summary>
    /// Read a single byte (blocking)
    /// </summary>
    public static byte ReadByte()
    {
        return Uart16550.ReadByte();
    }

    /// <summary>
    /// Read a single key byte from the console (blocking).
    /// Phase 1 console input API; alias for ReadByte().
    /// </summary>
    public static byte ReadKey()
    {
        return ReadByte();
    }

    /// <summary>
    /// Try to read a single byte (non-blocking)
    /// </summary>
    /// <param name="value">The byte read, if available</param>
    /// <returns>True if a byte was read, false if no data available</returns>
    public static bool TryReadByte(out byte value)
    {
        return Uart16550.TryReadByte(out value);
    }

    // Tracks the previous byte so "\n" can be translated to "\r\n"
    // without doubling CR in an existing "\r\n" sequence.
    private static bool _lastWasCr;

    /// <summary>
    /// Write a single byte, translating LF to CRLF (console line discipline).
    /// Routes through the CAL once initialized; otherwise writes the UART
    /// directly (early boot).
    /// </summary>
    public static void WriteByte(byte b)
    {
        if (ConsoleAbstractionLayer.IsInitialized)
        {
            ConsoleAbstractionLayer.Devices.Write((char)b);
            return;
        }

        if (b == 0x0A && !_lastWasCr)
        {
            Uart16550.WriteByte(0x0D);  // CR before LF for serial terminals that need CRLF
        }
        Uart16550.WriteByte(b);
        _lastWasCr = b == 0x0D;
    }

    /// <summary>
    /// Write a single byte to the UART without translation.
    /// </summary>
    private static void RawWriteByte(byte b)
    {
        // Wait for transmit buffer empty
        while ((inb(COM1_LSR) & 0x20) == 0) { }
        outb(COM1_DATA, b);
    }

    /// <summary>
    /// Write a single character
    /// </summary>
    public static void WriteChar(char c)
    {
        WriteByte((byte)c);
    }

    /// <summary>
    /// Write a string
    /// </summary>
    public static void Write(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            WriteByte((byte)s[i]);
        }
    }

    /// <summary>
    /// Write a string followed by newline
    /// </summary>
    public static void WriteLine(string s)
    {
        Write(s);
        WriteLine();
    }

    /// <summary>
    /// Write a newline (CR+LF)
    /// </summary>
    public static void WriteLine()
    {
        WriteByte(0x0D);  // CR
        WriteByte(0x0A);  // LF
    }

    /// <summary>
    /// Write a 64-bit value as hexadecimal followed by a space.
    /// Used for early boot output before string.Format is available.
    /// </summary>
    public static void WriteHex(ulong value)
    {
        // Write digits from most significant to least
        for (int i = 60; i >= 0; i -= 4)
        {
            int nibble = (int)((value >> i) & 0xF);
            byte c = (byte)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);
            WriteByte(c);
        }
        WriteByte((byte)' ');
    }

    /// <summary>
    /// Write a 32-bit value as hexadecimal followed by a space.
    /// </summary>
    public static void WriteHex(uint value)
    {
        for (int i = 28; i >= 0; i -= 4)
        {
            int nibble = (int)((value >> i) & 0xF);
            byte c = (byte)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);
            WriteByte(c);
        }
        WriteByte((byte)' ');
    }

    /// <summary>
    /// Write a 16-bit value as hexadecimal followed by a space.
    /// </summary>
    public static void WriteHex(ushort value)
    {
        for (int i = 12; i >= 0; i -= 4)
        {
            int nibble = (value >> i) & 0xF;
            byte c = (byte)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);
            WriteByte(c);
        }
        WriteByte((byte)' ');
    }

    /// <summary>
    /// Write a single byte as 2-digit hexadecimal followed by a space.
    /// </summary>
    public static void WriteHex(byte value)
    {
        int hi = (value >> 4) & 0xF;
        int lo = value & 0xF;
        WriteByte((byte)(hi < 10 ? '0' + hi : 'A' + hi - 10));
        WriteByte((byte)(lo < 10 ? '0' + lo : 'A' + lo - 10));
        WriteByte((byte)' ');
    }

    /// <summary>
    /// Write a signed integer as decimal
    /// </summary>
    public static void WriteDecimal(int value)
    {
        if (value == 0)
        {
            WriteByte((byte)'0');
            return;
        }

        if (value < 0)
        {
            WriteByte((byte)'-');
            value = -value;
        }

        WriteDecimal((uint)value);
    }

    /// <summary>
    /// Write an unsigned integer as decimal
    /// </summary>
    public static void WriteDecimal(uint value)
    {
        if (value == 0)
        {
            WriteByte((byte)'0');
            return;
        }

        // Find the highest power of 10 <= value
        uint divisor = 1;
        uint temp = value;
        while (temp >= 10)
        {
            divisor *= 10;
            temp /= 10;
        }

        // Write digits from most significant to least
        while (divisor > 0)
        {
            uint digit = value / divisor;
            WriteByte((byte)('0' + digit));
            value %= divisor;
            divisor /= 10;
        }
    }

    /// <summary>
    /// Write an unsigned 64-bit integer as decimal
    /// </summary>
    public static void WriteDecimal(ulong value)
    {
        if (value == 0)
        {
            WriteByte((byte)'0');
            return;
        }

        // Find the highest power of 10 <= value
        ulong divisor = 1;
        ulong temp = value;
        while (temp >= 10)
        {
            divisor *= 10;
            temp /= 10;
        }

        // Write digits from most significant to least
        while (divisor > 0)
        {
            ulong digit = value / divisor;
            WriteByte((byte)('0' + digit));
            value %= divisor;
            divisor /= 10;
        }
    }

    /// <summary>
    /// Write a decimal number with zero-padding to specified width
    /// </summary>
    public static void WriteDecimalPadded(int value, int width)
    {
        // Calculate number of digits
        int digits = 1;
        int temp = value;
        if (temp < 0) temp = -temp;
        while (temp >= 10)
        {
            digits++;
            temp /= 10;
        }

        // Add padding zeros
        for (int i = digits; i < width; i++)
        {
            WriteByte((byte)'0');
        }

        WriteDecimal(value);
    }
}
