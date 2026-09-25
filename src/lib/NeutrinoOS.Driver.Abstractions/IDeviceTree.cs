// NeutrinoOS driver framework - device tree interface.

namespace NeutrinoOS.Drivers
{
    /// <summary>
    /// The kernel's hierarchical device tree, populated at boot by bus
    /// enumerators (root -> PCI -> VirtIO -> ...). Drivers can walk it to
    /// find siblings/children (e.g. a VirtIO bus driver exposing its device).
    /// </summary>
    public interface IDeviceTree
    {
        /// <summary>Root device; never null.</summary>
        DeviceInfo Root { get; }

        /// <summary>Total number of devices in the tree.</summary>
        int Count { get; }

        /// <summary>Get the ith device in discovery order.</summary>
        DeviceInfo GetAt(int index);

        /// <summary>Find a device by id, or null.</summary>
        DeviceInfo GetById(int id);

        /// <summary>Find a device by path, or null.</summary>
        DeviceInfo GetByPath(string path);

        /// <summary>Children of a device (ids), empty array when none.</summary>
        int[] GetChildren(int id);
    }
}
