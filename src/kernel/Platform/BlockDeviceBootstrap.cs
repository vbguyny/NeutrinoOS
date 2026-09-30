// NeutrinoOS kernel - block device bootstrap (Phase 10 Task 4).
//
// After the storage drivers bind, their sector-I/O statics are JIT'd
// here and each attached drive is registered in the kernel block device
// registry (same JIT-and-store-function-pointer idiom as
// Platform.FileExports and Services.ServiceRegistry). Utilities and the
// exFAT driver then reach every disk through the Kernel_BlockDevice*
// exports:
//
//   hda, hdb, ...   AHCI drives (port/drive order)
//   nvme0           NVMe namespace 1
//   sda, sda1, ...  USB mass storage (registered on attach in UsbStorage)

using System;
using NeutrinoOS.Runtime;
using NeutrinoOS.Runtime.JIT;
using NeutrinoOS.Storage;

namespace NeutrinoOS.Platform;

/// <summary>Registers bound storage drivers in the block device registry.</summary>
public static unsafe class BlockDeviceBootstrap
{
    private static bool _ahciDone;
    private static bool _nvmeDone;

    /// <summary>Drive names for AHCI indexes 0..7 (extend if needed).</summary>
    private static readonly string[] AhciNames =
        { "hda", "hdb", "hdc", "hdd", "hde", "hdf", "hdg", "hdh" };

    /// <summary>
    /// JITs the AHCI block-device statics and registers one device per
    /// bound drive. Safe to call repeatedly (runs once).
    /// </summary>
    public static void RegisterAhciDevices(uint driverId)
    {
        if (_ahciDone || driverId == AssemblyLoader.InvalidAssemblyId)
            return;

        uint typeToken = AssemblyLoader.FindTypeDefByFullName(
            driverId, "NeutrinoOS.Drivers.Storage.Ahci", "AhciEntry");
        if (typeToken == 0)
        {
            Console.WriteLine("[Blocks] WARNING: AhciEntry not found for registration");
            return;
        }

        void* fnCount = Jit(driverId, typeToken, "BlockDeviceCount");
        void* fnSectors = Jit(driverId, typeToken, "BlockDeviceSectorCount");
        void* fnSize = Jit(driverId, typeToken, "BlockDeviceSectorSize");
        void* fnRead = Jit(driverId, typeToken, "BlockDeviceRead");
        void* fnWrite = Jit(driverId, typeToken, "BlockDeviceWrite");
        void* fnFlush = Jit(driverId, typeToken, "BlockDeviceFlush");
        if (fnCount == null || fnSectors == null || fnSize == null
            || fnRead == null || fnWrite == null || fnFlush == null)
        {
            Console.WriteLine("[Blocks] WARNING: AHCI registry statics failed to JIT");
            return;
        }

        int count = ((delegate* unmanaged<int>)fnCount)();
        for (int i = 0; i < count; i++)
        {
            ulong sectors = ((delegate* unmanaged<int, ulong>)fnSectors)(i);
            if (sectors == 0)
                continue;
            uint sectorSize = ((delegate* unmanaged<int, uint>)fnSize)(i);
            string name = i < AhciNames.Length ? AhciNames[i] : "hd" + i.ToString();
            BlockDeviceRegistry.Register(name, BlockDeviceRegistry.KindAhci, i,
                sectors, sectorSize, false, fnRead, fnWrite, fnFlush);
        }

        _ahciDone = true;
    }

    /// <summary>
    /// JITs the NVMe block-device statics and registers the namespace
    /// as "nvme0". Safe to call repeatedly (runs once).
    /// </summary>
    public static void RegisterNvmeDevice(uint driverId)
    {
        if (_nvmeDone || driverId == AssemblyLoader.InvalidAssemblyId)
            return;

        uint typeToken = AssemblyLoader.FindTypeDefByFullName(
            driverId, "NeutrinoOS.Drivers.Storage.Nvme", "NvmeEntry");
        if (typeToken == 0)
        {
            Console.WriteLine("[Blocks] WARNING: NvmeEntry not found for registration");
            return;
        }

        void* fnCount = Jit(driverId, typeToken, "BlockDeviceCount");
        void* fnSectors = Jit(driverId, typeToken, "BlockDeviceSectorCount");
        void* fnSize = Jit(driverId, typeToken, "BlockDeviceSectorSize");
        void* fnRead = Jit(driverId, typeToken, "BlockDeviceRead");
        void* fnWrite = Jit(driverId, typeToken, "BlockDeviceWrite");
        void* fnFlush = Jit(driverId, typeToken, "BlockDeviceFlush");
        if (fnCount == null || fnSectors == null || fnSize == null
            || fnRead == null || fnWrite == null || fnFlush == null)
        {
            Console.WriteLine("[Blocks] WARNING: NVMe registry statics failed to JIT");
            return;
        }

        int count = ((delegate* unmanaged<int>)fnCount)();
        if (count <= 0)
            return;

        ulong sectors = ((delegate* unmanaged<int, ulong>)fnSectors)(0);
        if (sectors == 0)
            return;
        uint sectorSize = ((delegate* unmanaged<int, uint>)fnSize)(0);
        BlockDeviceRegistry.Register("nvme0", BlockDeviceRegistry.KindNvme, 0,
            sectors, sectorSize, false, fnRead, fnWrite, fnFlush);

        _nvmeDone = true;
    }

    /// <summary>JIT-compiles one static method and returns its address.</summary>
    private static void* Jit(uint driverId, uint typeToken, string methodName)
    {
        uint token = AssemblyLoader.FindMethodDefByName(driverId, typeToken, methodName);
        if (token == 0)
            return null;
        var result = Tier0JIT.CompileMethod(driverId, token);
        if (!result.Success || result.CodeAddress == null)
            return null;
        return result.CodeAddress;
    }
}
