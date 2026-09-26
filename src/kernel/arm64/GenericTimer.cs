// NeutrinoOS kernel - ARM64 generic timer (Phase 8 Task 3, interrupt pass).
//
// Drives the scheduler tick with the EL1 physical timer (CNTP): a 1 ms
// match value on CNTP_CVAL_EL0, delivered as PPI 30 through the GICv2.
// This is the ARM64 replacement for the x64 LAPIC timer + HPET stack
// (BootLog timestamps stay at 0 ms on ARM64 until the timer feeds them).

#if ARCH_ARM64

using ProtonOS.Platform;
using ProtonOS.Threading;

namespace ProtonOS.Arch;

/// <summary>ARM64 generic timer (CNTP) used as the scheduler tick source.</summary>
public static unsafe class GenericTimer
{
    private const ulong TickRateHz = 1000;   // 1 ms tick, matching the x64 APIC cadence

    /// <summary>CNTFRQ_EL0: timer frequency in Hz (62.5 MHz on QEMU virt).</summary>
    public static ulong FrequencyHz { get; private set; }

    /// <summary>Timer interrupts serviced since boot.</summary>
    public static ulong Ticks;

    /// <summary>Whether the timer has been initialized.</summary>
    public static bool Initialized { get; private set; }

    /// <summary>
    /// Program the first expiry, enable CNTP and register the PPI handler
    /// with the GIC. Interrupts are delivered once PSTATE.I is cleared
    /// (Arch.InitStage2).
    /// </summary>
    public static void Init()
    {
        if (Initialized)
            return;

        FrequencyHz = CPU.ReadCntFrq();
        if (FrequencyHz == 0)
            FrequencyHz = 62_500_000;      // QEMU virt default

        ProgramNextExpiry();
        CPU.WriteCntpCtl(1);               // ENABLE=1, IMASK=0, ISTATUS=0

        GicV2.SetPriority(GicV2.TimerIntId, 0xA0);
        GicV2.EnableInterrupt(GicV2.TimerIntId);
        Arch.RegisterHandler((int)(32 + GicV2.TimerIntId), &OnTimerIrq);

        Initialized = true;
        DebugConsole.Write("[arm64] Generic timer: ");
        DebugConsole.WriteDecimal(FrequencyHz);
        DebugConsole.WriteLine(" Hz, 1 ms tick (PPI 30)");
    }

    private static void ProgramNextExpiry()
    {
        CPU.WriteCntpCval(CPU.ReadCntPct() + FrequencyHz / TickRateHz);
    }

    private static void OnTimerIrq(InterruptFrame* frame)
    {
        Ticks++;

        // Arm the next expiry FIRST: the scheduler tick may switch to
        // another thread and this stack won't run again for a while.
        ProgramNextExpiry();

        ProtonOS.Profiling.Profiler.Sample(frame->Rip);
        Scheduler.TimerTick();
    }
}

#endif
