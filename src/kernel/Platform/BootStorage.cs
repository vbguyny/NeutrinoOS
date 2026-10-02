// Boot-volume storage driver selection (Platform.BootStorage).
//
// FileExports (the System.IO kernel bridge) and AssemblyRunner (`run`)
// JIT-resolve their boot-file helpers from a storage driver entry that
// exposes a fixed static method set (GetBootFileSize, ReadBootFile,
// WriteBootFile, DeleteBootFile, CreateBootDir, DeleteBootDir,
// BootPathExists, ListBootDirEntry, GetBootVolumeStats). Historically
// that was always the AHCI driver; QEMU configurations that attach the
// boot disk to virtio-blk have no AHCI device, so every System.IO call
// failed there ("cd /apps" -> no such directory) even though the kernel
// itself booted fine via UEFI.
//
// This class picks the driver that actually carries the FAT boot volume:
// AHCI is probed first (GetBootFatDevice FAT-probes its drives, so an
// exFAT data disk does not win), then virtio-blk. The choice is cached
// once made; before that, callers simply retry - drivers bind during
// boot and the helpers are resolved lazily on first use.

using System.Runtime.InteropServices;
using NeutrinoOS.Runtime;
using NeutrinoOS.Runtime.JIT;

namespace NeutrinoOS.Platform;

/// <summary>Selects the storage driver that serves the FAT boot volume.</summary>
public static class BootStorage
{
    private static uint _asmId;         // 0 (InvalidAssemblyId) until chosen
    private static uint _entryToken;
    private static bool _resolveInProgress;

    /// <summary>
    /// Resolves the assembly id and entry type token of the boot-volume
    /// driver (cached). Returns false while no candidate reports a FAT
    /// volume yet - callers retry on their next lazy-resolve attempt.
    /// </summary>
    public static bool TryResolve(out uint asmId, out uint entryToken)
    {
        if (_asmId != 0)
        {
            asmId = _asmId;
            entryToken = _entryToken;
            return true;
        }

        // Reentrancy: JIT-compiling HasBootVolume can trigger assembly
        // resolution that routes back here; fail fast, the outer call
        // keeps going.
        if (_resolveInProgress)
        {
            asmId = 0;
            entryToken = 0;
            return false;
        }

        _resolveInProgress = true;
        try
        {
            if (!TryCandidate(Kernel.AhciDriverAssemblyId,
                    "NeutrinoOS.Drivers.Storage.Ahci", "AhciEntry") &&
                !TryCandidate(Kernel.VirtioBlkDriverAssemblyId,
                    "NeutrinoOS.Drivers.Storage.VirtioBlk", "VirtioBlkEntry"))
            {
                asmId = 0;
                entryToken = 0;
                return false;
            }
        }
        finally
        {
            _resolveInProgress = false;
        }

        asmId = _asmId;
        entryToken = _entryToken;
        return true;
    }

    /// <summary>
    /// Probes one candidate driver: JIT-compiles its HasBootVolume() and
    /// calls it. On 1 the driver is selected and cached; returns false
    /// when the driver is absent, unbound or has no FAT volume.
    /// </summary>
    private static unsafe bool TryCandidate(uint asmId, string ns, string typeName)
    {
        if (asmId == AssemblyLoader.InvalidAssemblyId)
            return false;

        uint typeToken = AssemblyLoader.FindTypeDefByFullName(asmId, ns, typeName);
        if (typeToken == 0)
            return false;

        uint hasVolumeToken = AssemblyLoader.FindMethodDefByName(asmId, typeToken, "HasBootVolume");
        if (hasVolumeToken == 0)
            return false;

        var jit = Tier0JIT.CompileMethod(asmId, hasVolumeToken);
        if (!jit.Success || jit.CodeAddress == null)
            return false;

        var hasVolume = (delegate* unmanaged<int>)jit.CodeAddress;
        if (hasVolume() != 1)
            return false;

        _asmId = asmId;
        _entryToken = typeToken;
        return true;
    }
}
