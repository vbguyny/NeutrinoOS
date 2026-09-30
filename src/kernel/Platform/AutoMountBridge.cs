// NeutrinoOS kernel - auto-mount bridge (Phase 10 Task 4).
//
// JIT-compiles the DDK's AutoMount.Tick once and calls it from the
// shell idle hook: removable disks carrying exFAT volumes are mounted
// under /mnt/usb/<device> and unmounted on removal (same JIT-and-call
// idiom as Services.ServiceRegistry). The mount itself happens inside
// the DDK, so it lands in the shared VFS and every utility sees it.

using System;
using NeutrinoOS.Runtime;
using NeutrinoOS.Runtime.JIT;

namespace NeutrinoOS.Platform;

/// <summary>Polls the DDK USB auto-mount service (see file header).</summary>
public static unsafe class AutoMountBridge
{
    private static void* _tickFn;
    private static bool _failed;

    /// <summary>
    /// One poll step. JITs the DDK entry on first use; silently idle
    /// until the DDK assembly is available (and after a JIT failure,
    /// to avoid retry churn on every idle iteration).
    /// </summary>
    public static void Poll()
    {
        if (_tickFn == null)
        {
            if (_failed || !EnsureJitted())
                return;
        }
        var tick = (delegate* unmanaged<int>)_tickFn;
        tick();
    }

    /// <summary>JIT-compiles NeutrinoOS.DDK.Storage.ExFat.AutoMount.Tick.</summary>
    private static bool EnsureJitted()
    {
        uint ddkId = Kernel.DdkAssemblyId;
        if (ddkId == AssemblyLoader.InvalidAssemblyId)
            return false;

        uint typeToken = AssemblyLoader.FindTypeDefByFullName(
            ddkId, "NeutrinoOS.DDK.Storage.ExFat", "AutoMount");
        if (typeToken == 0)
        {
            Console.WriteLine("[automount] WARNING: AutoMount type not found in DDK");
            _failed = true;
            return false;
        }

        uint tickToken = AssemblyLoader.FindMethodDefByName(ddkId, typeToken, "Tick");
        if (tickToken == 0)
        {
            Console.WriteLine("[automount] WARNING: AutoMount.Tick not found");
            _failed = true;
            return false;
        }

        var result = Tier0JIT.CompileMethod(ddkId, tickToken);
        if (!result.Success || result.CodeAddress == null)
        {
            Console.WriteLine("[automount] WARNING: AutoMount.Tick failed to JIT");
            _failed = true;
            return false;
        }

        _tickFn = result.CodeAddress;
        return true;
    }
}
