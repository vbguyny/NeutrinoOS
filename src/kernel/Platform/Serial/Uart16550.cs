// NeutrinoOS kernel - production UART 16550 driver (ttyS0)
//
// Phase 2: replaces the Phase 1 write-only DebugConsole UART code with a
// full 16550 driver featuring:
//   - configurable baud rate (9600/19200/38400/57600/115200), 8N1
//   - 16-byte FIFO enabled with 14-byte RX trigger
//   - DTR/RTS asserted
//   - interrupt-driven RX (IRQ4 -> vector 36) into a 1024-byte ring buffer
//   - interrupt-driven TX from a 256-byte ring buffer with polled fallback
//   - overrun / line-status error accounting
//
// The driver drives COM1 (port index 0, I/O base 0x3F8) which is the
// system console in NeutrinoOS and is registered as /dev/ttyS0 by the
// console abstraction layer.

using System.Runtime.InteropServices;
using ProtonOS.Threading;
using ProtonOS.Arch;

namespace ProtonOS.Platform;

/// <summary>
/// UART 16550 driver. One instance per COM port; Phase 2 uses COM1.
/// All methods are called from kernel context.
/// </summary>
public static unsafe class Uart16550
{
    // ==================== Register offsets ====================
    private const int REG_DATA = 0;         // RBR/THR (read/write)
    private const int REG_IER = 1;          // Interrupt Enable (DLAB=0)
    private const int REG_FCR = 2;          // FIFO Control (write)
    private const int REG_IIR = 2;          // Interrupt Identification (read)
    private const int REG_LCR = 3;          // Line Control
    private const int REG_MCR = 4;          // Modem Control
    private const int REG_LSR = 5;          // Line Status
    private const int REG_MSR = 6;          // Modem Status
    private const int REG_SCR = 7;          // Scratch
    private const int REG_DLL = 0;          // Divisor Latch Low (DLAB=1)
    private const int REG_DLH = 1;          // Divisor Latch High (DLAB=1)

    // ==================== LSR bits ====================
    private const byte LSR_DATA_READY = 0x01;
    private const byte LSR_OVERRUN = 0x02;
    private const byte LSR_PARITY = 0x04;
    private const byte LSR_FRAMING = 0x08;
    private const byte LSR_BREAK = 0x10;
    private const byte LSR_THRE = 0x20;     // Transmit Holding Register Empty

    // ==================== IER bits ====================
    private const byte IER_RX_AVAILABLE = 0x01;
    private const byte IER_THRE = 0x02;
    private const byte IER_RX_STATUS = 0x04;

    // ==================== Port configuration ====================
    // NOTE: plain functions instead of static arrays - the kernel runtime
    // has no LdTokenHelpers, so array type tokens must be avoided in
    // kernel-compiled static initialization.

    private static ushort PortBaseAt(int index)
    {
        switch (index)
        {
            case 0: return 0x3F8;   // COM1
            case 1: return 0x2F8;   // COM2
            case 2: return 0x3E8;   // COM3
            default: return 0x2E8;  // COM4
        }
    }

    private static int IrqVectorAt(int index)
    {
        // COM1/COM3 use IRQ4 (vector 36), COM2/COM4 use IRQ3 (vector 35)
        return (index == 0 || index == 2) ? 36 : 35;
    }

    /// <summary>COM1 port I/O base address.</summary>
    public const ushort Com1Base = 0x3F8;

    /// <summary>IRQ vector used by COM1 (IRQ4 -> vector 36, where 32 = IRQ0).</summary>
    public const int Com1IrqVector = 36;

    private const int RxRingSize = 1024;    // power of two
    private const int TxRingSize = 256;     // power of two

    // ==================== Ring buffers ====================
    private struct RingStore
    {
        public fixed byte Rx[RxRingSize];
        public fixed byte Tx[TxRingSize];
        public int RxHead;                  // producer: IRQ handler
        public int RxTail;                  // consumer: readers
        public int TxHead;                  // producer: writers
        public int TxTail;                  // consumer: IRQ handler / polled writer
    }

    private static RingStore _rings;

    // ==================== State ====================
    private static ushort _portBase;
    private static int _portIndex;
    private static bool _initialized;
    private static bool _interruptsEnabled;

    /// <summary>Number of RX bytes dropped because the ring buffer was full.</summary>
    public static volatile int RxOverruns;

    /// <summary>Number of RX overrun errors reported by the UART.</summary>
    public static volatile int HardwareOverruns;

    /// <summary>Number of RX parity/framing/break errors.</summary>
    public static volatile int LineStatusErrors;

    /// <summary>Baud rate the driver was initialized with.</summary>
    public static uint BaudRate { get; private set; }

    /// <summary>Whether the driver has been initialized.</summary>
    public static bool IsInitialized => _initialized;

    /// <summary>Whether interrupt-driven RX/TX is active.</summary>
    public static bool InterruptsEnabled => _interruptsEnabled;

    /// <summary>
    /// Optional RX byte consumer. When set, received bytes are delivered
    /// to the hook (the console line discipline) instead of the ring
    /// buffer. Cleared to null for the polled ring-buffer path.
    /// </summary>
    public static delegate* unmanaged<byte, void> RxConsumer;

    /// <summary>Sets the RX byte consumer (or null to use the ring buffer).</summary>
    public static void SetRxConsumer(delegate* unmanaged<byte, void> consumer)
        => RxConsumer = consumer;

    // ==================== Port I/O ====================
    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern void outb(ushort port, byte value);

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern byte inb(ushort port);

    private static void WriteReg(int reg, byte value) => outb((ushort)(_portBase + reg), value);
    private static byte ReadReg(int reg) => inb((ushort)(_portBase + reg));

    // ==================== Initialization ====================

    /// <summary>
    /// Initialize the UART at the given port index (0 = COM1) with the
    /// requested baud rate. Supported rates: 9600, 19200, 38400, 57600,
    /// 115200 (any divisor computed from a 1.8432 MHz base clock).
    /// </summary>
    public static bool Initialize(uint baudRate, int portIndex = 0)
    {
#if ARCH_ARM64
        // ARM64: the QEMU virt PL011 at 0x09000000 replaces COM1.
        _portIndex = 0;
        BaudRate = baudRate;
        _interruptsEnabled = false;
        _initialized = Pl011.Initialize(baudRate);
        return _initialized;
#else
        if (portIndex < 0 || portIndex > 3)
            return false;

        _portIndex = portIndex;
        _portBase = PortBaseAt(portIndex);
        BaudRate = baudRate;

        // Disable all UART interrupts while configuring
        WriteReg(REG_IER, 0x00);

        // Enable DLAB and set the baud rate divisor (115200 / baud)
        uint divisor = 115200 / baudRate;
        if (divisor == 0)
            divisor = 1;
        WriteReg(REG_LCR, 0x80);
        WriteReg(REG_DLL, (byte)(divisor & 0xFF));
        WriteReg(REG_DLH, (byte)((divisor >> 8) & 0xFF));

        // 8 data bits, no parity, 1 stop bit; DLAB off
        WriteReg(REG_LCR, 0x03);

        // Enable FIFO, clear both FIFOs, 14-byte RX trigger level
        WriteReg(REG_FCR, 0xC7);

        // DTR + RTS + OUT2 (OUT2 required for IRQ delivery through the PIC
        // on legacy systems; harmless with the IOAPIC)
        WriteReg(REG_MCR, 0x0B);

        // Mask modem-status interrupts (we do not consume them)
        WriteReg(REG_IER, 0x00);

        // Reset ring buffers
        fixed (RingStore* rings = &_rings)
        {
            rings->RxHead = 0;
            rings->RxTail = 0;
            rings->TxHead = 0;
            rings->TxTail = 0;
        }

        RxOverruns = 0;
        HardwareOverruns = 0;
        LineStatusErrors = 0;
        _interruptsEnabled = false;
        _initialized = true;
        return true;
#endif
    }

    /// <summary>
    /// Enable interrupt-driven RX (data available + line status) and the
    /// TX-empty interrupt when transmit data is queued. Also registers the
    /// IRQ handler the first time it is called. Must only be called after
    /// the scheduler and IDT are fully up (the console blocks on RX).
    /// </summary>
    public static void EnableInterrupts()
    {
        if (!_initialized || _interruptsEnabled)
            return;

        ProtonOS.Arch.Arch.RegisterHandler(IrqVectorAt(_portIndex), &SerialIrqHandler);
        byte ier = IER_RX_AVAILABLE | IER_RX_STATUS;
        if (TxPending())
            ier |= IER_THRE;

        WriteReg(REG_IER, ier);
        _interruptsEnabled = true;
    }

    /// <summary>Disable all UART interrupts (polled mode).</summary>
    public static void DisableInterrupts()
    {
        if (!_initialized)
            return;

        WriteReg(REG_IER, 0x00);
        _interruptsEnabled = false;
    }

    // ==================== TX ring helpers ====================

    private static bool TxPending()
    {
        fixed (RingStore* rings = &_rings)
            return rings->TxHead != rings->TxTail;
    }

    private static bool TryEnqueueTx(byte b)
    {
        fixed (RingStore* rings = &_rings)
        {
            int next = (rings->TxHead + 1) & (TxRingSize - 1);
            if (next == rings->TxTail)
                return false;
            rings->Tx[(nuint)rings->TxHead] = b;
            rings->TxHead = next;
            return true;
        }
    }

    private static bool TryDequeueTx(out byte b)
    {
        fixed (RingStore* rings = &_rings)
        {
            if (rings->TxHead == rings->TxTail)
            {
                b = 0;
                return false;
            }
            b = rings->Tx[(nuint)rings->TxTail];
            rings->TxTail = (rings->TxTail + 1) & (TxRingSize - 1);
            return true;
        }
    }

    // ==================== RX ring helpers ====================

    private static void EnqueueRx(byte b)
    {
        fixed (RingStore* rings = &_rings)
        {
            int next = (rings->RxHead + 1) & (RxRingSize - 1);
            if (next == rings->RxTail)
            {
                // Ring full - drop the byte but keep draining the UART FIFO,
                // otherwise the FIFO stalls and delays interrupt processing.
                RxOverruns++;
                return;
            }
            rings->Rx[(nuint)rings->RxHead] = b;
            rings->RxHead = next;
        }
    }

    private static bool TryDequeueRx(out byte b)
    {
        fixed (RingStore* rings = &_rings)
        {
            if (rings->RxHead == rings->RxTail)
            {
                b = 0;
                return false;
            }
            b = rings->Rx[(nuint)rings->RxTail];
            rings->RxTail = (rings->RxTail + 1) & (RxRingSize - 1);
            return true;
        }
    }

    // ==================== Interrupt handler ====================

    /// <summary>
    /// IRQ4 handler. Reads the Interrupt Identification Register and
    /// services all pending causes in priority order. Must send the APIC
    /// EOI (done at the end).
    /// </summary>
    private static void SerialIrqHandler(InterruptFrame* frame)
    {
        _ = frame;
        HandleInterruptBody();
        APIC.SendEoi();
    }

    /// <summary>
    /// The IRQ handling body without the EOI: loops until the UART reports
    /// no pending interrupt. Used both by the legacy handler above and by
    /// the framework UART driver (whose IDriverServices thunk sends the EOI
    /// after this returns).
    /// </summary>
    public static void HandleInterruptBody()
    {
        // Loop until the UART reports "no interrupt pending" (IIR bit 0 set)
        while (true)
        {
            byte iir = ReadReg(REG_IIR);
            if ((iir & 0x01) != 0)
                break;

            int cause = (iir >> 1) & 0x07;
            switch (cause)
            {
                case 0x02:  // Received data available
                case 0x06:  // Character timeout (FIFO has data below trigger)
                    DrainRxFifo();
                    break;

                case 0x01:  // Transmit holding register empty
                    if (!TryDequeueTx(out byte tx))
                    {
                        // TX ring empty - disable THRE interrupts
                        DisableThreInterrupt();
                    }
                    else
                    {
                        WriteReg(REG_DATA, tx);
                    }
                    break;

                case 0x03:  // Receiver line status error
                    {
                        byte lsr = ReadReg(REG_LSR);
                        if ((lsr & LSR_OVERRUN) != 0)
                            HardwareOverruns++;
                        if ((lsr & (LSR_PARITY | LSR_FRAMING | LSR_BREAK)) != 0)
                            LineStatusErrors++;
                        // Reading LSR above clears the cause; also drain any data
                        if ((lsr & LSR_DATA_READY) != 0)
                            DrainRxFifo();
                    }
                    break;

                default:
                    // Modem status (masked) or spurious - nothing to do
                    break;
            }
        }
    }

    private static void DrainRxFifo()
    {
        while ((ReadReg(REG_LSR) & LSR_DATA_READY) != 0)
        {
            byte b = ReadReg(REG_DATA);
            if (RxConsumer != null)
                RxConsumer(b);
            else
                EnqueueRx(b);
        }
    }

    private static void DisableThreInterrupt()
    {
        byte ier = ReadReg(REG_IER);
        ier &= unchecked((byte)~IER_THRE);
        WriteReg(REG_IER, ier);
    }

    private static void EnableThreInterrupt()
    {
        byte ier = ReadReg(REG_IER);
        ier |= IER_THRE;
        WriteReg(REG_IER, ier);
    }

    // ==================== Public write API ====================

    /// <summary>
    /// Write a single byte. Uses the interrupt-driven TX ring when
    /// interrupts are enabled (blocking with scheduler yields when the ring
    /// is full); falls back to polled THRE waiting during early boot.
    /// </summary>
    public static void WriteByte(byte b)
    {
#if ARCH_ARM64
        if (!_initialized)
            return;
        Pl011.WriteByte(b);
        return;
#else
        if (!_initialized)
            return;

        if (_interruptsEnabled)
        {
            int spins = 0;
            while (!TryEnqueueTx(b))
            {
                // Ring full: make sure the drain interrupt is armed (a
                // previous enqueue may have raced a busy transmitter before
                // enabling THRE), then wait for the IRQ handler to drain it.
                EnableThreInterrupt();
                Scheduler.Yield();
                CPU.Pause();

                if (++spins > 100_000_000)
                {
                    // Give up (should not happen) - fall through to polled write
                    PolledWriteByte(b);
                    return;
                }
            }

            // Arm the transmit-empty interrupt unconditionally. Checking
            // LSR_THRE here races with a busy transmitter: a burst queued
            // while THRE is momentarily clear would leave the interrupt
            // disabled, the ring would never drain, and once full WriteByte
            // would spin forever.
            EnableThreInterrupt();
            return;
        }

        PolledWriteByte(b);
#endif
    }

    private static void PolledWriteByte(byte b)
    {
        while ((ReadReg(REG_LSR) & LSR_THRE) == 0) { }
        WriteReg(REG_DATA, b);
    }

    /// <summary>
    /// Non-blocking single-byte write for use from interrupt context (e.g.
    /// line-discipline echo, which runs inside the RX interrupt handler).
    /// Enqueues when the TX ring has space and lets the THRE interrupt drain
    /// it; drops the byte when the ring is full.  The blocking WriteByte
    /// must never be used from an ISR: the THRE interrupt cannot run while
    /// the ISR is executing, so a full ring would never drain and the ISR
    /// would spin forever.
    /// </summary>
    public static bool TryWriteByte(byte b)
    {
#if ARCH_ARM64
        if (!_initialized)
            return true;
        Pl011.WriteByte(b);
        return true;
#else
        if (!_initialized)
            return true;

        if (!_interruptsEnabled)
        {
            PolledWriteByte(b);
            return true;
        }

        if (!TryEnqueueTx(b))
            return false;
        EnableThreInterrupt();
        return true;
#endif
    }

    /// <summary>
    /// Write a string. Newlines are translated to CRLF for terminals.
    /// </summary>
    public static void Write(string s)
    {
        bool lastWasCr = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\n' && !lastWasCr)
                WriteByte(0x0D);
            WriteByte((byte)(c < 256 ? c : '?'));
            lastWasCr = c == '\r';
        }
    }

    // ==================== Public read API ====================

    /// <summary>Number of bytes available in the RX ring buffer.</summary>
    public static int BytesAvailable
    {
        get
        {
            fixed (RingStore* rings = &_rings)
                return (rings->RxHead - rings->RxTail) & (RxRingSize - 1);
        }
    }

    /// <summary>Non-blocking read of one byte from the RX ring buffer.</summary>
    public static bool TryReadByte(out byte b)
    {
#if ARCH_ARM64
        return Pl011.TryReadByte(out b);
#else
        if (_interruptsEnabled)
            return TryDequeueRx(out b);

        // Polled mode (early boot): read directly from the UART
        if ((ReadReg(REG_LSR) & LSR_DATA_READY) != 0)
        {
            b = ReadReg(REG_DATA);
            return true;
        }
        b = 0;
        return false;
#endif
    }

    /// <summary>
    /// Blocking read of one byte. Waits for the RX interrupt (via CPU.Halt)
    /// when interrupts are enabled, otherwise polls the UART.
    /// </summary>
    public static byte ReadByte()
    {
        byte b;
        while (!TryReadByte(out b))
        {
            if (_interruptsEnabled)
                CPU.Halt();     // wake on the next timer/RX interrupt
            else
                CPU.Pause();
        }
        return b;
    }
}
