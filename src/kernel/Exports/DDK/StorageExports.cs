// NeutrinoOS kernel - block device exports (Phase 10 Task 4).
//
// Kernel-side implementations of the NeutrinoOS.DDK storage bridge
// (src/ddk/Storage/KernelBlockDevice.cs): utilities and the exFAT driver
// enumerate and open any attached disk (AHCI/NVMe/USB) through these,
// resolved by [DllImport("*")] name at JIT time.
//
// Call convention: raw pointers only; UTF-16 char* for names/paths.

using System;
using System.Runtime.InteropServices;
using NeutrinoOS.Storage;

namespace NeutrinoOS.Exports.DDK;

/// <summary>Unmanaged view of a registry entry (matches DDK RawDeviceInfo).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RawBlockDeviceInfo
{
    /// <summary>Device handle.</summary>
    public int Handle;

    /// <summary>Total sectors.</summary>
    public ulong SectorCount;

    /// <summary>Sector size in bytes.</summary>
    public uint SectorSize;

    /// <summary>Non-zero when removable.</summary>
    public int Removable;
}

/// <summary>Block device bridge exports (see file header).</summary>
public static unsafe class StorageExports
{
    /// <summary>
    /// Copies the device name (UTF-16, not NUL-terminated; truncated to
    /// the capacity) into the caller's buffer and returns its length,
    /// or -1 for a stale handle.
    /// </summary>
    [UnmanagedCallersOnly]
    public static int BlockDeviceName(int handle, char* buf, int capacity)
    {
        var d = BlockDeviceRegistry.Get(handle);
        if (d == null)
            return -1;
        int len = d.Name.Length;
        if (len > capacity)
            len = capacity;
        for (int i = 0; i < len; i++)
            buf[i] = d.Name[i];
        return len;
    }

    /// <summary>Sector count of a device (0 for a stale handle).</summary>
    [UnmanagedCallersOnly]
    public static ulong BlockDeviceSectorCount(int handle)
    {
        var d = BlockDeviceRegistry.Get(handle);
        return d == null ? 0 : d.SectorCount;
    }

    /// <summary>Sector size of a device (0 for a stale handle).</summary>
    [UnmanagedCallersOnly]
    public static uint BlockDeviceSectorSize(int handle)
    {
        var d = BlockDeviceRegistry.Get(handle);
        return d == null ? 0 : d.SectorSize;
    }

    /// <summary>Non-zero when the device is removable.</summary>
    [UnmanagedCallersOnly]
    public static int BlockDeviceRemovable(int handle)
    {
        var d = BlockDeviceRegistry.Get(handle);
        return (d != null && d.Removable) ? 1 : 0;
    }

    /// <summary>Non-zero while the handle refers to a present device.</summary>
    [UnmanagedCallersOnly]
    public static int BlockDevicePresent(int handle)
    {
        return BlockDeviceRegistry.Get(handle) != null ? 1 : 0;
    }

    /// <summary>
    /// Reads sectors into the caller's unmanaged buffer; returns the
    /// blocks read or a negative error code.
    /// </summary>
    [UnmanagedCallersOnly]
    public static int BlockDeviceRead(int handle, ulong startBlock, uint blockCount, byte* buffer)
    {
        return BlockDeviceRegistry.Read(handle, startBlock, blockCount, buffer);
    }

    /// <summary>Writes sectors from the caller's unmanaged buffer.</summary>
    [UnmanagedCallersOnly]
    public static int BlockDeviceWrite(int handle, ulong startBlock, uint blockCount, byte* buffer)
    {
        return BlockDeviceRegistry.Write(handle, startBlock, blockCount, buffer);
    }

    /// <summary>Flushes device caches; 0 on success, negative on error.</summary>
    [UnmanagedCallersOnly]
    public static int BlockDeviceFlush(int handle)
    {
        return BlockDeviceRegistry.Flush(handle);
    }

    /// <summary>Number of present devices.</summary>
    [UnmanagedCallersOnly]
    public static int BlockDeviceCount()
    {
        return BlockDeviceRegistry.Count();
    }

    /// <summary>Handle of the index-th present device (-1 on bad index).</summary>
    [UnmanagedCallersOnly]
    public static int BlockDeviceHandleAt(int index)
    {
        return BlockDeviceRegistry.HandleAt(index);
    }

    /// <summary>Attach/detach generation counter (auto-mount scan gate).</summary>
    [UnmanagedCallersOnly]
    public static int BlockDeviceGeneration()
    {
        return BlockDeviceRegistry.Generation();
    }

    /// <summary>Fills the caller's info struct (0 on success, -1 on error).</summary>
    [UnmanagedCallersOnly]
    public static int BlockDeviceInfoAt(int index, RawBlockDeviceInfo* info)
    {
        int handle = BlockDeviceRegistry.HandleAt(index);
        if (handle < 0 || info == null)
            return -1;
        var d = BlockDeviceRegistry.Get(handle);
        if (d == null)
            return -1;
        info->Handle = handle;
        info->SectorCount = d.SectorCount;
        info->SectorSize = d.SectorSize;
        info->Removable = d.Removable ? 1 : 0;
        return 0;
    }

    // ---- auto-mount notifications (DDK AutoMount -> shell completion) ----

    /// <summary>Records an auto-mount (device name + mount path; UTF-16).</summary>
    [UnmanagedCallersOnly]
    public static int AutoMountNote(char* device, int deviceLen, char* path, int pathLen)
    {
        if (device == null || path == null || deviceLen < 0 || pathLen < 0)
            return -1;
        string dev = new string(device, 0, deviceLen);
        string mnt = new string(path, 0, pathLen);
        BlockDeviceRegistry.NoteAutoMount(dev, mnt);
        return 0;
    }

    /// <summary>Drops an auto-mount record (device name; UTF-16).</summary>
    [UnmanagedCallersOnly]
    public static int AutoMountForget(char* device, int deviceLen)
    {
        if (device == null || deviceLen < 0)
            return -1;
        string dev = new string(device, 0, deviceLen);
        BlockDeviceRegistry.ForgetAutoMount(dev);
        return 0;
    }
}
