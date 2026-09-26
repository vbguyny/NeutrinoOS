// NeutrinoOS kernel - xHCI host controller driver (Phase 9 Task 1).
//
// Implements the eXtensible Host Controller Interface for USB 3.x
// controllers (QEMU qemu-xhci, NEC/Renesas, Intel and AMD xHCI):
//
//   - capability/operational/runtime/doorbell register interface (BAR0)
//   - command ring, event ring (ERST), transfer rings (one per endpoint)
//   - device slots: enable slot, address device, evaluate context,
//     configure endpoint, reset/stop endpoint, disable slot
//   - port management: power, reset, speed detection, connect/disconnect
//     change bits (hot-plug detection; the controller is polled)
//   - control, bulk and interrupt transfers (synchronous, polled
//     completion - see the polling note below)
//
// Polling note: xHCI interrupts are MSI/MSI-X only (the interface has no
// legacy INTx mode). MSI setup is not wired through the kernel PCI layer
// yet, so the controller runs in polled mode: the event ring is drained
// during every synchronous transfer wait and by the hot-plug poll hook
// (ShellMain.IdlePump, same cadence as the Phase 8 PCIe hot-plug scan).
// All registers/rings follow the xHCI 1.1 specification; transfer and
// command data structures live in physically-addressed DMA pages
// (PageAllocator + physmap window), as the controller walks them by
// physical address.

using System;
using System.Runtime.InteropServices;
using ProtonOS.Arch;
using ProtonOS.Memory;
using ProtonOS.Platform;

namespace ProtonOS.Usb.Xhci;

// ============================================================================
// TRB (transfer request block, 16 bytes)
// ============================================================================

/// <summary>TRB types (dword3 bits 15:10).</summary>
public static class XhciTrbType
{
    public const uint Normal = 1;
    public const uint SetupStage = 2;
    public const uint DataStage = 3;
    public const uint StatusStage = 4;
    public const uint Link = 6;
    public const uint EnableSlotCmd = 9;
    public const uint DisableSlotCmd = 10;
    public const uint AddressDeviceCmd = 11;
    public const uint ConfigureEndpointCmd = 12;
    public const uint EvaluateContextCmd = 13;
    public const uint ResetEndpointCmd = 14;
    public const uint StopEndpointCmd = 15;
    public const uint SetTrDequeueCmd = 16;
    public const uint ResetDeviceCmd = 17;
    public const uint TransferEvent = 32;
    public const uint CommandCompletionEvent = 33;
    public const uint PortStatusChangeEvent = 34;
}

/// <summary>xHCI completion codes (TRB status field bits 31:24 for events).</summary>
public static class XhciCompletion
{
    public const byte Success = 1;
    public const byte ShortPacket = 13;
    public const byte StallError = 6;
    public const byte BabbleDetected = 3;
    public const byte UsbTransactionError = 4;
    public const byte TrbError = 5;
    public const byte NoSlotsAvailable = 9;
    public const byte SlotNotEnabled = 10;
    public const byte BandwidthError = 11;
    public const byte ContextStateError = 19;
    public const byte CommandRingStopped = 24;
    public const byte CommandAborted = 25;
    public const byte Stopped = 26;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct XhciTrb
{
    public uint D0;
    public uint D1;
    public uint D2;
    public uint D3;

    public ulong Param => (ulong)D0 | ((ulong)D1 << 32);
    public uint Status => D2;
    public byte CompletionCode => (byte)(D2 >> 24);
    public uint Cycle => D3 & 1;
    public uint Type => (D3 >> 10) & 0x3F;
    public uint SlotId => (D3 >> 24) & 0xFF;

    public static unsafe void Write(
        XhciTrb* trb, ulong param, uint status, uint type, bool cycle, uint slotId, uint epTarget, bool ioc, bool idt)
    {
        trb->D0 = (uint)param;
        trb->D1 = (uint)(param >> 32);
        trb->D2 = status;
        uint d3 = (type & 0x3F) << 10;
        if (ioc)
            d3 |= 1u << 5;
        if (idt)
            d3 |= 1u << 6;
        d3 |= (slotId & 0xFF) << 24;
        if (cycle)
            d3 |= 1;
        _ = epTarget;
        trb->D3 = d3;
    }

    public static unsafe void Write2(
        XhciTrb* trb, ulong param, uint status, uint type, bool cycle)
    {
        trb->D0 = (uint)param;
        trb->D1 = (uint)(param >> 32);
        trb->D2 = status;
        uint d3 = (type & 0x3F) << 10;
        if (cycle)
            d3 |= 1;
        trb->D3 = d3;
    }
}

// ============================================================================
// Register offsets (BAR0)
// ============================================================================

public static class XhciReg
{
    // Capability registers
    public const uint CAPLENGTH = 0x00;    // u8
    public const uint HCIVERSION = 0x02;   // u16
    public const uint HCSPARAMS1 = 0x04;   // u32 (MaxSlots[7:0], MaxIntrs[18:8], MaxPorts[31:24])
    public const uint HCSPARAMS2 = 0x08;   // u32 (scratchpad buffers [31:27][25:21])
    public const uint HCSPARAMS3 = 0x0C;
    public const uint HCCPARAMS1 = 0x10;   // u32 (AC64 bit0, CSZ bit2, xECP [31:16])
    public const uint DBOFF = 0x14;        // u32
    public const uint RTSOFF = 0x18;       // u32
    public const uint HCCPARAMS2 = 0x1C;

    // Operational registers (at CAPLENGTH)
    public const uint USBCMD = 0x00;       // u32
    public const uint USBSTS = 0x04;       // u32
    public const uint PAGESIZE = 0x08;     // u32
    public const uint DNCTRL = 0x14;       // u32
    public const uint CRCR = 0x18;         // u64
    public const uint DCBAAP = 0x30;       // u64
    public const uint CONFIG = 0x38;       // u32
    public const uint PORTSC_BASE = 0x400; // + 0x10 per port

    // Runtime (at RTSOFF). Offsets 0x00..0x1F are MFINDEX/reserved;
    // interrupter 0's register set starts at 0x20 (xHCI spec 5.4.1 +
    // QEMU hcd-xhci.c: v = (reg - 0x20) / 0x20).
    public const uint IMAN = 0x20;
    public const uint IMOD = 0x24;
    public const uint ERSTSZ = 0x28;
    public const uint ERSTBA = 0x30;       // u64
    public const uint ERDP = 0x38;         // u64
}

public static class XhciPortsc
{
    public const uint CCS = 1u << 0;       // current connect status (RO)
    public const uint PED = 1u << 1;       // port enabled/disabled (RW1CS)
    public const uint OCA = 1u << 3;
    public const uint PR = 1u << 4;        // port reset
    public const uint PLS = 0xFu << 5;     // port link state
    public const uint PLS_SHIFT = 5;
    public const uint PP = 1u << 9;        // port power
    public const uint Speed = 0xFu << 10;
    public const uint SPEED_SHIFT = 10;
    public const uint LWS = 1u << 16;
    public const uint CSC = 1u << 17;      // connect status change (RW1C)
    public const uint PEC = 1u << 18;      // port enabled change
    public const uint WRC = 1u << 19;      // warm port reset change
    public const uint PRC = 1u << 21;      // port reset change
    public const uint PLC = 1u << 22;      // port link state change
    public const uint CEC = 1u << 23;      // config error change
    public const uint WPR = 1u << 31;      // warm port reset
}

/// <summary>USB speed from PORTSC.[3:0] (same encoding as device slot speed).</summary>
public enum UsbSpeed : uint
{
    Unknown = 0,
    Full = 1,
    Low = 2,
    High = 3,
    Super = 4,
    SuperPlus = 5,
}

/// <summary>Controller-level errors surfaced to callers.</summary>
public enum XhciStatus
{
    Ok = 0,
    ShortPacket = 1,
    Stall = 2,
    Timeout = 3,
    TrbError = 4,
    Babble = 5,
    ContextError = 6,
    NoSlot = 7,
    NotConnected = 8,
    HardwareFault = 9,
    Unsupported = 10,
}

/// <summary>Synchronous transfer result.</summary>
public struct XhciTransferResult
{
    public XhciStatus Status;
    public int BytesTransferred;
    public byte CompletionCode;

    public bool Ok => Status == XhciStatus.Ok || Status == XhciStatus.ShortPacket;
}

/// <summary>One completed transfer event (queued by the event drain).</summary>
public struct XhciXferCompletion
{
    public byte SlotId;
    public byte Dci;
    public byte Code;
    public int Transferred;
}

/// <summary>DMA frame: physical + virtual view of a page-aligned buffer.</summary>
public unsafe struct UsbDma
{
    public ulong Physical;
    public byte* Virtual;
    public uint Size;

    public static UsbDma Allocate(uint size)
    {
        uint pages = (size + 4095) / 4096;
        ulong phys = PageAllocator.AllocatePages(pages);
        var dma = new UsbDma();
        if (phys == 0)
            return dma;
        dma.Physical = phys;
        dma.Virtual = (byte*)VirtualMemory.PhysToVirt(phys);
        dma.Size = size;
        for (uint i = 0; i < pages * 4096; i++)
            dma.Virtual[i] = 0;
        return dma;
    }

    public static unsafe void Free(ref UsbDma dma)
    {
        if (dma.Physical != 0)
        {
            uint pages = (dma.Size + 4095) / 4096;
            PageAllocator.FreePageRange(dma.Physical, pages);
            dma.Physical = 0;
            dma.Virtual = null;
        }
    }
}
