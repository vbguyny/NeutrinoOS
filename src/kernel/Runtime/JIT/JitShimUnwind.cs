// NeutrinoOS JIT - Unwind info registration for the native assembly shims
//
// The JIT-emitted call sites route their calls through the alignment shims in
// native.asm (jit_align_call, jit_ensure_compiled_shim, jit_new_*_shim, ...).
// Each shim establishes a frame pointer with
//
//     push rbp
//     mov  rbp, rsp
//     and  rsp, -16        <- no x64 unwind op can express this
//     sub  rsp, 32
//     call <target>
//     mov  rsp, rbp
//     pop  rbp
//     ret
//
// Even though the RSP adjustment cannot be encoded directly, the frame is
// still fully recoverable: the unwind codes below undo `push rbp` and
// `mov rbp, rsp` (UWOP_SET_FPREG), and the unwinder rebuilds RSP from RBP -
// which makes the `and`/`sub` adjustment irrelevant for any RIP past the
// prolog. RBP itself is callee-saved and preserved through the target call.
//
// Without these RUNTIME_FUNCTION entries the kernel stack walker
// (StackRoots.EnumerateStackRoots, used by GC mark AND compaction, and by
// exception unwinding) cannot unwind a shim frame. It then falls back to
// "leaf function - pop one return address", which is wrong for these
// frames, and the walk wanders through the raw stack. A compacting GC
// triggered while such a walk is in progress (e.g. the fragmentation
// caused by compiling a method mid-call) then rewrites GC slot addresses
// computed from a bogus frame base - corrupting live caller frames.

using System.Runtime.InteropServices;
using NeutrinoOS.Arch;
using NeutrinoOS.Memory;
using NeutrinoOS.Platform;
using NeutrinoOS.Runtime;

namespace NeutrinoOS.Runtime.JIT;

public static unsafe class JitShimUnwind
{
    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint jit_align_call_addr();

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint jit_ensure_compiled_shim_addr();

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint jit_ensure_virtual_compiled_shim_addr();

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint jit_ensure_vtable_slot_compiled_shim_addr();

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint jit_get_interface_method_shim_addr();

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint jit_new_fast_shim_addr();

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint jit_new_array_shim_addr();

    /// <summary>
    /// Register RUNTIME_FUNCTION/UNWIND_INFO entries for every alignment
    /// shim so stack walks can traverse shim frames. Must be called after
    /// JitStubs.Init() has resolved the shim addresses.
    /// </summary>
    public static void Register()
    {
        var bootInfo = BootInfoAccess.Get();
        if (bootInfo == null || !bootInfo->IsValid)
            return;
        ulong imageBase = bootInfo->KernelPhysicalBase;
        if (imageBase == 0)
            return;

        // Prolog: push rbp (1) + mov rbp,rsp (3) + and rsp,-16 (4) + sub rsp,32 (4)
        // Only the two frame-establishing ops are encoded; with
        // UWOP_SET_FPREG present the unwinder rebuilds RSP from RBP, so the
        // `and`/`sub` adjustment (not expressible in unwind codes) is moot
        // for every RIP beyond the prolog.
        const byte shimPrologSize = 12;

        // Shim bodies are ~22 bytes; 0x18 covers them incl. the trailing ret.
        const uint shimBodySize = 0x18;

        byte* unwind = CodeHeap.Alloc(8);
        RuntimeFunction* functions = (RuntimeFunction*)CodeHeap.Alloc(
            (ulong)(7 * sizeof(RuntimeFunction)));
        if (unwind == null || functions == null)
        {
            DebugConsole.WriteLine("[JitShimUnwind] Failed to allocate unwind tables");
            return;
        }

        // UNWIND_INFO: version 1, no flags, frame register RBP with offset 0.
        unwind[0] = 0x01;                                  // VersionAndFlags
        unwind[1] = shimPrologSize;                        // SizeOfProlog
        unwind[2] = 2;                                     // CountOfUnwindCodes
        unwind[3] = (byte)UnwindRegister.RBP;              // FrameReg=RBP, offset 0

        // Codes are ordered latest-prolog-op first.
        unwind[4] = 4;                                     // end of `mov rbp, rsp`
        unwind[5] = UnwindOpCodes.UWOP_SET_FPREG;
        unwind[6] = 1;                                     // end of `push rbp`
        unwind[7] = (byte)((UnwindRegister.RBP << 4) | UnwindOpCodes.UWOP_PUSH_NONVOL);

        uint unwindRva = (uint)((ulong)unwind - imageBase);

        // Collect shim entry addresses and sort ascending: LookupFunctionEntry
        // binary-searches the table by RVA.
        ulong* addrs = stackalloc ulong[7];
        addrs[0] = (ulong)jit_align_call_addr();
        addrs[1] = (ulong)jit_ensure_compiled_shim_addr();
        addrs[2] = (ulong)jit_ensure_virtual_compiled_shim_addr();
        addrs[3] = (ulong)jit_ensure_vtable_slot_compiled_shim_addr();
        addrs[4] = (ulong)jit_get_interface_method_shim_addr();
        addrs[5] = (ulong)jit_new_fast_shim_addr();
        addrs[6] = (ulong)jit_new_array_shim_addr();

        for (int i = 1; i < 7; i++)
        {
            ulong key = addrs[i];
            int j = i - 1;
            while (j >= 0 && addrs[j] > key)
            {
                addrs[j + 1] = addrs[j];
                j--;
            }
            addrs[j + 1] = key;
        }

        for (int i = 0; i < 7; i++)
        {
            uint begin = (uint)(addrs[i] - imageBase);
            functions[i].BeginAddress = begin;
            functions[i].EndAddress = begin + shimBodySize;
            functions[i].UnwindInfoAddress = unwindRva;
        }

        bool ok = ExceptionHandling.AddFunctionTable(functions, 7, imageBase);
        if (ok)
        {
            JitTrace.WriteLine("[JitShimUnwind] Registered 7 shim unwind entries");
        }
        else
        {
            DebugConsole.WriteLine("[JitShimUnwind] AddFunctionTable failed");
        }
    }
}
