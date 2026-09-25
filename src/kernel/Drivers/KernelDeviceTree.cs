// ProtonOS Kernel - driver framework device tree.
//
// The kernel-side device tree implements the NeutrinoOS.Drivers.IDeviceTree
// ABI (src/lib/NeutrinoOS.Driver.Abstractions) and is populated at boot by
// the bus enumerators (PCI, then VirtIO on top of PCI). Device nodes use the
// shared DeviceInfo/DeviceResource types so drivers receive them directly.

using System;
using NeutrinoOS.Drivers;

namespace ProtonOS.Drivers;

/// <summary>
/// Kernel device tree. Fixed-capacity (MaxDevices) for simplicity; the
/// machine's PCI bus rarely exposes more than a few dozen devices.
/// </summary>
public sealed class KernelDeviceTree : IDeviceTree
{
    public const int MaxDevices = 128;

    /// <summary>Singleton tree instance.</summary>
    public static readonly KernelDeviceTree Instance = new KernelDeviceTree();

    private readonly DeviceInfo[] _devices;
    private int _count;

    private KernelDeviceTree()
    {
        _devices = new DeviceInfo[MaxDevices];
        _count = 0;

        // Root node: the platform itself.
        AddRoot();
    }

    /// <summary>Number of devices in the tree (including the root).</summary>
    public int Count => _count;

    /// <summary>Root device; never null.</summary>
    public DeviceInfo Root => _devices[0];

    /// <summary>Get the ith device in discovery order.</summary>
    public DeviceInfo GetAt(int index)
    {
        if (index < 0 || index >= _count)
            return null;
        return _devices[index];
    }

    /// <summary>Find a device by id, or null.</summary>
    public DeviceInfo GetById(int id)
    {
        if (id < 0 || id >= _count)
            return null;
        return _devices[id];
    }

    /// <summary>Find a device by path, or null.</summary>
    public DeviceInfo GetByPath(string path)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_devices[i].Path == path)
                return _devices[i];
        }
        return null;
    }

    /// <summary>Children of a device (ids), empty array when none.</summary>
    public int[] GetChildren(int id)
    {
        int n = 0;
        for (int i = 0; i < _count; i++)
        {
            if (_devices[i].ParentId == id)
                n++;
        }
        if (n == 0)
            return EmptyIds;

        int[] result = new int[n];
        int k = 0;
        for (int i = 0; i < _count; i++)
        {
            if (_devices[i].ParentId == id)
                result[k++] = i;
        }
        return result;
    }

    /// <summary>
    /// Add a device under the given parent. Returns the created node, or
    /// null when the tree is full.
    /// </summary>
    public DeviceInfo Add(
        int parentId,
        string bus,
        uint address,
        ushort vendorId,
        ushort deviceId,
        DeviceClass deviceClass,
        uint classCode,
        string pathSegment,
        DeviceResource[] resources)
    {
        if (_count >= MaxDevices)
            return null;

        string parentPath = "";
        if (parentId >= 0 && parentId < _count)
            parentPath = _devices[parentId].Path;

        string path = parentPath.Length == 0 ? pathSegment : parentPath + "/" + pathSegment;

        int id = _count;
        DeviceInfo device = new DeviceInfo(
            id, parentId, path, bus, address,
            vendorId, deviceId, deviceClass, classCode, resources);
        _devices[id] = device;
        _count = id + 1;
        return device;
    }

    /// <summary>Count devices matching a bus name and address.</summary>
    public DeviceInfo FindByBusAddress(string bus, uint address)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_devices[i].Bus == bus && _devices[i].Address == address)
                return _devices[i];
        }
        return null;
    }

    /// <summary>Number of non-root devices (devices discovered on buses).</summary>
    public int DeviceCount
    {
        get { return _count - 1; }
    }

    private void AddRoot()
    {
        _devices[0] = new DeviceInfo(
            0, -1, "", "root", 0,
            0xFFFF, 0xFFFF, DeviceClass.System, 0, EmptyResources);
        _count = 1;
    }

    private static readonly DeviceResource[] EmptyResources = new DeviceResource[0];
    private static readonly int[] EmptyIds = new int[0];
}
