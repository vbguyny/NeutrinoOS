// ProtonOS Kernel - VirtIO bus enumerator for the driver framework.
//
// Runs after the PCI enumerator: every PCI function with vendor 0x1AF4 is a
// VirtIO device. The device id encodes the VirtIO device type (legacy
// 0x1000+type, modern 0x1040+type). Each device gets a child node under its
// PCI parent so drivers can bind to it (e.g. virtio-net, virtio-blk).

using System;
using NeutrinoOS.Drivers;

namespace ProtonOS.Drivers;

/// <summary>Enumerates VirtIO devices (PCI vendor 0x1AF4) into the tree.</summary>
public static class VirtioBusEnumerator
{
    public const ushort VirtioVendorId = 0x1AF4;

    private static readonly int[] TypeCounts = new int[64];

    /// <summary>VirtIO device type from a PCI device id, or -1 when not VirtIO.</summary>
    public static int VirtioTypeFromDeviceId(ushort deviceId)
    {
        if (deviceId >= 0x1000 && deviceId <= 0x103F)
            return deviceId - 0x1000;   // legacy: 0x1000 + type
        if (deviceId >= 0x1040 && deviceId <= 0x107F)
            return deviceId - 0x1040;   // modern: 0x1040 + type
        return -1;
    }

    /// <summary>Short name for a VirtIO device type.</summary>
    public static string TypeName(int virtioType)
    {
        switch (virtioType)
        {
            case 1: return "net";
            case 2: return "blk";
            case 3: return "console";
            case 4: return "scsi";
            case 5: return "entropy";
            case 9: return "9p";
            case 16: return "gpu";
            case 18: return "input";
            default: return "type" + virtioType.ToString();
        }
    }

    /// <summary>
    /// Add child nodes for every VirtIO PCI device. Returns the count added.
    /// </summary>
    public static int Enumerate()
    {
        KernelDeviceTree tree = KernelDeviceTree.Instance;
        int added = 0;

        for (int i = 0; i < tree.Count; i++)
        {
            DeviceInfo pciDev = tree.GetAt(i);
            if (pciDev.Bus != "pci" || pciDev.VendorId != VirtioVendorId)
                continue;

            if (TryAddFor(pciDev) != null)
                added++;
        }

        return added;
    }

    /// <summary>
    /// Add the VirtIO child node for one PCI device (used by boot
    /// enumeration and by the PCIe hot-plug detector when a new VirtIO
    /// function appears). Returns the node, or null when the device is not
    /// VirtIO, the tree is full, or a child already exists.
    /// </summary>
    public static DeviceInfo TryAddFor(DeviceInfo pciDev)
    {
        if (pciDev == null || pciDev.Bus != "pci" || pciDev.VendorId != VirtioVendorId)
            return null;

        KernelDeviceTree tree = KernelDeviceTree.Instance;

        int virtioType = VirtioTypeFromDeviceId(pciDev.DeviceId);
        if (virtioType < 0)
            return null;

        // Idempotence for the hot-plug path: never add a second child for
        // the same PCI function.
        int[] children = tree.GetChildren(pciDev.Id);
        for (int c = 0; c < children.Length; c++)
        {
            DeviceInfo child = tree.GetById(children[c]);
            if (child != null && child.Bus == "virtio")
                return child;
        }

        string typeName = TypeName(virtioType);
        int index = 0;
        if (virtioType < TypeCounts.Length)
            index = TypeCounts[virtioType]++;

        string segment = index == 0 ? "virtio-" + typeName : "virtio-" + typeName + index.ToString();

        return tree.Add(
            pciDev.Id,
            "virtio",
            (uint)virtioType,
            VirtioVendorId,
            pciDev.DeviceId,
            DeviceClass.Unknown,
            (uint)virtioType,
            segment,
            new DeviceResource[0]);
    }
}
