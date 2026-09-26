// NeutrinoOS Kernel - ICH9 (q35) ACPI PCI hotplug, guest side (Phase 8).
//
// On QEMU q35, the chipset hotplug controller (acpi-pci-hotplug, ICH9 PM
// region at port 0xCC0) owns PCIe root-port slots: for cold-plugged
// bridges QEMU overwrites the port's native hotplug handler with the ACPI
// controller, so `device_add`/`device_del` are surfaced through the ACPI
// registers instead of the PCIe Slot Status bits:
//
//   +0x00 UP    arrival bitmask     (bit S set when slot S got a device;
//                                    a read clears it)
//   +0x04 DOWN  eject-request mask  (bit S set on device_del; the guest
//                                    must acknowledge by writing EJ)
//   +0x08 EJ    write bitmask to complete the eject (QEMU then unplugs
//               the device; the DOWN bit clears)
//   +0x0C RMV   removable-slots mask
//   +0x10 SEL   bus selector (bsel) - selects which hotplug bus the
//               other registers refer to (one bsel per hotplug bus)
//   +0x14 AIDX  acpi-index query (unused)
//
// The poll reads DOWN for every bsel and writes EJ with the pending mask;
// the device then physically leaves its bus, and PcieHotplug's per-port
// bus rescan sees the absence, unbinds the driver and marks the tree node
// removed. Reading UP keeps the arrival latch clear (arrivals themselves
// are picked up by the same bus rescan).
//
// The 0xCC0 base is QEMU's fixed ICH9 layout (q35). The availability
// probe reads the selector back: an unmapped port reads 0xFFFFFFFF, in
// which case the whole class is a no-op on that machine.

using System;
using ProtonOS.Platform;

namespace ProtonOS.Drivers;

/// <summary>ICH9 ACPI PCI hotplug guest driver (see file header).</summary>
public static class AcpiPciHotplug
{
    private const ushort Base = 0x0CC0;

    private const ushort RegUp = 0x00;
    private const ushort RegDown = 0x04;
    private const ushort RegEj = 0x08;
    private const ushort RegSel = 0x10;

    private const int MaxBsels = 8;          // slots/buses we scan
    private const uint NoDevice = 0xFFFFFFFF;

    private static bool _initialized;
    private static bool _available;

    /// <summary>True when the ICH9 hotplug controller answered the probe.</summary>
    public static bool Available => _available;

    /// <summary>
    /// Probe the ICH9 hotplug controller. Idempotent; safe on machines
    /// without it (all registers read back unmapped). No-op on ARM64:
    /// port I/O and the ICH9 PM block are x64 (q35) only.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;

#if !ARCH_ARM64
        ProtonOS.Arch.CPU.OutDword((ushort)(Base + RegSel), 0);
        uint sel = ProtonOS.Arch.CPU.InDword((ushort)(Base + RegSel));
        _available = sel == 0;

        DebugConsole.Write("[hotplug] acpi-pci-hotplug at 0x");
        DebugConsole.WriteDecimal(Base);
        DebugConsole.WriteLine(_available ? ": available" : ": not present");
#endif
    }

    /// <summary>
    /// One poll pass: complete pending ejects on every selected bus and
    /// clear arrival latches. Called from PcieHotplug.Poll (throttled).
    /// No-op on ARM64.
    /// </summary>
    public static void Poll()
    {
#if !ARCH_ARM64
        if (!_available)
            return;

        for (byte bsel = 0; bsel < MaxBsels; bsel++)
        {
            ProtonOS.Arch.CPU.OutDword((ushort)(Base + RegSel), bsel);

            uint down = ProtonOS.Arch.CPU.InDword((ushort)(Base + RegDown));
            if (down != 0 && down != NoDevice)
            {
                // Acknowledge the eject request: QEMU unplugs the device
                // and clears the DOWN bit.
                DebugConsole.Write("[hotplug] acpi eject bsel=");
                DebugConsole.WriteDecimal(bsel);
                DebugConsole.Write(" slots=0x");
                DebugConsole.Write(Hex4((ushort)down));
                DebugConsole.WriteLine();
                ProtonOS.Arch.CPU.OutDword((ushort)(Base + RegEj), down);
            }

            uint up = ProtonOS.Arch.CPU.InDword((ushort)(Base + RegUp));
            if (up != 0 && up != NoDevice)
            {
                // Arrival latch; the device tree update happens in the
                // bus rescan. The read above already cleared the latch.
                DebugConsole.Write("[hotplug] acpi arrival bsel=");
                DebugConsole.WriteDecimal(bsel);
                DebugConsole.Write(" slots=0x");
                DebugConsole.Write(Hex4((ushort)up));
                DebugConsole.WriteLine();
            }
        }

        ProtonOS.Arch.CPU.OutDword((ushort)(Base + RegSel), 0);
#endif
    }

    private static char HexDigit(int v)
    {
        v &= 0xF;
        return v < 10 ? (char)('0' + v) : (char)('A' + (v - 10));
    }

    private static string Hex4(ushort v)
    {
        return new string(new char[]
        {
            HexDigit(v >> 12), HexDigit(v >> 8), HexDigit(v >> 4), HexDigit(v)
        });
    }
}
