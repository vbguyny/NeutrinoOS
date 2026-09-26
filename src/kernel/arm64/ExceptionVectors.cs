// NeutrinoOS kernel - ARM64 exception vector dispatch (Phase 8 Task 3).
//
// The VBAR_EL1 vector stubs in native.s build an Arm64ExceptionFrame and
// call Dispatch() for every exception. IRQs are routed into the shared
// vector-handler machinery (Arch.RegisterHandler) using the x64-style
// vector numbering (32 + interrupt ID); sync/SError faults print a full
// diagnostic dump and halt (exception unwinding is x64-only for now).

#if ARCH_ARM64

using System.Runtime.InteropServices;
using ProtonOS.Platform;

namespace ProtonOS.Arch;

/// <summary>
/// Exception frame built by the VBAR_EL1 stubs. The offsets of
/// InterruptNumber (0x88), Esr (0x90), Elr (0x98), Spsr (0xA0) and
/// EntrySp (0xB0) match the x64 InterruptFrame layout so the shared
/// dispatch/handler code works unchanged.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Arm64ExceptionFrame
{
    public ulong X0, X1, X2, X3, X4, X5, X6, X7;              // 0x00..0x38
    public ulong X8, X9, X10, X11, X12, X13, X14, X15;        // 0x40..0x78
    public ulong X16;               // 0x80
    public ulong InterruptNumber;   // 0x88 (x64-compatible)
    public ulong Esr;               // 0x90 (ESR_EL1; x64 "ErrorCode")
    public ulong Elr;               // 0x98 (faulting/interrupted PC; x64 "Rip")
    public ulong Spsr;              // 0xA0 (x64 "Cs")
    public ulong ReservedA8;        // 0xA8
    public ulong EntrySp;           // 0xB0 (SP at exception entry; x64 "Rsp")
    public ulong ReservedB8;        // 0xB8
    public ulong X17, X18, X19, X20, X21, X22, X23, X24;      // 0xC0..0xF8
    public ulong X25, X26, X27, X28, X29, X30;                // 0x100..0x128
    public ulong Far;               // 0x130 (FAR_EL1)
    public ulong Kind;              // 0x138 (0 sync, 1 irq, 2 serror, 3 fiq)
}

/// <summary>Managed side of the VBAR_EL1 vector stubs.</summary>
public static unsafe class ExceptionVectors
{
    private const ulong KindIrq = 1;
    private const ulong KindFiq = 3;

    /// <summary>Entry point from native.s (arm64_vectors_common).</summary>
    [UnmanagedCallersOnly(EntryPoint = "arm64_exception_dispatch")]
    public static void Dispatch(Arm64ExceptionFrame* frame)
    {
        if (frame->Kind == KindIrq || frame->Kind == KindFiq)
        {
            HandleInterrupt(frame);
            return;
        }

        HandleFault(frame);
    }

    private static void HandleInterrupt(Arm64ExceptionFrame* frame)
    {
        // IRQs are masked until GicV2.Init (InitStage1 masks them); this
        // is a safety net only.
        if (!GicV2.Initialized)
            return;

        // A single-security-view GICv2 CPU interface has exactly ONE ack
        // pair: GICC_IAR (0x00C) / GICC_EOIR (0x010). GicV2.Init places
        // every source in group 0 precisely because that is the group
        // this (secure-view) IAR surfaces — see the comment in Init().
        uint intId = GicV2.AcknowledgeGroup0();
        if (intId >= 1020)
            return;                         // spurious

        // EOI first: handlers may switch threads.
        GicV2.EndOfInterruptGroup0(intId);

        // Reuse the shared handler machinery with x64-style numbering.
        // Only IDs that fit the 256-vector table are dispatched; anything
        // else is acknowledged, EOI'd and dropped.
        if (intId > 192)
            return;

        frame->InterruptNumber = 32 + intId;
        Arch.DispatchInterruptManaged((InterruptFrame*)frame);
    }

    private static void HandleFault(Arm64ExceptionFrame* frame)
    {
        ulong ec = (frame->Esr >> 26) & 0x3F;

        DebugConsole.WriteLine();
        DebugConsole.WriteLine("[arm64] === SYNC EXCEPTION ===");
        DebugConsole.Write("  ESR=0x");
        DebugConsole.WriteHex(frame->Esr);
        DebugConsole.Write(" EC=0x");
        DebugConsole.WriteHex(ec);
        DebugConsole.Write(" ELR=0x");
        DebugConsole.WriteHex(frame->Elr);
        DebugConsole.Write(" FAR=0x");
        DebugConsole.WriteHex(frame->Far);
        DebugConsole.WriteLine();
        DebugConsole.Write("  SPSR=0x");
        DebugConsole.WriteHex(frame->Spsr);
        DebugConsole.Write(" SP=0x");
        DebugConsole.WriteHex(frame->EntrySp);
        DebugConsole.Write(" x29=0x");
        DebugConsole.WriteHex(frame->X29);
        DebugConsole.Write(" x30=0x");
        DebugConsole.WriteHex(frame->X30);
        DebugConsole.WriteLine();
        DebugConsole.Write("  x0=0x");
        DebugConsole.WriteHex(frame->X0);
        DebugConsole.Write(" x1=0x");
        DebugConsole.WriteHex(frame->X1);
        DebugConsole.Write(" x2=0x");
        DebugConsole.WriteHex(frame->X2);
        DebugConsole.Write(" x3=0x");
        DebugConsole.WriteHex(frame->X3);
        DebugConsole.WriteLine();
        DebugConsole.WriteLine("[arm64] halting");

        for (;;)
            CPU.Halt();
    }
}

#endif
