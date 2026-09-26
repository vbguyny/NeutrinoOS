// NeutrinoOS kernel - CPU power management (Phase 9 Task 4, milestone 3).
//
// C-states and P-states through the firmware's ACPI `_CST` / `_PSS`
// objects, evaluated by the AML interpreter (src/kernel/Platform/Aml.cs):
//
//   - `_CST` (per processor object, usually \_PR.CPU0._CST or
//     \_SB.PR00._CST): Package of C-state packages
//     { CStateType, Latency, Power, ... }.
//   - `_PSS`: Package of P-state packages
//     { CoreFrequency(MHz), Power(mW), TransitionLatency, BusMasterLatency,
//       Control, Status }.
//
// C-state entry uses MONITOR/MWAIT when the CPU reports both features
// (CPUID.01H:ECX bits 3 and 11) AND the firmware exposes `_CST` - the
// firmware gate keeps the MWAIT path off on platforms (QEMU/TCG) whose
// idle semantics have not been validated for it; those platforms stay on
// the HLT (C1) path, which is what the scheduler idle thread used before.
//
// QEMU q35 firmware exposes neither `_CST` nor `_PSS` (the DSDT declares
// one bare Processor object; verified by disassembling the dumped DSDT
// with iasl), so `cpupower` reports their absence there and the report
// falls back to "C1 via HLT". VirtualBox and real hardware are expected
// to expose at least _CST.
//
// Output lines are stable ("[cpupower] ...") so acceptance scripts can
// assert on them.

using System;
using ProtonOS.Arch;

namespace ProtonOS.Platform;

/// <summary>CPU C-state/P-state detection and reporting (Phase 9).</summary>
public static unsafe class CpuPower
{
    private static bool _initialized;
    private static bool _hasMonitor;
    private static bool _hasMwait;
    private static bool _firmwareProbed;
    private static bool _cstFromFirmware;
    private static bool _pssFromFirmware;
    private static int _cstCount;
    private static int _pssCount;

    /// <summary>MONITOR + MWAIT usable for C-state entry (per CPUID).</summary>
    public static bool MwaitCapable => _hasMonitor && _hasMwait;

    /// <summary>True when the firmware exposes an `_CST` object.</summary>
    public static bool CstFromFirmware => _cstFromFirmware;

    /// <summary>Detect CPU capabilities via CPUID. Idempotent and cheap.</summary>
    public static void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;
#if !ARCH_ARM64
        CPU.Cpuid(1, out _, out _, out uint ecx, out _);
        _hasMonitor = (ecx & (1u << 3)) != 0;
        _hasMwait = (ecx & (1u << 11)) != 0;
#endif
    }

    /// <summary>
    /// Idle the current CPU until the next interrupt. Uses MONITOR/MWAIT
    /// C1 entry when both the CPU and the firmware support it; otherwise
    /// HLT (the classic C1).
    /// </summary>
    public static void IdleOnce()
    {
        Initialize();
        if (!_firmwareProbed)
            ProbeFirmware();
#if !ARCH_ARM64
        if (MwaitCapable && _cstFromFirmware)
        {
            ulong slot = 0;
            CPU.Monitor(&slot);
            CPU.Mwait(0, 1);    // C1 hint
            return;
        }
#endif
        CPU.Halt();
    }

    private static void ProbeFirmware()
    {
        _firmwareProbed = true;
        Aml.IndexFirmwareTables();

        Aml.Pkg cst;
        if (Aml.OpenNamedPackage((byte)'_', (byte)'C', (byte)'S', (byte)'T', out cst) && cst.Count > 0)
        {
            _cstFromFirmware = true;
            _cstCount = cst.Count;
        }

        Aml.Pkg pss;
        if (Aml.OpenNamedPackage((byte)'_', (byte)'P', (byte)'S', (byte)'S', out pss) && pss.Count > 0)
        {
            _pssFromFirmware = true;
            _pssCount = pss.Count;
        }
    }

    /// <summary>
    /// `cpupower` command body: reports the C/P-state picture. Detects the
    /// firmware objects on first use and caches the result.
    /// </summary>
    public static void PrintReport()
    {
        Initialize();
        if (!_firmwareProbed)
            ProbeFirmware();

        DebugConsole.Write("[cpupower] CPU: monitor=");
        DebugConsole.Write(_hasMonitor ? "1" : "0");
        DebugConsole.Write(" mwait=");
        DebugConsole.Write(_hasMwait ? "1" : "0");
        DebugConsole.Write(" idle=");
        DebugConsole.WriteLine(MwaitCapable && _cstFromFirmware ? "MWAIT C1" : "HLT (C1)");

        // ---- C-states --------------------------------------------------
        Aml.Pkg cst;
        if (_cstFromFirmware && Aml.OpenNamedPackage((byte)'_', (byte)'C', (byte)'S', (byte)'T', out cst))
        {
            DebugConsole.Write("[cpupower] C-states: ");
            DebugConsole.WriteDecimal(_cstCount);
            DebugConsole.WriteLine(" entries (_CST)");
            int shown = 0;
            for (int i = 0; i < _cstCount && shown < 8; i++)
            {
                Aml.Pkg sub;
                if (!Aml.PkgSub(in cst, i, out sub))
                    continue;
                ulong type, latency, power;
                if (!Aml.PkgInt(in sub, 0, out type))
                    continue;
                if (!Aml.PkgInt(in sub, 1, out latency))
                    latency = 0;
                if (!Aml.PkgInt(in sub, 2, out power))
                    power = 0;
                DebugConsole.Write("[cpupower]   C type=");
                DebugConsole.WriteDecimal((int)type);
                DebugConsole.Write(" latency=");
                DebugConsole.WriteDecimal((int)latency);
                DebugConsole.Write(" power=");
                DebugConsole.WriteDecimal((int)power);
                DebugConsole.WriteLine();
                shown++;
            }
        }
        else
        {
            DebugConsole.WriteLine("[cpupower] C-states: none exposed by firmware (_CST absent); C1 = HLT");
        }

        // ---- P-states --------------------------------------------------
        Aml.Pkg pss;
        if (_pssFromFirmware && Aml.OpenNamedPackage((byte)'_', (byte)'P', (byte)'S', (byte)'S', out pss))
        {
            DebugConsole.Write("[cpupower] P-states: ");
            DebugConsole.WriteDecimal(_pssCount);
            DebugConsole.WriteLine(" entries (_PSS)");
            int shown = 0;
            for (int i = 0; i < _pssCount && shown < 8; i++)
            {
                Aml.Pkg sub;
                if (!Aml.PkgSub(in pss, i, out sub))
                    continue;
                ulong freq, power;
                if (!Aml.PkgInt(in sub, 0, out freq))
                    continue;
                if (!Aml.PkgInt(in sub, 1, out power))
                    power = 0;
                DebugConsole.Write("[cpupower]   P freq=");
                DebugConsole.WriteDecimal((int)freq);
                DebugConsole.Write(" MHz power=");
                DebugConsole.WriteDecimal((int)power);
                DebugConsole.Write(" mW");
                DebugConsole.WriteLine();
                shown++;
            }
        }
        else
        {
            DebugConsole.WriteLine("[cpupower] P-states: none exposed by firmware (_PSS absent)");
        }
    }

    /// <summary>One-line summary for boot logs/tests.</summary>
    public static string Describe()
    {
        Initialize();
        if (!_firmwareProbed)
            ProbeFirmware();
        string s = "monitor=" + (_hasMonitor ? "1" : "0") + " mwait=" + (_hasMwait ? "1" : "0");
        s += _cstFromFirmware ? (" _CST=" + _cstCount.ToString()) : " _CST=absent";
        s += _pssFromFirmware ? (" _PSS=" + _pssCount.ToString()) : " _PSS=absent";
        return s;
    }
}
