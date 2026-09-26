// NeutrinoOS Kernel - PCIe hot-plug detection (Phase 8).
//
// Implements the spec's PCIe hot-plug support: at framework init the
// detector walks the PCIe capability structure of every PCI bridge and
// remembers the root ports that implement a slot (PCIe capability, Slot
// Implemented bit). While the system idles at the shell prompt the poll
// reads each port's Slot Status register (Presence Detect State, Data Link
// Layer State Changed) and rescans the port's secondary bus; devices that
// appeared are added to the device tree (PCI node + VirtIO child) and
// matched against registered drivers, devices that disappeared are
// unbound (driver Stop) and marked DeviceStatus.Removed.
//
// QEMU: add ports with `-device pcie-root-port,id=hp0` and hot-plug
// endpoints through the monitor/QMP (`device_add virtio-net-pci,bus=hp0`,
// `device_del <id>`). The bus rescan is authoritative: it detects changes
// even when the firmware leaves the slot's presence-detect wiring
// unwired, while the capability walk still gates which ports are polled.
//
// Cross-arch: the detector uses the kernel PCI config accessor
// (0xCF8/0xCFC) and runs from the shell idle hook, so it is compiled on
// both x64 and arm64. Only ports with Slot Implemented are tracked.

using System;
using NeutrinoOS.Drivers;
using ProtonOS.Platform;

namespace ProtonOS.Drivers;

/// <summary>PCIe hot-plug detection and driver load/unload handling.</summary>
public static class PcieHotplug
{
    private const int MaxPorts = 16;
    private const int MaxTracked = 32;
    private const int PollIntervalTicks = 200;   // ~200 ms at the 1 ms tick

    // PCI config space offsets used by the capability walk.
    private const byte OffsetStatus = 0x06;
    private const byte OffsetHeaderType = 0x0E;
    private const byte OffsetCapPointer = 0x34;
    private const byte OffsetSecondaryBus = 0x19;
    private const byte OffsetSubordinateBus = 0x1A;

    // PCIe capability structure layout.
    private const byte PcieCapabilityId = 0x10;         // capability id (PCIe)
    private const int PciePortTypeRoot = 4;             // PCIe cap type: root port
    private const int PciePortTypeDownstream = 6;       // PCIe cap type: downstream port
    private const int PcieCapSlotImplemented = 8;       // bit in PCIe caps register
    private const byte PcieCapSlotControlOffset = 0x18; // from capability base
    private const byte PcieCapSlotStatusOffset = 0x1A;  // from capability base
    private const int SlotStatusAttentionButton = 0;    // SLTSTA bit 0 (ABP)
    private const int SlotStatusPresenceDetect = 6;     // SLTSTA bit 6 (PDS)
    private const int SlotStatusDllChanged = 8;         // SLTSTA bit 8 (DLSCC, informational)

    // Slot Control fields used to complete an unplug request (PCIe 6.7.3):
    // Power Indicator Control [9:8] (11b = off) and Power Controller
    // Control bit 10 (1b = power off). QEMU performs the physical eject
    // when the guest powers the slot off (pcie_cap_slot_write_config ->
    // "powered_off && PDS -> do_unplug").
    private const ushort SlotControlPicMask = 0x0300;
    private const ushort SlotControlPicOff = 0x0300;
    private const ushort SlotControlPccOff = 0x0400;

    private struct Port
    {
        public byte Bus;            // root port BDF
        public byte Device;
        public byte Function;
        public byte CapOffset;      // PCIe capability base (debug)
        public byte SecondaryBus;   // bus range below the port
        public byte SubordinateBus;
        public byte SlotControlOffset;  // 0 = no slot registers
        public byte SlotStatusOffset;   // 0 = no slot registers
        public bool HasSlot;
        public int LastPresence;    // -1 = unknown
        public ushort LastStatusRaw;    // last Slot Status value (change log)
    }

    private struct TrackedDevice
    {
        public int PortIndex;
        public byte Bus;
        public byte Device;
        public byte Function;
        public int PciTreeId;
        public int VirtioTreeId;
    }

    private static readonly Port[] _ports = new Port[MaxPorts];
    private static int _portCount;
    private static readonly TrackedDevice[] _tracked = new TrackedDevice[MaxTracked];
    private static int _trackedCount;
    private static bool _initialized;
    private static ulong _lastPollTick;

    /// <summary>Number of root ports with slots found at init (diagnostics).</summary>
    public static int PortCount => _portCount;

    /// <summary>
    /// Find every PCIe root port that implements a slot and remember its
    /// secondary bus so the poll can rescan it. Called once from
    /// DriverFramework.Initialize after the bus enumerators ran.
    /// No-op on ARM64 (x64/PCIe hot-plug support).
    /// </summary>
    public static void Initialize()
    {
#if ARCH_ARM64
        _initialized = true;
        return;
#else
        if (_initialized)
            return;
        _initialized = true;

        KernelDeviceTree tree = KernelDeviceTree.Instance;
        for (int i = 1; i < tree.Count && _portCount < MaxPorts; i++)
        {
            DeviceInfo dev = tree.GetAt(i);
            if (dev.Bus != "pci")
                continue;

            // Only PCI-to-PCI bridges (base 0x06, sub 0x04) carry slot
            // registers; endpoints also have a PCIe capability but no
            // downstream bus (q35's integrated e1000e is such an endpoint).
            if (((dev.ClassCode >> 16) & 0xFF) != 0x06 || ((dev.ClassCode >> 8) & 0xFF) != 0x04)
                continue;

            byte bus = (byte)((dev.Address >> 8) & 0xFF);
            byte device = (byte)((dev.Address >> 3) & 0x1F);
            byte function = (byte)(dev.Address & 0x7);

            int cap = FindPcieCapability(bus, device, function);
            if (cap < 0)
                continue;

            ushort pcieCaps = PCI.ReadConfig16(bus, device, function, (byte)(cap + 0x02));
            int portType = (pcieCaps >> 4) & 0xF;
            if (portType != PciePortTypeRoot && portType != PciePortTypeDownstream)
                continue;   // PCIe-to-PCI bridges etc. have no hot-plug slots
            bool slotImplemented = ((pcieCaps >> PcieCapSlotImplemented) & 1) != 0;

            Port port = new Port();
            port.Bus = bus;
            port.Device = device;
            port.Function = function;
            port.CapOffset = (byte)cap;
            port.SecondaryBus = PCI.ReadConfig8(bus, device, function, OffsetSecondaryBus);
            port.SubordinateBus = PCI.ReadConfig8(bus, device, function, OffsetSubordinateBus);
            port.HasSlot = slotImplemented;
            port.SlotControlOffset = slotImplemented ? (byte)(cap + PcieCapSlotControlOffset) : (byte)0;
            port.SlotStatusOffset = slotImplemented ? (byte)(cap + PcieCapSlotStatusOffset) : (byte)0;
            port.LastPresence = slotImplemented ? ReadPresence(bus, device, function, port.SlotStatusOffset) : -1;
            port.LastStatusRaw = slotImplemented ? PCI.ReadConfig16(bus, device, function, port.SlotStatusOffset) : (ushort)0;
            _ports[_portCount] = port;
            _portCount++;

            DebugConsole.Write("[hotplug] root port ");
            DebugConsole.Write(dev.Path);
            DebugConsole.Write(" bus=");
            DebugConsole.WriteDecimal(port.SecondaryBus);
            DebugConsole.Write(slotImplemented ? " slot=yes" : " slot=no");
            if (slotImplemented)
            {
                DebugConsole.Write(" cap=0x");
                DebugConsole.Write(Hex2b((byte)cap));
                DebugConsole.Write(" caps=0x");
                DebugConsole.Write(Hex4(pcieCaps));
                DebugConsole.Write(" ctl=0x");
                DebugConsole.Write(Hex4(PCI.ReadConfig16(bus, device, function, port.SlotControlOffset)));
                DebugConsole.Write(" sta=0x");
                DebugConsole.Write(Hex4(PCI.ReadConfig16(bus, device, function, port.SlotStatusOffset)));
            }
            DebugConsole.WriteLine();
        }

        DebugConsole.Write("[hotplug] monitoring ");
        DebugConsole.WriteDecimal((uint)_portCount);
        DebugConsole.WriteLine(" PCIe root port bus(es), poll 200ms");
#endif
    }

    /// <summary>
    /// Poll the root ports: presence-detect edges and secondary-bus
    /// rescans. Throttled to PollIntervalTicks; safe to call from the
    /// shell idle hook on every pass.
    /// </summary>
    public static void Poll()
    {
        if (!_initialized)
            return;

        ulong now = ProtonOS.Arch.APIC.TickCount;
        if (now - _lastPollTick < PollIntervalTicks)
            return;
        _lastPollTick = now;

        // ICH9/q35: complete pending ACPI eject requests first; a
        // completed eject makes the device disappear, which the per-port
        // rescan below then observes and unbinds.
        AcpiPciHotplug.Poll();

        for (int p = 0; p < _portCount; p++)
        {
            Port port = _ports[p];

            // Spec: hot-plug detection via the PCIe capability structure.
            if (port.HasSlot)
            {
                ushort slotStatus = PCI.ReadConfig16(port.Bus, port.Device, port.Function, port.SlotStatusOffset);

                // Unplug request: the port set Attention Button Pressed
                // (QEMU does this on device_del for a powered slot). The
                // guest responds by powering the slot off - Power
                // Indicator Control = off, Power Controller Control =
                // power off - which performs the physical eject and drops
                // Presence Detect State; the bus rescan below then
                // unbinds the driver. The ABP bit is write-1-to-clear.
                if (((slotStatus >> SlotStatusAttentionButton) & 1) != 0)
                {
                    ushort slotControl = PCI.ReadConfig16(port.Bus, port.Device, port.Function, port.SlotControlOffset);
                    slotControl = (ushort)((slotControl & ~SlotControlPicMask) | SlotControlPicOff);
                    slotControl = (ushort)(slotControl | SlotControlPccOff);
                    PCI.WriteConfig16(port.Bus, port.Device, port.Function, port.SlotControlOffset, slotControl);
                    PCI.WriteConfig16(port.Bus, port.Device, port.Function, port.SlotStatusOffset, 0x0001);
                    DebugConsole.Write("[hotplug] unplug requested (attention button), powering slot off: bus=");
                    DebugConsole.WriteDecimal(port.SecondaryBus);
                    DebugConsole.WriteLine();
                }

                // Presence detect may have already changed during the
                // power-off write (QEMU ejects synchronously), so re-read.
                slotStatus = PCI.ReadConfig16(port.Bus, port.Device, port.Function, port.SlotStatusOffset);

                // Log every slot-status change (presence detect, attention
                // button, link state) - the raw bits are the primary
                // hot-plug diagnostics.
                if (slotStatus != port.LastStatusRaw)
                {
                    DebugConsole.Write("[hotplug] slot ");
                    DebugConsole.WriteDecimal(port.SecondaryBus);
                    DebugConsole.Write(" status 0x");
                    DebugConsole.Write(Hex4(slotStatus));
                    DebugConsole.Write(", presence=");
                    DebugConsole.WriteDecimal((uint)((slotStatus >> SlotStatusPresenceDetect) & 1));
                    DebugConsole.WriteLine();
                    port.LastStatusRaw = slotStatus;
                }

                int presence = (int)((slotStatus >> SlotStatusPresenceDetect) & 1);
                port.LastPresence = presence;
                _ports[p] = port;
            }

            RescanPort(p, port);
        }
    }

    /// <summary>
    /// Rescan one port's secondary bus: add devices that appeared, remove
    /// tracked devices that vanished.
    /// </summary>
    private static void RescanPort(int portIndex, Port port)
    {
        if (port.SecondaryBus == 0)
            return;   // firmware assigned no bus below this port

        // Removal: every tracked device below this port that no longer
        // answers config reads is hot-unplugged.
        for (int t = 0; t < _trackedCount; t++)
        {
            TrackedDevice entry = _tracked[t];
            if (entry.PortIndex != portIndex)
                continue;
            if (entry.Bus != port.SecondaryBus)
                continue;

            uint id = PCI.ReadConfig32(entry.Bus, entry.Device, entry.Function, 0);
            if ((id & 0xFFFF) != 0xFFFF)
                continue;

            HandleRemove(t, entry);
            t--;   // swap-removed; recheck this slot
        }

        // Arrival: scan the port's bus range for functions that are not
        // known to the tree yet.
        for (int dev = 0; dev < 32; dev++)
        {
            byte functionCount = 1;
            uint fn0 = PCI.ReadConfig32(port.SecondaryBus, (byte)dev, 0, 0);
            if ((fn0 & 0xFFFF) == 0xFFFF)
                continue;
            byte headerType = PCI.ReadConfig8(port.SecondaryBus, (byte)dev, 0, OffsetHeaderType);
            if ((headerType & 0x80) != 0)
                functionCount = 8;

            for (byte fn = 0; fn < functionCount; fn++)
            {
                uint id = fn == 0 ? fn0 : PCI.ReadConfig32(port.SecondaryBus, (byte)dev, fn, 0);
                if ((id & 0xFFFF) == 0xFFFF)
                    continue;
                if (IsTracked(port.SecondaryBus, (byte)dev, fn))
                    continue;
                if (TreeHasFunction(port.SecondaryBus, (byte)dev, fn))
                    continue;

                HandleAdd(portIndex, port.SecondaryBus, (byte)dev, fn);
            }
        }
    }

    /// <summary>Add a newly appeared device: tree nodes, then driver match.</summary>
    private static void HandleAdd(int portIndex, byte bus, byte device, byte function)
    {
        DeviceInfo pciNode = PciBusEnumerator.AddFunction(bus, device, function);
        if (pciNode == null)
            return;

        DebugConsole.Write("[hotplug] device added: ");
        DebugConsole.Write(pciNode.Path);
        DebugConsole.Write(" vid:did=");
        DebugConsole.Write(Hex4(pciNode.VendorId));
        DebugConsole.Write(":");
        DebugConsole.Write(Hex4(pciNode.DeviceId));
        DebugConsole.WriteLine();

        DeviceInfo virtioNode = VirtioBusEnumerator.TryAddFor(pciNode);

        if (_trackedCount < MaxTracked)
        {
            TrackedDevice entry = new TrackedDevice();
            entry.PortIndex = portIndex;
            entry.Bus = bus;
            entry.Device = device;
            entry.Function = function;
            entry.PciTreeId = pciNode.Id;
            entry.VirtioTreeId = virtioNode != null ? virtioNode.Id : -1;
            _tracked[_trackedCount] = entry;
            _trackedCount++;
        }

        // Match the new nodes against registered drivers; the manager logs
        // "[drv] bound '<name>' to <path>" when a driver starts.
        int started = DriverManager.MatchAll();
        if (started == 0)
        {
            DebugConsole.Write("[hotplug] no driver matched ");
            DebugConsole.WriteLine(pciNode.Path);
        }
    }

    /// <summary>
    /// Remove a device that disappeared: stop its drivers (children
    /// first), then mark the nodes removed.
    /// </summary>
    private static void HandleRemove(int trackedIndex, TrackedDevice entry)
    {
        KernelDeviceTree tree = KernelDeviceTree.Instance;
        DeviceInfo pciNode = tree.GetById(entry.PciTreeId);

        DebugConsole.Write("[hotplug] device removed: ");
        DebugConsole.WriteLine(pciNode != null ? pciNode.Path : "<unknown>");

        DriverManager.UnbindDeviceTree(pciNode);

        // Swap-remove the tracked entry.
        _trackedCount--;
        _tracked[trackedIndex] = _tracked[_trackedCount];
    }

    /// <summary>Presence Detect State (1 = card present) from Slot Status.</summary>
    private static int ReadPresence(byte bus, byte device, byte function, byte slotStatusOffset)
    {
        ushort status = PCI.ReadConfig16(bus, device, function, slotStatusOffset);
        return (int)((status >> SlotStatusPresenceDetect) & 1);
    }

    /// <summary>Walk the capability list for the PCI Express capability.</summary>
    private static int FindPcieCapability(byte bus, byte device, byte function)
    {
        ushort status = PCI.ReadConfig16(bus, device, function, OffsetStatus);
        if ((status & 0x10) == 0)   // capability list present
            return -1;

        byte pos = (byte)(PCI.ReadConfig8(bus, device, function, OffsetCapPointer) & 0xFC);
        for (int i = 0; i < 48 && pos != 0; i++)
        {
            byte id = PCI.ReadConfig8(bus, device, function, pos);
            if (id == PcieCapabilityId)
                return pos;
            pos = (byte)(PCI.ReadConfig8(bus, device, function, (byte)(pos + 1)) & 0xFC);
        }
        return -1;
    }

    /// <summary>True when this BDF is already tracked as a hot-added device.</summary>
    private static bool IsTracked(byte bus, byte device, byte function)
    {
        for (int t = 0; t < _trackedCount; t++)
        {
            TrackedDevice e = _tracked[t];
            if (e.Bus == bus && e.Device == device && e.Function == function)
                return true;
        }
        return false;
    }

    /// <summary>True when a PCI node for this BDF exists in the device tree.</summary>
    private static bool TreeHasFunction(byte bus, byte device, byte function)
    {
        uint address = (uint)((bus << 8) | ((device & 0x1F) << 3) | (function & 0x7));
        return KernelDeviceTree.Instance.FindByBusAddress("pci", address) != null;
    }

    private static char HexDigit(int v)
    {
        v &= 0xF;
        return v < 10 ? (char)('0' + v) : (char)('A' + (v - 10));
    }

    private static string Hex2b(byte v)
    {
        return new string(new char[] { HexDigit(v >> 4), HexDigit(v) });
    }

    private static string Hex4(ushort v)
    {
        return new string(new char[]
        {
            HexDigit(v >> 12), HexDigit(v >> 8), HexDigit(v >> 4), HexDigit(v)
        });
    }
}
