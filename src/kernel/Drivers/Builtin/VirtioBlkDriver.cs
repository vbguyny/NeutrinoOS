// ProtonOS Kernel - built-in VirtIO-BLK driver (framework port).
//
// Binds the "virtio-blk" child node the VirtIO enumerator adds under its
// PCI parent (vendor 0x1AF4, device type flattened into ClassCode: 2 =
// block). The virtqueue and FAT/EXT2 mounts keep running through the
// legacy JIT'd driver until the full port moves the block I/O behind the
// framework.

using System;
using NeutrinoOS.Drivers;

namespace ProtonOS.Drivers.Builtin;

/// <summary>VirtIO block device driver (framework port).</summary>
public sealed class VirtioBlkDriver : IDriver
{
    private IDriverServices _services;

    public string Name => "virtio-blk";

    public string Version => "1.0.0";

    public int AbiMajor => DriverAbi.Major;

    public int AbiMinor => DriverAbi.Minor;

    /// <summary>Stores the kernel services instance injected by the manager.</summary>
    public void Initialize(IDriverServices services)
    {
        _services = services;
    }

    /// <summary>Matches the virtio-blk child node (type 2 in ClassCode).</summary>
    public bool Match(DeviceInfo device)
    {
        return device.Bus == "virtio" && device.ClassCode == 2;
    }

    /// <summary>No resource requirements while the transport stays legacy.</summary>
    public bool Probe(DeviceInfo device)
    {
        _ = device;
        return true;
    }

    /// <summary>Logs the framework handoff.</summary>
    public bool Start(DeviceInfo device)
    {
        if (_services == null)
            return false;
        _services.Log(DriverLogLevel.Info, "virtio-blk started on " + device.Path
            + " (legacy VirtIO-BLK path keeps the filesystems)");
        return true;
    }

    /// <summary>Nothing to release yet.</summary>
    public void Stop(DeviceInfo device)
    {
        _ = device;
    }
}
