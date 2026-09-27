// NeutrinoOS kernel - block device registry (Phase 10 Task 4).
//
// The kernel-side table of attached block devices (AHCI ports, NVMe
// namespaces, USB mass storage) that the ProtonOS.DDK storage tooling
// enumerates through the Kernel_BlockDevice* exports. Each entry stores
// the device identity plus (for externally loaded drivers) the JIT'd
// code addresses of the driver's sector-read/write/flush statics with a
// uniform shape:
//
//   read/write: int (int deviceIndex, ulong startBlock, uint blockCount,
//                   byte* buffer)   -> blocks transferred, negative on error
//   flush:      int (int deviceIndex) -> 0 on success
//
// USB mass storage is built into the kernel, so its entries carry the
// UsbDisk object directly and the adapter below chunks transfers
// through UsbStorage (64-sector limit per SCSI command).
//
// Handles are stable slot indexes; a slot is "present" while registered.
// The registry also keeps a small shadow list of auto-mounted volumes
// (device name -> mount path) so the shell completion can offer
// /mnt/usb/<name> without round-tripping into the DDK.

using System;
using ProtonOS.Platform;
using ProtonOS.Usb;

namespace ProtonOS.Storage;

/// <summary>One registered block device (see file header).</summary>
public sealed unsafe class BlockDevice
{
    /// <summary>Device name ("hda", "nvme0", "sda"...).</summary>
    public string Name = "";

    /// <summary>Device source (see BlockKind constants).</summary>
    public int Kind;

    /// <summary>Driver-level device index (AHCI port / NVMe namespace).</summary>
    public int DeviceIndex;

    /// <summary>Total number of sectors.</summary>
    public ulong SectorCount;

    /// <summary>Sector size in bytes.</summary>
    public uint SectorSize;

    /// <summary>Removable media (USB).</summary>
    public bool Removable;

    /// <summary>Slot currently registered (false after unplug).</summary>
    public bool Present;

    /// <summary>JIT'd read static (uniform shape; null for USB).</summary>
    public void* ReadFn;

    /// <summary>JIT'd write static (uniform shape; null for USB).</summary>
    public void* WriteFn;

    /// <summary>JIT'd flush static (uniform shape; null for USB).</summary>
    public void* FlushFn;

    /// <summary>Backing USB disk (kind USB only).</summary>
    public UsbDisk? UsbDisk;
}

/// <summary>Kernel block device registry (see file header).</summary>
public static unsafe class BlockDeviceRegistry
{
    /// <summary>Registry capacity (AHCI ports + NVMe + USB).</summary>
    public const int MaxDevices = 16;

    /// <summary>Entry kind: AHCI SATA port.</summary>
    public const int KindAhci = 0;

    /// <summary>Entry kind: NVMe namespace.</summary>
    public const int KindNvme = 1;

    /// <summary>Entry kind: USB mass storage.</summary>
    public const int KindUsb = 2;

    private static readonly BlockDevice?[] _slots = new BlockDevice?[MaxDevices];

    // Bumped on every register/unregister: the DDK AutoMount scans the
    // removable devices only when the generation changes.
    private static int _generation;

    /// <summary>Monotonic attach/detach counter (see file header).</summary>
    public static int Generation() => _generation;

    // USB adapter chunk: UsbStorage limits one SCSI command to 64 sectors.
    private const uint UsbChunkSectors = 64;

    /// <summary>
    /// Registers an externally driven device (AHCI/NVMe): the caller
    /// passes the JIT'd statics with the uniform signatures above.
    /// Returns the device handle, or -1 when the table is full.
    /// </summary>
    public static int Register(string name, int kind, int deviceIndex,
        ulong sectorCount, uint sectorSize, bool removable,
        void* readFn, void* writeFn, void* flushFn)
    {
        for (int i = 0; i < MaxDevices; i++)
        {
            if (_slots[i] == null || !_slots[i]!.Present)
            {
                // Reuse a freed slot only when the object was dropped;
                // otherwise allocate a fresh one.
                if (_slots[i] == null)
                    _slots[i] = new BlockDevice();
                var d = _slots[i]!;
                d.Name = name;
                d.Kind = kind;
                d.DeviceIndex = deviceIndex;
                d.SectorCount = sectorCount;
                d.SectorSize = sectorSize == 0 ? 512 : sectorSize;
                d.Removable = removable;
                d.Present = true;
                d.ReadFn = readFn;
                d.WriteFn = writeFn;
                d.FlushFn = flushFn;
                d.UsbDisk = null;
                _generation++;
                DebugConsole.Write("[Blocks] registered ");
                DebugConsole.Write(name);
                DebugConsole.Write(" (");
                DebugConsole.Write(sectorCount.ToString());
                DebugConsole.WriteLine(" sectors)");
                return i;
            }
        }
        DebugConsole.Write("[Blocks] registry full, dropping ");
        DebugConsole.WriteLine(name);
        return -1;
    }

    /// <summary>Registers a USB mass storage disk (kernel-internal adapter).</summary>
    public static int RegisterUsb(UsbDisk disk, string name)
    {
        for (int i = 0; i < MaxDevices; i++)
        {
            if (_slots[i] == null)
                _slots[i] = new BlockDevice();
            var d = _slots[i]!;
            if (d.Present)
                continue;
            d.Name = name;
            d.Kind = KindUsb;
            d.DeviceIndex = i;
            d.SectorCount = disk.BlockCount;
            d.SectorSize = disk.BlockSize == 0 ? 512 : disk.BlockSize;
            d.Removable = true;
            d.Present = true;
            d.ReadFn = null;
            d.WriteFn = null;
            d.FlushFn = null;
            d.UsbDisk = disk;
            _generation++;
            DebugConsole.Write("[Blocks] registered USB ");
            DebugConsole.Write(name);
            DebugConsole.Write(" (");
            DebugConsole.Write(disk.BlockCount.ToString());
            DebugConsole.WriteLine(" sectors)");
            return i;
        }
        return -1;
    }

    /// <summary>Removes a USB disk (unplug / driver unbind).</summary>
    public static void UnregisterUsb(UsbDisk disk)
    {
        for (int i = 0; i < MaxDevices; i++)
        {
            var d = _slots[i];
            if (d == null || !d.Present || d.Kind != KindUsb)
                continue;
            if (object.ReferenceEquals(d.UsbDisk, disk))
            {
                d.Present = false;
                d.UsbDisk = null;
                _generation++;
                DebugConsole.Write("[Blocks] unregistered USB ");
                DebugConsole.WriteLine(d.Name);
                return;
            }
        }
    }

    /// <summary>Handle of a named device (-1 when not present).</summary>
    public static int Find(string name)
    {
        for (int i = 0; i < MaxDevices; i++)
        {
            var d = _slots[i];
            if (d != null && d.Present && NameEquals(d.Name, name))
                return i;
        }
        return -1;
    }

    /// <summary>Number of present devices.</summary>
    public static int Count()
    {
        int n = 0;
        for (int i = 0; i < MaxDevices; i++)
            if (_slots[i] != null && _slots[i]!.Present)
                n++;
        return n;
    }

    /// <summary>Handle of the <paramref name="index"/>-th present device (-1).</summary>
    public static int HandleAt(int index)
    {
        int seen = 0;
        for (int i = 0; i < MaxDevices; i++)
        {
            var d = _slots[i];
            if (d == null || !d.Present)
                continue;
            if (seen == index)
                return i;
            seen++;
        }
        return -1;
    }

    /// <summary>Device for a handle (null when stale).</summary>
    public static BlockDevice? Get(int handle)
    {
        if (handle < 0 || handle >= MaxDevices)
            return null;
        var d = _slots[handle];
        return (d != null && d.Present) ? d : null;
    }

    /// <summary>
    /// Reads sectors through the registered device. Returns blocks read
    /// or a negative error code (bad handle, out of range, I/O error).
    /// </summary>
    public static int Read(int handle, ulong startBlock, uint blockCount, byte* buffer)
    {
        var d = Get(handle);
        if (d == null || buffer == null || blockCount == 0)
            return -1;
        if (startBlock + blockCount > d.SectorCount)
            return -2;

        if (d.Kind == KindUsb)
            return UsbRead(d.UsbDisk, startBlock, blockCount, buffer);

        var fn = (delegate* unmanaged<int, ulong, uint, byte*, int>)d.ReadFn;
        if (fn == null)
            return -3;
        return fn(d.DeviceIndex, startBlock, blockCount, buffer);
    }

    /// <summary>Writes sectors through the registered device.</summary>
    public static int Write(int handle, ulong startBlock, uint blockCount, byte* buffer)
    {
        var d = Get(handle);
        if (d == null || buffer == null || blockCount == 0)
            return -1;
        if (startBlock + blockCount > d.SectorCount)
            return -2;

        if (d.Kind == KindUsb)
            return UsbWrite(d.UsbDisk, startBlock, blockCount, buffer);

        var fn = (delegate* unmanaged<int, ulong, uint, byte*, int>)d.WriteFn;
        if (fn == null)
            return -3;
        return fn(d.DeviceIndex, startBlock, blockCount, buffer);
    }

    /// <summary>Flushes a device (0 on success, negative on error).</summary>
    public static int Flush(int handle)
    {
        var d = Get(handle);
        if (d == null)
            return -1;
        if (d.Kind == KindUsb)
            return 0;   // BOT transfers are synchronous; nothing cached

        var fn = (delegate* unmanaged<int, int>)d.FlushFn;
        if (fn == null)
            return -3;
        return fn(d.DeviceIndex);
    }

    /// <summary>Caps a transfer to a chunk size (64 KiB default windows).</summary>
    public static uint MaxTransferSectors(BlockDevice d)
    {
        _ = d;
        return 128;   // 64 KiB at 512-byte sectors
    }

    // ---- USB adapter (64-sector SCSI command limit) ----

    private static int UsbRead(UsbDisk? disk, ulong startBlock, uint blockCount, byte* buffer)
    {
        if (disk == null || !disk.Ready || disk.BlockSize != 512)
            return -1;

        uint done = 0;
        while (done < blockCount)
        {
            uint chunk = blockCount - done;
            if (chunk > UsbChunkSectors)
                chunk = UsbChunkSectors;
            ulong lba = startBlock + done;
            if (lba > 0xFFFFFFFF)
                return -1;
            if (!UsbStorage.BlockRead(disk, (uint)lba, (int)chunk, buffer + done * 512))
                return (int)done;
            done += chunk;
        }
        return (int)done;
    }

    private static int UsbWrite(UsbDisk? disk, ulong startBlock, uint blockCount, byte* buffer)
    {
        if (disk == null || !disk.Ready || disk.BlockSize != 512)
            return -1;

        uint done = 0;
        while (done < blockCount)
        {
            uint chunk = blockCount - done;
            if (chunk > UsbChunkSectors)
                chunk = UsbChunkSectors;
            ulong lba = startBlock + done;
            if (lba > 0xFFFFFFFF)
                return -1;
            if (!UsbStorage.BlockWrite(disk, (uint)lba, (int)chunk, buffer + done * 512))
                return (int)done;
            done += chunk;
        }
        return (int)done;
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

    // ==================== auto-mount shadow list ====================
    // Maintained by the DDK AutoMount service through the
    // Kernel_AutoMountNote/Forget exports; consumed by the shell's
    // tab completion to offer /mnt/usb/<device> paths.

    private const int MaxAutoMounts = 8;
    private static readonly string?[] _autoDevice = new string?[MaxAutoMounts];
    private static readonly string?[] _autoPath = new string?[MaxAutoMounts];
    private static int _autoCount;

    /// <summary>Records an auto-mounted volume (device name + mount path).</summary>
    public static void NoteAutoMount(string device, string mountPath)
    {
        for (int i = 0; i < _autoCount; i++)
        {
            if (_autoDevice[i] == device)
            {
                _autoPath[i] = mountPath;
                return;
            }
        }
        if (_autoCount >= MaxAutoMounts)
            return;
        _autoDevice[_autoCount] = device;
        _autoPath[_autoCount] = mountPath;
        _autoCount++;
    }

    /// <summary>Drops an auto-mounted volume record.</summary>
    public static void ForgetAutoMount(string device)
    {
        for (int i = 0; i < _autoCount; i++)
        {
            if (_autoDevice[i] == device)
            {
                for (int j = i; j < _autoCount - 1; j++)
                {
                    _autoDevice[j] = _autoDevice[j + 1];
                    _autoPath[j] = _autoPath[j + 1];
                }
                _autoCount--;
                _autoDevice[_autoCount] = null;
                _autoPath[_autoCount] = null;
                return;
            }
        }
    }

    /// <summary>Number of recorded auto-mounts.</summary>
    public static int AutoMountCount() => _autoCount;

    /// <summary>Device name of auto-mount <paramref name="index"/> (null).</summary>
    public static string? AutoMountDeviceAt(int index) =>
        (index >= 0 && index < _autoCount) ? _autoDevice[index] : null;

    /// <summary>Mount path of auto-mount <paramref name="index"/> (null).</summary>
    public static string? AutoMountPathAt(int index) =>
        (index >= 0 && index < _autoCount) ? _autoPath[index] : null;

    /// <summary>True when a device name parses as a registry device.</summary>
    public static bool IsRegisteredName(string name) => Find(name) >= 0;
}
