// NeutrinoOS kernel - DDK service exports (Phase 6)
//
// Kernel_ServiceStart / Kernel_ServiceStop: let shell utilities (e.g.
// `sshd`, `webhost`) start cooperative background services; the kernel
// compiles their entry points from the DDK assembly by name.

using System.Runtime.InteropServices;
using ProtonOS.Services;

namespace ProtonOS.Exports.DDK;

/// <summary>DDK service-management exports (see file header).</summary>
public static unsafe class ServiceExports
{
    /// <summary>Start a named service; 0 = ok. Name is ASCII with an explicit length.</summary>
    [UnmanagedCallersOnly(EntryPoint = "Kernel_ServiceStart")]
    public static int ServiceStart(byte* name, int length)
    {
        if (name == null || length <= 0 || length > 64)
            return -1;
        var chars = new char[length];
        for (int i = 0; i < length; i++)
            chars[i] = (char)name[i];
        return ServiceRegistry.Start(new string(chars));
    }

    /// <summary>Stop a named service; 0 = ok.</summary>
    [UnmanagedCallersOnly(EntryPoint = "Kernel_ServiceStop")]
    public static int ServiceStop(byte* name, int length)
    {
        if (name == null || length <= 0 || length > 64)
            return -1;
        var chars = new char[length];
        for (int i = 0; i < length; i++)
            chars[i] = (char)name[i];
        return ServiceRegistry.Stop(new string(chars));
    }
}
