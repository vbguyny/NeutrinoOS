// ProtonOS Kernel - PCI bus enumerator for the driver framework.
//
// Walks the PCI bus (already scanned by ProtonOS.Platform.PCI) and populates
// the kernel device tree with device nodes: identity, location, class and
// resources (I/O port ranges, MMIO windows, IRQ line). BARs are sized by the
// standard write-all-ones/restore probe.

using System;
using NeutrinoOS.Drivers;
using ProtonOS.Platform;

namespace ProtonOS.Drivers;

/// <summary>Enumerates PCI devices into the kernel device tree.</summary>
public static unsafe class PciBusEnumerator
{
    // PCI config space offsets.
    private const byte OffsetCommand = 0x04;
    private const byte OffsetBar0 = 0x10;
    private const byte OffsetInterruptLine = 0x3C;

    /// <summary>
    /// Enumerate all detected PCI devices under the tree root.
    /// Returns the number of devices added.
    /// </summary>
    public static int Enumerate()
    {
        KernelDeviceTree tree = KernelDeviceTree.Instance;
        int added = 0;

        int count = PCI.DeviceCount;
        for (int i = 0; i < count; i++)
        {
            PciDevice* dev = PCI.GetDevice(i);
            if (dev == null)
                continue;

            byte bus = dev->Bus;
            byte device = dev->Device;
            byte function = dev->Function;

            DeviceResource[] resources = BuildResources(bus, device, function);

            DeviceClass deviceClass = MapClass(dev->BaseClass, dev->SubClass);
            uint classCode = (uint)((dev->BaseClass << 16) | (dev->SubClass << 8) | dev->ProgIF);
            uint address = (uint)((bus << 8) | ((device & 0x1F) << 3) | (function & 0x7));

            string segment = "pci/" + Hex2(bus) + ":" + Hex2(device) + "." + Hex1(function);

            DeviceInfo node = tree.Add(
                0,
                "pci",
                address,
                dev->VendorId,
                dev->DeviceId,
                deviceClass,
                classCode,
                segment,
                resources);
            if (node == null)
                break;
            added++;
        }

        return added;
    }

    /// <summary>
    /// Build the resource list for a PCI function: BAR0-5 (I/O or MMIO with
    /// real sizes) plus the interrupt line when assigned.
    /// </summary>
    private static DeviceResource[] BuildResources(byte bus, byte device, byte function)
    {
        // Up to 6 BARs + 1 IRQ.
        DeviceResource[] temp = new DeviceResource[7];
        int n = 0;

        for (int bar = 0; bar < 6; bar++)
        {
            byte offset = (byte)(OffsetBar0 + bar * 4);
            uint value = PCI.ReadConfig32(bus, device, function, offset);
            if (value == 0 || value == 0xFFFFFFFF)
                continue;

            if ((value & 0x1) != 0)
            {
                // I/O space BAR.
                ulong size = SizeIoBar(bus, device, function, offset, value);
                temp[n++] = new DeviceResource(DeviceResourceKind.IoPort, value & 0xFFFFFFFCu, size);
            }
            else
            {
                // Memory space BAR; 64-bit BARs consume the next slot.
                int type = (int)((value >> 1) & 0x3);
                if (type == 0x2)
                {
                    // 64-bit BAR: upper 32 bits in the next slot.
                    uint upper = PCI.ReadConfig32(bus, device, function, (byte)(offset + 4));
                    ulong baseAddr = value & 0xFFFFFFF0u;
                    baseAddr |= ((ulong)upper) << 32;
                    ulong size = SizeMemBar64(bus, device, function, offset);
                    temp[n++] = new DeviceResource(DeviceResourceKind.Mmio, baseAddr, size);
                    bar++; // upper half consumed
                }
                else
                {
                    ulong size = SizeMemBar32(bus, device, function, offset, value);
                    temp[n++] = new DeviceResource(DeviceResourceKind.Mmio, (ulong)(value & 0xFFFFFFF0u), size);
                }
            }
        }

        byte irqLine = PCI.ReadConfig8(bus, device, function, OffsetInterruptLine);
        if (irqLine != 0 && irqLine != 0xFF)
        {
            temp[n++] = new DeviceResource(DeviceResourceKind.Irq, irqLine, 0);
        }

        if (n == 0)
            return new DeviceResource[0];

        DeviceResource[] result = new DeviceResource[n];
        for (int i = 0; i < n; i++)
            result[i] = temp[i];
        return result;
    }

    /// <summary>Size an I/O BAR with the write-all-ones probe.</summary>
    private static ulong SizeIoBar(byte bus, byte device, byte function, byte offset, uint original)
    {
        uint savedCommand = PCI.ReadConfig32(bus, device, function, OffsetCommand);

        // Disable I/O decoding while probing.
        PCI.WriteConfig32(bus, device, function, OffsetCommand, (savedCommand & 0xFFFFFFFCu) | 0x1u);
        PCI.WriteConfig32(bus, device, function, offset, 0xFFFFFFFF);
        uint sized = PCI.ReadConfig32(bus, device, function, offset);
        PCI.WriteConfig32(bus, device, function, offset, original);
        PCI.WriteConfig32(bus, device, function, OffsetCommand, savedCommand);

        uint mask = sized & 0xFFFFFFFCu;
        if (mask == 0)
            return 0;
        return (~mask + 1u) & 0xFFFF;
    }

    /// <summary>Size a 32-bit memory BAR with the write-all-ones probe.</summary>
    private static ulong SizeMemBar32(byte bus, byte device, byte function, byte offset, uint original)
    {
        uint savedCommand = PCI.ReadConfig32(bus, device, function, OffsetCommand);
        PCI.WriteConfig32(bus, device, function, OffsetCommand, savedCommand & 0xFFFFFFFCu);
        PCI.WriteConfig32(bus, device, function, offset, 0xFFFFFFFF);
        uint sized = PCI.ReadConfig32(bus, device, function, offset);
        PCI.WriteConfig32(bus, device, function, offset, original);
        PCI.WriteConfig32(bus, device, function, OffsetCommand, savedCommand);

        uint mask = sized & 0xFFFFFFF0u;
        if (mask == 0)
            return 0;
        return (~mask + 1u);
    }

    /// <summary>Size a 64-bit memory BAR (upper half in the next slot).</summary>
    private static ulong SizeMemBar64(byte bus, byte device, byte function, byte offset)
    {
        uint origLow = PCI.ReadConfig32(bus, device, function, offset);
        uint origHigh = PCI.ReadConfig32(bus, device, function, (byte)(offset + 4));
        uint savedCommand = PCI.ReadConfig32(bus, device, function, OffsetCommand);

        PCI.WriteConfig32(bus, device, function, OffsetCommand, savedCommand & 0xFFFFFFFCu);
        PCI.WriteConfig32(bus, device, function, offset, 0xFFFFFFF0);
        PCI.WriteConfig32(bus, device, function, (byte)(offset + 4), 0xFFFFFFFF);
        uint sizedLow = PCI.ReadConfig32(bus, device, function, offset);
        uint sizedHigh = PCI.ReadConfig32(bus, device, function, (byte)(offset + 4));
        PCI.WriteConfig32(bus, device, function, offset, origLow);
        PCI.WriteConfig32(bus, device, function, (byte)(offset + 4), origHigh);
        PCI.WriteConfig32(bus, device, function, OffsetCommand, savedCommand);

        ulong mask = sizedLow & 0xFFFFFFF0u;
        mask |= ((ulong)sizedHigh) << 32;
        if (mask == 0)
            return 0;
        return (~mask) + 1UL;
    }

    /// <summary>Map PCI base/sub class to a device class.</summary>
    public static DeviceClass MapClass(byte baseClass, byte subClass)
    {
        switch (baseClass)
        {
            case 0x01: // Mass storage
                return DeviceClass.Storage;
            case 0x02: // Network
                return DeviceClass.Network;
            case 0x03: // Display
                return DeviceClass.Display;
            case 0x04: // Multimedia
                return DeviceClass.Audio;
            case 0x06: // Bridge
                return DeviceClass.Bridge;
            case 0x0C: // Serial bus
                return subClass == 0x03 ? DeviceClass.Input : DeviceClass.Serial;
            default:
                return DeviceClass.Unknown;
        }
    }

    private static char HexDigit(int v)
    {
        v &= 0xF;
        return v < 10 ? (char)('0' + v) : (char)('A' + (v - 10));
    }

    private static string Hex2(byte v)
    {
        return new string(new char[] { HexDigit(v >> 4), HexDigit(v) });
    }

    private static string Hex1(byte v)
    {
        return new string(new char[] { HexDigit(v) });
    }
}
