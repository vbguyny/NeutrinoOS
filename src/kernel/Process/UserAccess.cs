// NeutrinoOS kernel - user-pointer validation (Phase 7 security)
//
// Syscalls that write into caller-supplied buffers must first verify
// that the pointer + length describe a range inside the canonical user
// address window. Without this, a ring-3 process could hand the kernel
// an arbitrary pointer (including kernel addresses) and have the kernel
// write to it on the process's behalf.
//
// This is a range check, not a page-table walk: everything below the
// kernel half is governed by the process page tables, so an address
// inside the user window that is not mapped still faults safely in user
// context rather than corrupting kernel memory. (Kernel-mode faults on
// unmapped user pages are a documented residual risk - see
// docs/PHASE7-SECURITY.md.)

using System.Runtime.CompilerServices;

namespace ProtonOS.Process;

/// <summary>Ring-3 pointer range validation for kernel syscall handlers.</summary>
public static unsafe class UserAccess
{
    /// <summary>Lowest user address callers may pass (null-page guard).</summary>
    public const ulong UserMin = 0x10000;

    /// <summary>
    /// True when [addr, addr+len) lies entirely inside the user address
    /// window. Rejects null, len == 0, non-user pointers, and wraparound.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Valid(void* addr, ulong len)
    {
        ulong a = (ulong)addr;
        if (a < UserMin || len == 0)
            return false;
        if (a > UserLayout.UserEnd)
            return false;
        // Overflow-safe: reject when the end would pass UserEnd.
        if (len > UserLayout.UserEnd - a + 1)
            return false;
        return true;
    }
}
