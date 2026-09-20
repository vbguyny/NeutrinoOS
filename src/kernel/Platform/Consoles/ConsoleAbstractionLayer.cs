// NeutrinoOS kernel - Console Abstraction Layer (CAL)
//
// Owns the console multiplexer, registers /dev/ttyS0, wires the UART RX
// interrupt to the line discipline, and provides the write/read entry
// points used by DebugConsole (kernel logging) and the korlib console
// exports (System.Console for kernel and JIT-compiled code).
//
// Initialization order (after Scheduler.EnableScheduling in Kernel.Main):
//   1. SerialUart interrupts were enabled during boot (polled output only)
//   2. Cal.Initialize():
//        a. creates the SerialConsoleDevice backed by Uart16550
//        b. registers it with the multiplexer and the device registry
//          as /dev/ttyS0
//        c. routes UART RX bytes into LineDiscipline.Feed
//        d. enables UART RX interrupts (IRQ4 -> vector 36)
//   3. shell runs via System.Console (ReadLine / Write)

using System;
using ProtonOS.X64;

namespace ProtonOS.Platform;

/// <summary>
/// Console Abstraction Layer: multiplexer ownership and the kernel-side
/// console entry points used by DebugConsole and the System.Console
/// kernel exports.
/// </summary>
public static unsafe class ConsoleAbstractionLayer
{
    /// <summary>Default configured console width.</summary>
    public const int DefaultWidth = 80;

    /// <summary>Default configured console height.</summary>
    public const int DefaultHeight = 50;

    /// <summary>The console multiplexer (devices + active input).</summary>
    /// <remarks>
    /// Lazily created: the static constructor of this class is triggered by
    /// DebugConsole's very first write (before the kernel heap exists), so
    /// NO allocation may happen during static initialization - otherwise
    /// the allocator fails and reports through Environment.FailFast.
    /// </remarks>
    public static ConsoleMultiplexer Devices => _devices ??= new ConsoleMultiplexer();

    private static ConsoleMultiplexer? _devices;

    /// <summary>Whether the CAL has been initialized.</summary>
    public static bool IsInitialized { get; private set; }

    /// <summary>
    /// Initializes the CAL: creates the serial console device, registers
    /// it as /dev/ttyS0, and starts interrupt-driven console input.
    /// Must be called after the scheduler is enabled.
    /// </summary>
    public static void Initialize()
    {
        if (IsInitialized)
            return;

        var serial = new SerialConsoleDevice();
        Devices.Register(serial);
        Devices.SetActiveInput(serial);
        ConsoleDeviceRegistry.Register(SerialConsoleDevice.DevicePath, serial);

        // Route UART RX bytes into the line discipline and enable RX IRQs
        Uart16550.SetRxConsumer(&LineDiscipline.Feed);
        Uart16550.EnableInterrupts();

        IsInitialized = true;
    }

    // ==================== Output helpers ====================

    /// <summary>Writes a string to all console devices (early-boot fallback: UART).</summary>
    public static void Write(string s)
    {
        if (Devices.DeviceCount > 0)
            Devices.Write(s);
        else
            Uart16550.Write(s);
    }

    /// <summary>Writes a character to all console devices (early-boot fallback: UART).</summary>
    public static void Write(char c)
    {
        if (Devices.DeviceCount > 0)
            Devices.Write(c);
        else
            Uart16550.WriteByte(c < 256 ? (byte)c : (byte)'?');
    }

    /// <summary>Writes a span of characters.</summary>
    public static void Write(ReadOnlySpan<char> s)
    {
        if (Devices.DeviceCount > 0)
            Devices.Write(s);
        else
        {
            for (int i = 0; i < s.Length; i++)
                Write(s[i]);
        }
    }

    /// <summary>Writes a CRLF via the active devices.</summary>
    public static void WriteLine()
    {
        if (Devices.DeviceCount > 0)
            Devices.WriteLine();
        else
        {
            Uart16550.WriteByte(0x0D);
            Uart16550.WriteByte(0x0A);
        }
    }

    /// <summary>Writes a string followed by CRLF.</summary>
    public static void WriteLine(string s)
    {
        Write(s);
        WriteLine();
    }

    /// <summary>Flushes all console devices.</summary>
    public static void Flush()
    {
        Devices.Flush();
    }

    // ==================== Input helpers ====================

    /// <summary>Whether a key press is available.</summary>
    public static bool KeyAvailable => LineDiscipline.KeyAvailable;

    /// <summary>Attempts to dequeue a key press.</summary>
    public static bool TryReadKey(out ConsoleKeyInfo key)
        => LineDiscipline.TryDequeueKey(out key);

    /// <summary>Reads a line through the active console device.</summary>
    public static int ReadLine(Span<char> destination)
    {
        var device = Devices.ActiveInput;
        if (device == null)
            return -1;
        return device.ReadLine(destination);
    }

    /// <summary>
    /// Reads a single character for Console.Read(): the KeyChar of the next
    /// key event, or -1 if none can be produced (Phase 2 has no EOF state
    /// for this path; documented).
    /// </summary>
    public static int ReadChar()
    {
        if (LineDiscipline.TryDequeueKey(out ConsoleKeyInfo key))
            return key.KeyChar == '\0' ? (int)key.Key : key.KeyChar;

        LineDiscipline.BeginKeyRead(echo: false);
        try
        {
            while (true)
            {
                if (LineDiscipline.TryDequeueKey(out key))
                    return key.KeyChar == '\0' ? (int)key.Key : key.KeyChar;
                CPU.Halt();
                LineDiscipline.PollTimeouts();
            }
        }
        finally
        {
            LineDiscipline.EndKeyRead();
        }
    }
}
