// NeutrinoOS Phase 10 - kernel block device bridge.
//
// Wraps the kernel's block device registry (AHCI ports, NVMe namespaces,
// USB mass storage) as an IBlockDevice so filesystem drivers and disk
// tooling can perform raw sector I/O on any attached disk. The kernel
// registers devices as they bind; names follow the device tree
// (hda/hdb... for AHCI, nvme0..., sda... for USB).

using System;
using System.Runtime.InteropServices;

namespace NeutrinoOS.DDK.Storage;

/// <summary>Information about one kernel-registered block device.</summary>
public class BlockDeviceInfo
{
    /// <summary>Device name (e.g., "hda", "nvme0", "sda").</summary>
    public string Name = "";

    /// <summary>Kernel registry handle (pass to <see cref="KernelBlockDevice"/>).</summary>
    public int Handle;

    /// <summary>Total number of sectors.</summary>
    public ulong SectorCount;

    /// <summary>Sector size in bytes.</summary>
    public uint SectorSize;

    /// <summary>True when the device is removable (USB mass storage).</summary>
    public bool Removable;
}

/// <summary>
/// DDK view of the kernel block device registry. Utilities and
/// filesystem drivers enumerate attached disks here and open them with
/// <see cref="KernelBlockDevice"/>; all I/O is synchronous and funnels
/// into the same driver code paths the kernel uses for its own mounts.
/// </summary>
public static unsafe class BlockDevices
{
    /// <summary>Name of a device (raw form; returns length or -1).</summary>
    [DllImport("*", EntryPoint = "Kernel_BlockDeviceName")]
    private static extern int BlockDeviceName(int handle, char* buf, int capacity);

    /// <summary>Sector count of a device (0 when the handle is invalid).</summary>
    [DllImport("*", EntryPoint = "Kernel_BlockDeviceSectorCount")]
    private static extern ulong BlockDeviceSectorCount(int handle);

    /// <summary>Sector size of a device (0 when the handle is invalid).</summary>
    [DllImport("*", EntryPoint = "Kernel_BlockDeviceSectorSize")]
    private static extern uint BlockDeviceSectorSize(int handle);

    /// <summary>Non-zero when the device is removable.</summary>
    [DllImport("*", EntryPoint = "Kernel_BlockDeviceRemovable")]
    private static extern int BlockDeviceRemovable(int handle);

    /// <summary>Non-zero when the device handle still exists.</summary>
    [DllImport("*", EntryPoint = "Kernel_BlockDevicePresent")]
    private static extern int BlockDevicePresent(int handle);

    /// <summary>Reads sectors; returns blocks read or negative on error.</summary>
    [DllImport("*", EntryPoint = "Kernel_BlockDeviceRead")]
    private static extern int BlockDeviceRead(int handle, ulong startBlock, uint blockCount, byte* buffer);

    /// <summary>Writes sectors; returns blocks written or negative on error.</summary>
    [DllImport("*", EntryPoint = "Kernel_BlockDeviceWrite")]
    private static extern int BlockDeviceWrite(int handle, ulong startBlock, uint blockCount, byte* buffer);

    /// <summary>Flushes device write caches; 0 OK, negative on error.</summary>
    [DllImport("*", EntryPoint = "Kernel_BlockDeviceFlush")]
    private static extern int BlockDeviceFlush(int handle);

    /// <summary>Number of registered devices.</summary>
    [DllImport("*", EntryPoint = "Kernel_BlockDeviceCount")]
    private static extern int BlockDeviceCount();

    /// <summary>Handle of device <paramref name="index"/> (or -1).</summary>
    [DllImport("*", EntryPoint = "Kernel_BlockDeviceHandleAt")]
    private static extern int BlockDeviceHandleAt(int index);

    /// <summary>
    /// Attach/detach generation counter; changes whenever a device is
    /// registered or removed (used by AutoMount to gate re-scans).
    /// </summary>
    [DllImport("*", EntryPoint = "Kernel_BlockDeviceGeneration")]
    private static extern int BlockDeviceGeneration();

    /// <summary>Current registry generation (see the export docs).</summary>
    public static int Generation => BlockDeviceGeneration();

    /// <summary>Unmanaged view matching one registry entry.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RawDeviceInfo
    {
        public int Handle;
        public ulong SectorCount;
        public uint SectorSize;
        public int Removable;
    }

    /// <summary>Raw info of device <paramref name="index"/> (0 OK, -1 bad index).</summary>
    [DllImport("*", EntryPoint = "Kernel_BlockDeviceInfoAt")]
    private static extern int BlockDeviceInfoAt(int index, RawDeviceInfo* info);

    /// <summary>
    /// Enumerates all registered block devices.
    /// </summary>
    public static BlockDeviceInfo[] Enumerate()
    {
        int count = BlockDeviceCount();
        if (count <= 0)
            return new BlockDeviceInfo[0];

        var list = new BlockDeviceInfo[count];
        int used = 0;
        for (int i = 0; i < count; i++)
        {
            var info = new BlockDeviceInfo();
            int handle = BlockDeviceHandleAt(i);
            if (handle < 0)
                continue;
            info.Handle = handle;
            info.SectorCount = BlockDeviceSectorCount(handle);
            info.SectorSize = BlockDeviceSectorSize(handle);
            info.Removable = BlockDeviceRemovable(handle) != 0;

            const int Cap = 64;
            char* nameBuf = stackalloc char[Cap];
            int len = BlockDeviceName(handle, nameBuf, Cap);
            info.Name = (len > 0) ? new string(nameBuf, 0, len) : ("dev" + handle.ToString());

            list[used++] = info;
        }

        if (used == count)
            return list;

        var trimmed = new BlockDeviceInfo[used];
        for (int i = 0; i < used; i++)
            trimmed[i] = list[i];
        return trimmed;
    }

    /// <summary>
    /// Finds a device by name (exact match, case-insensitive for the
    /// ASCII device names). Returns null when not found.
    /// </summary>
    /// <param name="name">Device name such as "hdb" or "sda".</param>
    public static BlockDeviceInfo? Find(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        // Accept "/dev/xyz" forms as well.
        if (name.StartsWith("/dev/"))
            name = name.Substring(5);

        var all = Enumerate();
        for (int i = 0; i < all.Length; i++)
        {
            if (NameEquals(all[i].Name, name))
                return all[i];
        }
        return null;
    }

    /// <summary>Case-insensitive ASCII name comparison.</summary>
    private static bool NameEquals(string a, string b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            char ca = a[i];
            char cb = b[i];
            if (ca >= 'A' && ca <= 'Z')
                ca = (char)(ca + 32);
            if (cb >= 'A' && cb <= 'Z')
                cb = (char)(cb + 32);
            if (ca != cb)
                return false;
        }
        return true;
    }

    /// <summary>True when a device handle still exists (hot-plug safe).</summary>
    public static bool IsPresent(int handle) => BlockDevicePresent(handle) != 0;
}

/// <summary>
/// An <see cref="IBlockDevice"/> backed by a kernel-registered disk.
/// Sector I/O is forwarded to the kernel registry; the object is safe to
/// hold across hot-plug (calls fail cleanly once the device is gone).
/// </summary>
public unsafe class KernelBlockDevice : IBlockDevice
{
    private readonly int _handle;
    private readonly string _name;
    private readonly ulong _sectorCount;
    private readonly uint _sectorSize;
    private readonly bool _removable;

    /// <summary>Wraps a registry handle reported by <see cref="BlockDevices"/>.</summary>
    /// <param name="info">Device info from <see cref="BlockDevices.Enumerate"/>.</param>
    public KernelBlockDevice(BlockDeviceInfo info)
    {
        _handle = info.Handle;
        _name = info.Name;
        _sectorCount = info.SectorCount;
        _sectorSize = info.SectorSize == 0 ? 512 : info.SectorSize;
        _removable = info.Removable;
    }

    /// <summary>Opens a device by name (null when not found).</summary>
    /// <param name="name">Device name such as "hdb" or "/dev/sda".</param>
    public static KernelBlockDevice? Open(string name)
    {
        var info = BlockDevices.Find(name);
        if (info == null)
            return null;
        return new KernelBlockDevice(info);
    }

    /// <summary>Driver interface name (IDriver).</summary>
    public string DriverName => "exfat-block";

    /// <summary>Driver interface version (IDriver).</summary>
    public Version DriverVersion => new Version(1, 0, 0);

    /// <summary>This bridge is a block-device adapter (IDriver).</summary>
    public Drivers.DriverType Type => Drivers.DriverType.Storage;

    /// <summary>Always running while the handle is alive (IDriver).</summary>
    public Drivers.DriverState State => Drivers.DriverState.Running;

    /// <summary>No-op initialisation (IDriver).</summary>
    public bool Initialize() => true;

    /// <summary>No-op shutdown (IDriver).</summary>
    public void Shutdown() { }

    /// <summary>No-op suspend (IDriver).</summary>
    public void Suspend() { }

    /// <summary>No-op resume (IDriver).</summary>
    public void Resume() { }

    /// <summary>The device name from the kernel registry.</summary>
    public string DeviceName => _name;

    /// <summary>Total number of sectors.</summary>
    public ulong BlockCount => _sectorCount;

    /// <summary>Sector size in bytes.</summary>
    public uint BlockSize => _sectorSize;

    /// <summary>Capabilities reported by the kernel registry.</summary>
    public BlockDeviceCapabilities Capabilities =>
        BlockDeviceCapabilities.Read | BlockDeviceCapabilities.Write |
        BlockDeviceCapabilities.Flush |
        (_removable ? BlockDeviceCapabilities.Removable : 0);

    /// <summary>Reads sectors through the kernel registry.</summary>
    public int Read(ulong startBlock, uint blockCount, byte* buffer) =>
        BlockDevicesProxy.Read(_handle, startBlock, blockCount, buffer);

    /// <summary>Writes sectors through the kernel registry.</summary>
    public int Write(ulong startBlock, uint blockCount, byte* buffer) =>
        BlockDevicesProxy.Write(_handle, startBlock, blockCount, buffer);

    /// <summary>Flushes caches through the kernel registry.</summary>
    public BlockResult Flush() =>
        BlockDevicesProxy.Flush(_handle) == 0 ? BlockResult.Success : BlockResult.IoError;
}

/// <summary>Internals shared by <see cref="BlockDevices"/> and <see cref="KernelBlockDevice"/>.</summary>
internal static unsafe class BlockDevicesProxy
{
    [DllImport("*", EntryPoint = "Kernel_BlockDeviceRead")]
    internal static extern int Read(int handle, ulong startBlock, uint blockCount, byte* buffer);

    [DllImport("*", EntryPoint = "Kernel_BlockDeviceWrite")]
    internal static extern int Write(int handle, ulong startBlock, uint blockCount, byte* buffer);

    [DllImport("*", EntryPoint = "Kernel_BlockDeviceFlush")]
    internal static extern int Flush(int handle);
}
