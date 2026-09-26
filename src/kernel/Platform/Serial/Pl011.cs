// NeutrinoOS kernel - PL011 UART driver (ARM64 console, Phase 8 Task 3).
//
// ARM64 replacement for the 16550 console: QEMU 'virt' exposes a PL011
// at 0x09000000, which carries the serial console on ARM64 (registered
// as /dev/ttyS0 by the same console stack as the x64 UART). Polled TX/RX
// in this increment; GIC interrupt wiring arrives with the interrupt
// controller pass.

#if ARCH_ARM64

namespace ProtonOS.Platform;

/// <summary>PL011 UART driver for the ARM64 console (QEMU virt 0x09000000).</summary>
public static unsafe class Pl011
{
    /// <summary>PL011 base address on QEMU 'virt'.</summary>
    public const ulong Base = 0x09000000;

    // Register offsets
    private const int REG_DR = 0x00;      // data
    private const int REG_FR = 0x18;      // flag
    private const int REG_IBRD = 0x24;    // integer baud divisor
    private const int REG_FBRD = 0x28;    // fractional baud divisor
    private const int REG_LCRH = 0x2C;    // line control
    private const int REG_CR = 0x30;      // control
    private const int REG_IMSC = 0x38;    // interrupt mask
    private const int REG_MIS = 0x40;     // masked interrupt status
    private const int REG_ICR = 0x44;     // interrupt clear

    private const uint FR_TXFF = 1u << 5; // transmit FIFO full
    private const uint FR_RXFE = 1u << 4; // receive FIFO empty
    private const uint INTR_RX = 1u << 4; // receive interrupt (IMSC bit)
    private const uint INTR_ALL = 0x7FF;

    private static bool _initialized;

    /// <summary>Initialize the PL011 for 115200-ish 8N1 with FIFOs.</summary>
    public static bool Initialize(uint baudRate)
    {
        _ = baudRate;   // QEMU's PL011 clock is fixed; divisors below are nominal
        byte* uart = (byte*)Base;

        *(uint*)(uart + REG_CR) = 0;          // disable while configuring
        *(uint*)(uart + REG_ICR) = 0x7FF;     // clear all interrupts
        *(uint*)(uart + REG_IMSC) = 0;        // mask all interrupts (polled mode)
        *(uint*)(uart + REG_IBRD) = 13;       // 24MHz / (16*13.02) = 115200
        *(uint*)(uart + REG_FBRD) = 1;
        *(uint*)(uart + REG_LCRH) = 0x70;     // 8 bits, no parity, 1 stop, FIFO on
        *(uint*)(uart + REG_CR) = 0x301;      // UARTEN | TXE | RXE

        _initialized = true;
        return true;
    }

    /// <summary>Single-byte transmit (polls TXFF).</summary>
    public static void WriteByte(byte b)
    {
        byte* uart = (byte*)Base;
        int guard = 0;
        while ((*(uint*)(uart + REG_FR) & FR_TXFF) != 0)
        {
            if (++guard > 10_000_000)
                return;         // never spin forever on a wedged UART
        }
        *(byte*)(uart + REG_DR) = b;
    }

    /// <summary>Non-blocking receive (polls RXFE).</summary>
    public static bool TryReadByte(out byte b)
    {
        byte* uart = (byte*)Base;
        if ((*(uint*)(uart + REG_FR) & FR_RXFE) != 0)
        {
            b = 0;
            return false;
        }
        b = (byte)(*(uint*)(uart + REG_DR) & 0xFF);
        return true;
    }

    /// <summary>Whether RX data is waiting.</summary>
    public static bool IsDataAvailable()
    {
        byte* uart = (byte*)Base;
        return (*(uint*)(uart + REG_FR) & FR_RXFE) == 0;
    }

    /// <summary>
    /// Enable the receive interrupt (RXIM). The GIC SPI wiring is the
    /// caller's responsibility (see Uart16550.EnableInterrupts).
    /// </summary>
    public static void EnableRxInterrupt()
    {
        byte* uart = (byte*)Base;
        *(uint*)(uart + REG_ICR) = INTR_ALL;   // clear stale conditions
        *(uint*)(uart + REG_IMSC) = INTR_RX;
    }

    /// <summary>Mask all PL011 interrupts (polled mode).</summary>
    public static void DisableInterrupts()
    {
        byte* uart = (byte*)Base;
        *(uint*)(uart + REG_IMSC) = 0;
    }

    /// <summary>Whether an RX interrupt is currently asserted.</summary>
    public static bool RxInterruptPending
    {
        get
        {
            byte* uart = (byte*)Base;
            return (*(uint*)(uart + REG_MIS) & INTR_RX) != 0;
        }
    }

    /// <summary>Whether the driver has been initialized.</summary>
    public static bool IsInitialized => _initialized;
}

#endif
