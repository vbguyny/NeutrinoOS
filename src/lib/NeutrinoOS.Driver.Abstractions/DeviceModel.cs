// NeutrinoOS driver framework - device model.
//
// A DeviceInfo is the kernel's description of a hardware device instance:
// identity (vendor/device/class), location (bus + address), and the
// resources the owning driver may use (I/O ports, MMIO windows, IRQ lines,
// DMA capability). Drivers never touch hardware except through resources
// listed here and mapped via IDriverServices.

namespace NeutrinoOS.Drivers
{
    /// <summary>Broad device classes used for driver matching and /dev naming.</summary>
    public enum DeviceClass : int
    {
        Unknown = 0,
        Storage = 1,
        Network = 2,
        Display = 3,
        Input = 4,
        Serial = 5,
        Audio = 6,
        Bridge = 7,
        System = 8,
    }

    /// <summary>Kind of a device resource entry.</summary>
    public enum DeviceResourceKind : int
    {
        IoPort = 1,     // Base + Length, I/O port range
        Mmio = 2,       // Base + Length, physical MMIO range
        Irq = 3,        // Base = IRQ line, Length = 0
        Dma = 4,        // Base = 0, Length = max transfer size hint (0 = unrestricted)
    }

    /// <summary>A single device resource (port range, MMIO window, IRQ, DMA).</summary>
    public struct DeviceResource
    {
        public DeviceResourceKind Kind;
        public ulong Base;
        public ulong Length;

        public DeviceResource(DeviceResourceKind kind, ulong baseAddress, ulong length)
        {
            Kind = kind;
            Base = baseAddress;
            Length = length;
        }
    }

    /// <summary>Lifecycle status of a device in the device tree.</summary>
    public enum DeviceStatus : int
    {
        Discovered = 0,     // enumerated, no driver bound
        Matched = 1,        // driver found, probe pending
        Started = 2,        // driver bound and running
        Failed = 3,         // probe or start failed
        Removed = 4,        // hot-unplugged
    }

    /// <summary>
    /// Kernel description of a device instance. Created by bus enumerators
    /// (PCI, VirtIO) and handed to drivers via IDriver.Probe/Start.
    /// </summary>
    public sealed class DeviceInfo
    {
        /// <summary>Stable numeric device id, unique within the boot.</summary>
        public int Id { get; }

        /// <summary>Device tree parent id, or -1 for root devices.</summary>
        public int ParentId { get; }

        /// <summary>Device path in the tree, e.g. "/pci/00:03.0/virtio0".</summary>
        public string Path { get; }

        /// <summary>PCI-style vendor id (0xFFFF when not applicable).</summary>
        public ushort VendorId { get; }

        /// <summary>PCI-style device id.</summary>
        public ushort DeviceId { get; }

        /// <summary>Device class for matching and naming.</summary>
        public DeviceClass Class { get; }

        /// <summary>Raw bus-specific class code (e.g. PCI class/subclass/prog-if packed).</summary>
        public uint ClassCode { get; }

        /// <summary>Bus type name ("pci", "virtio", "platform", "root").</summary>
        public string Bus { get; }

        /// <summary>Bus-specific address (e.g. PCI bus/device/function packed as 0xBB_DD_F).</summary>
        public uint Address { get; }

        /// <summary>Resources assigned to this device.</summary>
        public DeviceResource[] Resources { get; }

        /// <summary>Current lifecycle status.</summary>
        public DeviceStatus Status { get; set; }

        public DeviceInfo(
            int id,
            int parentId,
            string path,
            string bus,
            uint address,
            ushort vendorId,
            ushort deviceId,
            DeviceClass deviceClass,
            uint classCode,
            DeviceResource[] resources)
        {
            Id = id;
            ParentId = parentId;
            Path = path;
            Bus = bus;
            Address = address;
            VendorId = vendorId;
            DeviceId = deviceId;
            Class = deviceClass;
            ClassCode = classCode;
            Resources = resources ?? EmptyResources;
            Status = DeviceStatus.Discovered;
        }

        private static readonly DeviceResource[] EmptyResources = new DeviceResource[0];

        /// <summary>Find the first resource of the given kind, or null.</summary>
        public bool TryGetResource(DeviceResourceKind kind, out DeviceResource resource)
        {
            for (int i = 0; i < Resources.Length; i++)
            {
                if (Resources[i].Kind == kind)
                {
                    resource = Resources[i];
                    return true;
                }
            }
            resource = default;
            return false;
        }
    }
}
