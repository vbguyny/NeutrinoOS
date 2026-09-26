// NeutrinoOS kernel - ARM64 GICv2 driver (Phase 8 Task 3, interrupt pass).
//
// QEMU 'virt' exposes a GICv2: distributor at 0x08000000, CPU interface
// at 0x08010000. Sources owned by the kernel:
//   - PL011 UART0: SPI 33 (console RX)
//   - Generic timer: PPI 30 (EL1 physical timer)
//
// Init() starts from a clean slate: the firmware leaves its own sources
// enabled (its timer PPI fires continuously), so everything is disabled
// before the kernel enables only the interrupts it owns.

#if ARCH_ARM64

namespace ProtonOS.Arch;

/// <summary>GICv2 interrupt controller driver (QEMU virt layout).</summary>
public static unsafe class GicV2
{
    /// <summary>Distributor base (QEMU virt).</summary>
    public const ulong DistBase = 0x08000000;

    /// <summary>CPU interface base (QEMU virt).</summary>
    public const ulong CpuBase = 0x08010000;

    /// <summary>PL011 UART0 interrupt (SPI 33).</summary>
    public const uint Uart0IntId = 33;

    /// <summary>EL1 physical generic timer interrupt (PPI 30).</summary>
    public const uint TimerIntId = 30;

    // Distributor registers
    private const ulong GICD_CTLR = 0x000;
    private const ulong GICD_IGROUPR = 0x080;
    private const ulong GICD_ISENABLER = 0x100;
    private const ulong GICD_ICENABLER = 0x180;
    private const ulong GICD_IPRIORITYR = 0x400;
    private const ulong GICD_ITARGETSR = 0x800;
    private const ulong GICD_PIDR2 = 0xFE8;

    // CPU interface registers
    private const ulong GICC_CTLR = 0x000;
    private const ulong GICC_PMR = 0x004;
    private const ulong GICC_IAR = 0x00C;     // THE acknowledge register
    private const ulong GICC_EOIR = 0x010;    // THE end-of-interrupt register

    /// <summary>GIC architecture version from GICD_PIDR2 (2 = GICv2).</summary>
    public static uint DetectedVersion { get; private set; }

    /// <summary>Whether Init() has completed.</summary>
    public static bool Initialized { get; private set; }

    private static uint Read32(ulong addr) => *(uint*)addr;

    private static void Write32(ulong addr, uint value) => *(uint*)addr = value;

    /// <summary>
    /// Initialize the GIC: disable all firmware sources, configure
    /// PPI/SPI priority + group + target, enable the distributor and the
    /// CPU interface. Interrupt delivery stays gated by PSTATE.I (IRQs
    /// are unmasked separately after the kernel sources are registered).
    /// </summary>
    public static void Init()
    {
        if (Initialized)
            return;

        DetectedVersion = (Read32(DistBase + GICD_PIDR2) >> 4) & 0xF;

        // Disable forwarding while reprogramming.
        Write32(DistBase + GICD_CTLR, 0);

        // Clean slate: disable every PPI (16-31, banked) and SPI (32+).
        Write32(DistBase + GICD_ICENABLER, 0xFFFF0000);
        for (uint i = 1; i < 32; i++)
            Write32(DistBase + GICD_ICENABLER + i * 4, 0xFFFFFFFF);

        // Group 0, priority 0xA0 (passes PMR=0xFF), SPIs targeted at CPU 0.
        //
        // All interrupts MUST be group 0: the single-view CPU interface
        // acknowledges via GICC_IAR, which (in QEMU: "secure" access)
        // only surfaces group-0 pending interrupts. Group-1 items are
        // deliberately hidden (IAR reads 1022) unless GICC_CTLR.AckCtl
        // is set — and AckCtl cannot be set without the security
        // extensions. A hidden-but-pending interrupt still asserts the
        // IRQ line, which livelocks the vector (the Phase 8 ack-storm).
        Write32(DistBase + GICD_IGROUPR, 0x00000000);
        Write32(DistBase + GICD_IPRIORITYR, 0xA0A0A0A0);
        Write32(DistBase + GICD_ITARGETSR, 0x01010101);
        for (uint i = 1; i < 32; i++)
        {
            Write32(DistBase + GICD_IGROUPR + i * 4, 0x00000000);
            Write32(DistBase + GICD_IPRIORITYR + i * 4, 0xA0A0A0A0);
            Write32(DistBase + GICD_ITARGETSR + i * 4, 0x01010101);
        }

        // Enable forwarding (group 0).
        Write32(DistBase + GICD_CTLR, 1);

        // CPU interface: allow all priorities, enable group 0. Group-0
        // interrupts signal as IRQ (FIQEn = 0), which is what the
        // VBAR_EL1 IRQ vector handles.
        Write32(CpuBase + GICC_PMR, 0xFF);
        Write32(CpuBase + GICC_CTLR, 1);

        Initialized = true;
    }

    /// <summary>Enable one interrupt (PPI or SPI).</summary>
    public static void EnableInterrupt(uint intId)
    {
        ulong reg = GICD_ISENABLER + (intId / 32) * 4;
        Write32(DistBase + reg, Read32(DistBase + reg) | (1u << (int)(intId % 32)));
    }

    /// <summary>Disable one interrupt (PPI or SPI).</summary>
    public static void DisableInterrupt(uint intId)
    {
        Write32(DistBase + GICD_ICENABLER + (intId / 32) * 4, 1u << (int)(intId % 32));
    }

    /// <summary>Set the priority of one interrupt (lower = more urgent).</summary>
    public static void SetPriority(uint intId, byte priority)
    {
        ((byte*)(DistBase + GICD_IPRIORITYR))[intId] = priority;
    }

    /// <summary>
    /// Acknowledge the highest-priority pending interrupt via GICC_IAR.
    /// A single-security-view GICv2 (the QEMU 'virt' model the kernel
    /// runs against) has exactly ONE ack register: there are no aliased
    /// AIAR/AEOIR registers, and offset 0x020 reads as constant 0 — never
    /// treat it as an acknowledge (it caused an ack-storm livelock).
    /// 1020-1023 = nothing pending / spurious.
    /// </summary>
    public static uint AcknowledgeGroup0() => Read32(CpuBase + GICC_IAR) & 0x3FF;

    /// <summary>Signal end-of-interrupt for an acknowledged ID (GICC_EOIR).</summary>
    public static void EndOfInterruptGroup0(uint intId) => Write32(CpuBase + GICC_EOIR, intId);

    // ==================== Diagnostics ====================

    /// <summary>GICD_CTLR read-back (diagnostics).</summary>
    public static uint ReadDistCtlr() => Read32(DistBase + GICD_CTLR);

    /// <summary>GICC_CTLR read-back (diagnostics).</summary>
    public static uint ReadCpuCtlr() => Read32(CpuBase + GICC_CTLR);

    /// <summary>GICC_PMR read-back (diagnostics).</summary>
    public static uint ReadPmr() => Read32(CpuBase + GICC_PMR);

    /// <summary>GICD_ISENABLER words 0 (SGI/PPI) and 1 (SPI 32-63) (diagnostics).</summary>
    public static uint ReadEnabled0() => Read32(DistBase + GICD_ISENABLER);

    /// <summary>GICD_ISENABLER word 1: SPIs 32-63 (diagnostics).</summary>
    public static uint ReadEnabled1() => Read32(DistBase + GICD_ISENABLER + 4);
}

#endif
