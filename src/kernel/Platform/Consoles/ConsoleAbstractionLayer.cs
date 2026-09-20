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
//
// Phase 3 extensions:
//   - VgaConsoleDevice (/dev/vga0) registered on the standard VGA text
//     buffer (80x25 or 80x50); boot parameters are marker files
//     (console-vga-off, console-vga-80x50, console-active-vga)
//   - Ps2Keyboard initialized (IRQ1 -> vector 33) with its bytes fed
//     into the same LineDiscipline as the UART
//   - echo mirrored to both consoles; the active input device follows
//     whichever console received the most recent byte

using System;
using System.Runtime.InteropServices;
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

    private static SerialConsoleDevice? _serialDevice;
    private static VgaConsoleDevice? _vgaDevice;
    private static bool _keyboardIsActiveInput;

    /// <summary>The VGA console device, when enabled (null otherwise).</summary>
    public static VgaConsoleDevice? VgaDevice => _vgaDevice;

    /// <summary>The serial console device.</summary>
    public static SerialConsoleDevice? SerialDevice => _serialDevice;

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
        _serialDevice = serial;

        // Route UART RX bytes into the line discipline and enable RX IRQs.
        // The consumer also makes the serial console the active input
        // again whenever serial bytes arrive (Phase 3 auto-switch).
        Uart16550.SetRxConsumer(&OnSerialRxByte);
        Uart16550.EnableInterrupts();

        // Phase 3: VGA text console and PS/2 keyboard. The VGA console is
        // normally brought up early (EarlyInitVgaConsole); the late path
        // here is a fallback and also performs the device registration.
        Uart16550.Write("[CAL-T1]");
        if (_vgaDevice == null)
            InitializeVgaConsole();
        RegisterVgaConsole();
        Uart16550.Write("[CAL-T8]");
        if (_vgaDevice != null)
        {
            if (BootInfoAccess.FindFile("skip-echo", out _) == null)
            {
                LineDiscipline.SetEchoSink(&EchoSink);
                Uart16550.Write("[CAL-T9]");
            }
            if (BootInfoAccess.FindFile("skip-ps2", out _) == null)
            {
                Ps2Keyboard.Initialize(&OnKeyboardByte);
                Uart16550.Write("[CAL-TA]");
            }
        }

        // Output now fans out through the multiplexer (serial + VGA), so
        // the early debug mirror must be cleared to avoid double-writing
        // to the VGA console.
        DebugConsole.SetEarlyMirror(null);

        IsInitialized = true;
    }

    // ==================== Phase 3: VGA console + PS/2 keyboard ====================

    /// <summary>
    /// Early boot: brings up the VGA text console (unless the
    /// console-vga-off marker is present) and mirrors DebugConsole output
    /// to it until CAL.Initialize() reroutes output through the device
    /// multiplexer. Call once after the heap and arch stage 2 are ready -
    /// early enough that the whole boot log is visible in the VM window.
    /// The console-vga-80x50 marker selects the 50-row mode.
    /// </summary>
    public static void EarlyInitVgaConsole()
    {
        if (_vgaDevice != null || IsInitialized)
            return;
        if (BootInfoAccess.FindFile("console-vga-off", out _) != null)
            return;

        bool mode80x50 = BootInfoAccess.FindFile("console-vga-80x50", out _) != null;

        Uart16550.Write("[VGA-early-a]");
        var vga = new VgaConsoleDevice();
        vga.Initialize(mode80x50);
        Uart16550.Write("[VGA-early-b]");

        _vgaDevice = vga;
        DebugConsole.SetEarlyMirror(&MirrorEarlyByte);
    }

    /// <summary>Early-boot mirror target (ISR-safe raw VGA write).</summary>
    [UnmanagedCallersOnly]
    private static void MirrorEarlyByte(byte b)
    {
        var vga = _vgaDevice;
        if (vga != null)
            vga.MirrorRawByte(b);
    }

    /// <summary>
    /// Late-boot fallback: creates and initializes the VGA text console
    /// when the early path did not run (e.g. booted through a path that
    /// skips EarlyInitVgaConsole). Honours console-vga-off and
    /// console-vga-80x50.
    /// </summary>
    private static void InitializeVgaConsole()
    {
        Uart16550.Write("[CAL-T2]");
        if (BootInfoAccess.FindFile("console-vga-off", out _) != null)
            return;
        Uart16550.Write("[CAL-T3]");

        bool mode80x50 = BootInfoAccess.FindFile("console-vga-80x50", out _) != null;

        var vga = new VgaConsoleDevice();
        Uart16550.Write("[CAL-T4]");
        vga.Initialize(mode80x50);
        Uart16550.Write("[CAL-T5]");
        _vgaDevice = vga;
    }

    /// <summary>
    /// Registers the (early- or late-initialized) VGA console with the
    /// multiplexer and the device registry, and applies the active-input
    /// marker. Honours skip-vga-register - which preserves the legacy
    /// semantics that no console-device integration (echo sink, PS/2
    /// keyboard, /dev/vga0) happens in that mode.
    /// </summary>
    private static void RegisterVgaConsole()
    {
        var vga = _vgaDevice;
        if (vga == null)
            return;

        if (BootInfoAccess.FindFile("skip-vga-register", out _) != null)
        {
            Uart16550.Write("[CAL-NOREG]");
            _vgaDevice = null;
            return;
        }

        Devices.Register(vga);
        Uart16550.Write("[CAL-T6]");
        ConsoleDeviceRegistry.Register(VgaConsoleDevice.DevicePath, vga);
        Uart16550.Write("[CAL-T7]");

        if (BootInfoAccess.FindFile("console-active-vga", out _) != null)
            Devices.SetActiveInput(vga);
    }

    /// <summary>
    /// Echo sink for the line discipline: mirrors every echo byte to the
    /// serial UART (non-blocking) and to the VGA text console. Runs in
    /// interrupt context, so both paths are ISR-safe.
    /// </summary>
    [UnmanagedCallersOnly]
    private static void EchoSink(byte b)
    {
        Uart16550.TryWriteByte(b);
        var vga = _vgaDevice;
        if (vga != null)
            vga.EchoRawByte(b);
    }

    /// <summary>
    /// UART RX consumer: feeds the byte into the line discipline, then
    /// makes the serial console the active input (auto-switch between
    /// serial and VGA happens on whichever console received a byte last).
    /// </summary>
    [UnmanagedCallersOnly]
    private static void OnSerialRxByte(byte b)
    {
        LineDiscipline.FeedByte(b);

        if (_keyboardIsActiveInput)
        {
            _keyboardIsActiveInput = false;
            var serial = _serialDevice;
            if (serial != null)
                Devices.SetActiveInput(serial);
        }
    }

    /// <summary>
    /// PS/2 keyboard byte consumer: feeds the synthesized ANSI byte into
    /// the same line discipline as the UART, then makes the VGA console
    /// the active input device.
    /// </summary>
    [UnmanagedCallersOnly]
    private static void OnKeyboardByte(byte b)
    {
        LineDiscipline.FeedByte(b);

        if (!_keyboardIsActiveInput)
        {
            _keyboardIsActiveInput = true;
            var vga = _vgaDevice;
            if (vga != null)
                Devices.SetActiveInput(vga);
        }
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
