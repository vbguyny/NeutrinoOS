// NeutrinoOS kernel - ARM64 UEFI boot setup (Phase 8, Task 3).
//
// The x64 flow receives a fully-formed BootInfo from the NASM loader
// (raw memory map, loaded files, RSDP). ARM64 boots single-stage as its
// own UEFI application, so this file builds the same BootInfo from the
// live UEFI boot services before the kernel consumes it:
//
//   - memory map: raw UEFI descriptors straight from GetMemoryMap (this
//     is exactly the format PageAllocator/DumpMemoryMap consume).
//     Regions owned by the firmware / boot services / loader are
//     re-typed as Reserved because this model does NOT call
//     ExitBootServices in this increment - only ConventionalMemory stays
//     reclaimable, so the allocator can never stomp live firmware state.
//   - kernel image range: EFI_LOADED_IMAGE_PROTOCOL ImageBase/ImageSize.
//   - RSDP: scanned from the system table's configuration table
//     (ACPI 2.0 GUID first, ACPI 1.0 as fallback).
//   - loaded files: none (the JIT world and /drivers payloads are
//     x64-only for now).
//
// The finished structure lives in UEFI pool memory (LoaderData) and its
// pointer is stored into the native slot that BootInfoAccess reads.

#if ARCH_ARM64

using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProtonOS.Platform;

/// <summary>Builds an ARM64 BootInfo from the live UEFI boot services.</summary>
public static unsafe class Arm64BootSetup
{
    // Same contract as x64 native.asm: get_boot_info returns the
    // BootInfo* value stored in the slot; set_boot_info stores it.
    [MethodImpl(MethodImplOptions.InternalCall)]
    [RuntimeImport("*", "get_boot_info")]
    private static extern BootInfo* GetBootInfo();

    [MethodImpl(MethodImplOptions.InternalCall)]
    [RuntimeImport("*", "set_boot_info")]
    private static extern void SetBootInfo(BootInfo* info);

    private static void InitAcpi20Guid(EFIGUID* g)
    {
        // {8868E871-E4F1-11D3-BC22-0080C73C8881}
        g->Data1 = 0x8868E871;
        g->Data2 = 0xE4F1;
        g->Data3 = 0x11D3;
        g->Data4[0] = 0xBC; g->Data4[1] = 0x22; g->Data4[2] = 0x00; g->Data4[3] = 0x80;
        g->Data4[4] = 0xC7; g->Data4[5] = 0x3C; g->Data4[6] = 0x88; g->Data4[7] = 0x81;
    }

    private static void InitAcpi10Guid(EFIGUID* g)
    {
        // {EB9D2D30-2D88-11D3-9A16-0090273FC1FD}
        g->Data1 = 0xEB9D2D30;
        g->Data2 = 0x2D88;
        g->Data3 = 0x11D3;
        g->Data4[0] = 0x9A; g->Data4[1] = 0x16; g->Data4[2] = 0x00; g->Data4[3] = 0x90;
        g->Data4[4] = 0x27; g->Data4[5] = 0x3F; g->Data4[6] = 0xC1; g->Data4[7] = 0xFD;
    }

    private static bool GuidMatches(EFIGUID* a, EFIGUID* b)
    {
        if (a->Data1 != b->Data1 || a->Data2 != b->Data2 || a->Data3 != b->Data3)
            return false;
        for (int i = 0; i < 8; i++)
        {
            if (a->Data4[i] != b->Data4[i])
                return false;
        }
        return true;
    }

    /// <summary>Ensure a valid BootInfo exists; build one from UEFI if not.</summary>
    public static void EnsureBootInfo()
    {
        BootInfo* existing = GetBootInfo();
        if (existing != null && existing->IsValid)
            return;

        // The korlib EFI_SYSTEM_TABLE (stored by EfiMain) has the same
        // layout as the kernel's EFISystemTable.
        EFISystemTable* st = (EFISystemTable*)System.Object.EfiSystemTable;
        if (st == null || st->BootServices == null)
        {
            DebugConsole.WriteLine("[BootInfo] ARM64: UEFI services unavailable");
            return;
        }

        EFIBootServices* bs = st->BootServices;

        // --- memory map (raw UEFI descriptors) ---
        ulong mapSize = 0;
        ulong mapKey = 0;
        ulong descSize = 0;
        uint descVer = 0;
        EFIStatus probe = bs->GetMemoryMap(&mapSize, null, &mapKey, &descSize, &descVer);
        if (probe != EFIStatus.BufferTooSmall || descSize == 0 || mapSize == 0)
        {
            DebugConsole.WriteLine("[BootInfo] ARM64: GetMemoryMap probe failed");
            return;
        }

        // Slack for the pool allocations this routine makes before the
        // real map call (each changes the map).
        mapSize += descSize * 64;

        void* raw = null;
        if (bs->AllocatePool(EFIMemoryType.LoaderData, mapSize, &raw) != EFIStatus.Success || raw == null)
        {
            DebugConsole.WriteLine("[BootInfo] ARM64: AllocatePool (memory map) failed");
            return;
        }
        EFIMemoryDescriptor* mapBuf = (EFIMemoryDescriptor*)raw;

        bool gotMap = false;
        for (int attempt = 0; attempt < 3 && !gotMap; attempt++)
        {
            ulong sz = mapSize;
            if (bs->GetMemoryMap(&sz, mapBuf, &mapKey, &descSize, &descVer) == EFIStatus.Success)
            {
                mapSize = sz;
                gotMap = true;
            }
        }
        if (!gotMap || descSize == 0)
        {
            DebugConsole.WriteLine("[BootInfo] ARM64: GetMemoryMap failed");
            return;
        }

        uint entryCount = (uint)(mapSize / descSize);

        // Keep only ConventionalMemory reclaimable: boot services are
        // still live in this increment, so loader/boot-service memory must
        // stay reserved (the x64 flow could reclaim it only after
        // ExitBootServices).
        for (uint i = 0; i < entryCount; i++)
        {
            EFIMemoryDescriptor* d = UEFIBoot.GetDescriptor((byte*)mapBuf, descSize, (int)i);
            EFIMemoryType t = d->Type;
            if (t == EFIMemoryType.LoaderCode || t == EFIMemoryType.LoaderData ||
                t == EFIMemoryType.BootServicesCode || t == EFIMemoryType.BootServicesData)
            {
                d->Type = EFIMemoryType.ReservedMemoryType;
            }
        }

        // --- kernel image range (for the page allocator's reservation) ---
        ulong imageBase = 0;
        ulong imageSize = 0;

        EFIGUID liGuid;
        liGuid.Data1 = 0x5B1B31A1;
        liGuid.Data2 = 0x9562;
        liGuid.Data3 = 0x11D2;
        liGuid.Data4[0] = 0x8E; liGuid.Data4[1] = 0x3F; liGuid.Data4[2] = 0x00; liGuid.Data4[3] = 0xA0;
        liGuid.Data4[4] = 0xC9; liGuid.Data4[5] = 0x69; liGuid.Data4[6] = 0x72; liGuid.Data4[7] = 0x3B;

        EFILoadedImageProtocol* li = null;
        if (bs->HandleProtocol(System.Object.EfiImageHandle, &liGuid, (void**)&li) == EFIStatus.Success && li != null)
        {
            imageBase = (ulong)li->ImageBase;
            imageSize = li->ImageSize;
        }

        // --- RSDP from the configuration table ---
        ulong rsdp = 0;
        EFIGUID acpi20;
        InitAcpi20Guid(&acpi20);
        EFIGUID acpi10;
        InitAcpi10Guid(&acpi10);

        byte* ct = (byte*)st->ConfigurationTable;
        for (ulong i = 0; i < st->NumberOfTableEntries && ct != null; i++)
        {
            EFIGUID* g = (EFIGUID*)(ct + i * 24);            // 16-byte GUID
            void* vt = *(void**)(ct + i * 24 + 16);          // + 8-byte pointer
            if (GuidMatches(g, &acpi20))
            {
                rsdp = (ulong)vt;
                break;
            }
            if (rsdp == 0 && GuidMatches(g, &acpi10))
                rsdp = (ulong)vt;
        }

        // --- BootInfo ---
        void* infoRaw = null;
        if (bs->AllocatePool(EFIMemoryType.LoaderData, (ulong)sizeof(BootInfo), &infoRaw) != EFIStatus.Success || infoRaw == null)
        {
            DebugConsole.WriteLine("[BootInfo] ARM64: AllocatePool (BootInfo) failed");
            return;
        }

        BootInfo* info = (BootInfo*)infoRaw;
        byte* infoBytes = (byte*)info;
        for (int i = 0; i < sizeof(BootInfo); i++)
            infoBytes[i] = 0;

        info->Magic = BootInfoConstants.Magic;
        info->Version = BootInfoConstants.Version;
        info->Flags = 0;
        info->MemoryMapAddress = (ulong)mapBuf;
        info->MemoryMapEntries = entryCount;
        info->MemoryMapEntrySize = (uint)descSize;
        info->KernelPhysicalBase = imageBase;
        info->KernelVirtualBase = imageBase;
        info->KernelSize = imageSize;
        info->KernelEntryOffset = 0;
        info->LoadedFilesAddress = 0;
        info->LoadedFilesCount = 0;
        info->AcpiRsdp = rsdp;
        info->SerialPort = Pl011.Base;

        SetBootInfo(info);

        DebugConsole.Write("[BootInfo] ARM64 built from UEFI: ");
        DebugConsole.WriteDecimal(entryCount);
        DebugConsole.Write(" map entries, rsdp=0x");
        DebugConsole.WriteHex(rsdp);
        DebugConsole.Write(", image=0x");
        DebugConsole.WriteHex(imageBase);
        DebugConsole.Write(" size=0x");
        DebugConsole.WriteHex(imageSize);
        DebugConsole.WriteLine();
    }
}

#endif
