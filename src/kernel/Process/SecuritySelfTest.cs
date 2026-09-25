// NeutrinoOS kernel - security self-test (Phase 7)
//
// Boot-time verification that the privilege boundary holds by
// construction. The walks run against the live kernel page tables: a
// user address space shares these entries (CreateUserSpace copies
// PML4[0] and PML4[256..511] into every process), so what is
// supervisor-only here is supervisor-only for ring 3 too:
//
//   [SEC] user cannot read kernel identity memory
//         (leaf mapping at 0x0100_0000 must be supervisor-only)
//   [SEC] user cannot read the kernel physmap
//         (leaf at PhysicalMapBase must be supervisor-only)
//   [SEC] kernel image is executable (W^X: kernel code has NX clear)
//   [SEC] ordinary RAM is NX (W^X: 2MB page past the image is NX)
//
// Output lines are stable ("[SEC] PASS ...") so the Phase 7 acceptance
// script can assert on them.

using ProtonOS.Memory;
using ProtonOS.Platform;
using ProtonOS.Arch;

namespace ProtonOS.Process;

/// <summary>Boot-time security invariant checks (see file header).</summary>
public static unsafe class SecuritySelfTest
{
    private static int _pass;
    private static int _fail;

    /// <summary>Run all checks and print [SEC] result lines.</summary>
    public static void Run()
    {
        _pass = 0;
        _fail = 0;

        DebugConsole.WriteLine("[SEC] Security self-test:");

        // The checks walk the LIVE kernel page tables. User address
        // spaces share these entries verbatim - CreateUserSpace copies
        // PML4[0] (low identity map) and PML4[256..511] (kernel half)
        // into every process - so a leaf that is supervisor-only here is
        // supervisor-only for ring 3 as well. (An earlier version built
        // and destroyed a scratch address space; destroying it freed
        // page-table pages shared with the kernel, so the walk runs
        // in-place instead.)
        ulong pml4 = VirtualMemory.Pml4Address;
        if (pml4 == 0)
        {
            DebugConsole.WriteLine("[SEC] WARN: paging not initialized; skipping");
            return;
        }

        // 1. Kernel identity memory (low 256MB) must be supervisor-only.
        CheckSupervisorOnly(pml4, 0x01000000, "user cannot read kernel identity memory");

        // 2. The physmap window must be supervisor-only.
        CheckSupervisorOnly(pml4, VirtualMemory.PhysicalMapBase, "user cannot read the kernel physmap");

        // 3. Kernel image executable; ordinary RAM NX (live kernel CR3,
        //    identity-mapped 2MB pages).
        CheckKernelImageWx();

        DebugConsole.Write("[SEC] result: ");
        DebugConsole.WriteDecimal(_pass);
        DebugConsole.Write(" pass, ");
        DebugConsole.WriteDecimal(_fail);
        DebugConsole.WriteLine(" fail");
    }

    private static void CheckSupervisorOnly(ulong pml4Phys, ulong va, string what)
    {
        ulong leaf = WalkLeaf(pml4Phys, va);
        if (leaf == 0)
        {
            // Not mapped at all: inaccessible to ring 3, which is even safer.
            Pass(what + " (unmapped)");
            return;
        }
        if ((leaf & PageFlags.User) != 0)
            Fail(what + " - LEAK: mapping is user-accessible");
        else
            Pass(what);
    }

    private static void CheckKernelImageWx()
    {
        var bootInfo = Platform.BootInfoAccess.Get();
        ulong kernelStart = bootInfo != null ? bootInfo->KernelPhysicalBase : 0;
        if (kernelStart == 0)
        {
            DebugConsole.WriteLine("[SEC] WARN: boot info unavailable; skipping W^X checks");
            return;
        }
        // 2MB-aligned base of the image, as re-mapped by ProtectKernelImage.
        ulong imageVa = kernelStart & ~0x1FFFFFUL;

        ulong imageLeaf = WalkLeaf(VirtualMemory.Pml4Address, imageVa);
        if (imageLeaf != 0 && (imageLeaf & PageFlags.NoExecute) == 0)
            Pass("kernel image is executable (W^X)");
        else if (imageLeaf == 0)
            DebugConsole.WriteLine("[SEC] WARN: kernel image mapping not found (skipped)");
        else
            Fail("kernel image is NOT executable (W^X broken)");

        // A 2MB page well past the image (e.g. 0x2000_0000 = 512MB is RAM
        // on a 2GB machine) must be NX when mapped.
        ulong ramVa = 0x20000000UL;
        ulong ramLeaf = WalkLeaf(VirtualMemory.Pml4Address, ramVa);
        if (ramLeaf != 0 && (ramLeaf & PageFlags.NoExecute) != 0)
            Pass("ordinary RAM is NX (W^X)");
        else if (ramLeaf == 0)
            DebugConsole.WriteLine("[SEC] WARN: 512MB RAM mapping not found (skipped)");
        else
            Fail("ordinary RAM is executable (W^X broken)");
    }

    /// <summary>
    /// Walk the page tables of <paramref name="pml4Phys"/> for
    /// <paramref name="va"/> and return the leaf entry (PTE or 2MB PDE),
    /// or 0 when unmapped. Handles the identity-mapped physical tables
    /// (physical == virtual below 256MB) plus the higher-half physmap.
    /// </summary>
    private static ulong WalkLeaf(ulong pml4Phys, ulong va)
    {
        if (pml4Phys == 0)
            return 0;

        ulong* pml4 = (ulong*)TableVirt(pml4Phys);
        ulong e = pml4[(int)((va >> 39) & 0x1FF)];
        if ((e & PageFlags.Present) == 0)
            return 0;
        if ((e & 0x80) != 0)          // 1GB page (PS bit) - unlikely, treat as leaf
            return e;

        ulong* pdpt = (ulong*)TableVirt(e & 0x000FFFFFFFFFF000UL);
        e = pdpt[(int)((va >> 30) & 0x1FF)];
        if ((e & PageFlags.Present) == 0)
            return 0;
        if ((e & 0x80) != 0)          // 1GB page
            return e;

        ulong* pd = (ulong*)TableVirt(e & 0x000FFFFFFFFFF000UL);
        e = pd[(int)((va >> 21) & 0x1FF)];
        if ((e & PageFlags.Present) == 0)
            return 0;
        if ((e & 0x80) != 0)          // 2MB page
            return e;

        ulong* pt = (ulong*)TableVirt(e & 0x000FFFFFFFFFF000UL);
        e = pt[(int)((va >> 12) & 0x1FF)];
        if ((e & PageFlags.Present) == 0)
            return 0;
        return e;
    }

    /// <summary>
    /// Physical address of a page-table page as a dereferenceable pointer.
    /// Low RAM is identity-mapped; beyond it the physmap window applies.
    /// </summary>
    private static ulong TableVirt(ulong phys)
    {
        if (phys < 0x10000000UL)      // 256MB: covered by the identity map
            return phys;
        return VirtualMemory.PhysToVirt(phys);
    }

    private static void Pass(string what)
    {
        _pass++;
        DebugConsole.Write("[SEC] PASS ");
        DebugConsole.WriteLine(what);
    }

    private static void Fail(string what)
    {
        _fail++;
        DebugConsole.Write("[SEC] FAIL ");
        DebugConsole.WriteLine(what);
    }
}
