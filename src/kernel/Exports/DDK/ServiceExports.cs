// NeutrinoOS kernel - DDK service exports (Phase 6)
//
// Kernel_ServiceStart / Kernel_ServiceStop: let shell utilities (e.g.
// `sshd`, `webhost`) start cooperative background services; the kernel
// compiles their entry points from the DDK assembly by name.

using System.Runtime.InteropServices;
using NeutrinoOS.Services;

namespace NeutrinoOS.Exports.DDK;

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

    /// <summary>
    /// Launch a web app DLL as a cooperative hosted app (M4). The blob is
    /// "\0"-separated tokens: DLL path first, then the app's arguments.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "Kernel_ServiceStartApp")]
    public static int ServiceStartApp(byte* blob, int length)
    {
        if (blob == null || length <= 0 || length > 1024)
            return -1;

        int count = 1;
        for (int i = 0; i < length; i++)
        {
            if (blob[i] == 0)
                count++;
        }

        var tokens = new string[count];
        int start = 0;
        int t = 0;
        for (int i = 0; i <= length; i++)
        {
            if (i == length || blob[i] == 0)
            {
                var chars = new char[i - start];
                for (int j = 0; j < chars.Length; j++)
                    chars[j] = (char)blob[start + j];
                tokens[t] = new string(chars);
                t++;
                start = i + 1;
            }
        }
        return ServiceRegistry.StartApp(tokens);
    }
}
