// NeutrinoOS kernel - Phase 4 big-stack runner
//
// Runs a kernel-mode code body (JIT compilation + assembly execution) on a
// private 8 MB stack instead of the shell thread's stack.
//
// Why: ConsoleSession.Run() executes the shell, and AssemblyRunner.Run()
// executes assemblies, on the boot thread - which still runs on the
// firmware (UEFI) stack of only a few tens of KB. A standard .NET 10 app's
// nested JIT compiles (compile-on-first-call recursion through the whole
// reachable call graph) plus its BCL call chains easily exceed that; the
// overflow manifests as an RhpStackProbe page fault (#PF at
// RhpStackProbe+0x10) during the JIT compile of Main.
//
// The switch itself is native (native.asm run_on_big_stack): it swaps RSP
// for the duration of the call and then restores the caller's stack. The
// argument block stays on the caller's stack, which remains mapped.

using System.Runtime.InteropServices;
using ProtonOS.IO;
using ProtonOS.Memory;

namespace ProtonOS.Platform;

/// <summary>
/// Runs a code body on a private large stack (see file header).
/// </summary>
public static unsafe class BigStackRunner
{
    /// <summary>Default size of the private execution stack.</summary>
    public const ulong StackSize = 8 * 1024 * 1024;

    // native.asm: int run_on_big_stack(void* stackTop, int (*fn)(void*), void* arg)
    // (bflat binds [DllImport("*")] externs directly to native.asm symbols;
    // a bare 'extern' has no codegen path - same convention as
    // set_syscall_kernel_stack in CPU.cs.)
    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern int run_on_big_stack(ulong stackTop, ulong fn, ulong arg);

    /// <summary>
    /// Run <paramref name="body"/> on a private stack and return its result.
    /// Allocates from the page allocator (the kernel heap is a few MB and
    /// cannot host an 8 MB block); falls back to the caller's stack if the
    /// allocation fails. The stack is intentionally never freed - same
    /// contract as the assembly image buffers (see AssemblyRunner header).
    /// </summary>
    public static int Run(delegate*<void*, int> body, void* arg)
    {
        ulong pages = (StackSize + 4095) / 4096;
        ulong stackBase = PageAllocator.AllocatePages(pages);
        if (stackBase == 0)
        {
            DebugConsole.WriteLine("[BigStack] page allocation failed; running on caller stack");
            return body(arg);
        }

        ulong top = (stackBase + StackSize) & ~0xFul;
        DebugConsole.Write("[BigStack] assembly compile+run on private stack top=0x");
        DebugConsole.WriteHex(top);
        DebugConsole.WriteLine();
        return run_on_big_stack(top, (ulong)body, (ulong)arg);
    }
}
