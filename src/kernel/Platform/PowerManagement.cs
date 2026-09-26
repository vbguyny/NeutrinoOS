// NeutrinoOS kernel - ACPI power management (Phase 9 Task 4, milestone 1).
//
// Implements the S5 "soft off" and the system reset path:
//
//   - FADT discovery through the existing ACPI XSDT/RSDT walker.
//   - PM1a/PM1b control block ports (64-bit X_* variants preferred, the
//     32-bit fields used as the ACPI 1.0 fallback) with the PM1_CNT
//     register width from PM1_CNT_LEN.
//   - The SLP_TYP values for the S5 state come from evaluating the
//     DSDT/SSDT `\_S5` Name object with the minimal AML evaluator
//     (src/kernel/Platform/Aml.cs). When no table provides `_S5`, the
//     common 5/5 pair is used as a best-effort fallback (logged).
//   - Reset: the FADT RESET_REG/RESET_VALUE when present, then the
//     0xCF9 PCI-reset port, then the 0x64 keyboard-controller reset.
//
// Milestones still to come: S3 suspend/resume (\_S3, \_PTS, \_WAK), the
// full AML method interpreter, CPU C-states/P-states (\_CST/\_PSS),
// `acpi`/`cpupower` reporting. QEMU q35 supports both S5 power-off and
// the 0xCF9 reset; VirtualBox supports S5; document per-platform notes
// in docs/PHASE9-ACPI.md.
//
// bflat AOT constraints: plain static state, no reflection, no
// exceptions on the probe path; all table access through raw pointers
// (identity-mapped physical addresses, same as the Phase 1 ACPI parser).

using System;

namespace ProtonOS.Platform;

/// <summary>ACPI power-off (S5) and reset (Phase 9).</summary>
public static unsafe class PowerManagement
{
    private const ushort Pm1CntSleepEnable = 0x2000;   // SLP_EN (bit 13)
    private const int Pm1CntSleepTypeShift = 10;       // SLP_TYP in bits 12:10
    private const ushort PciResetPort = 0x0CF9;        // QEMU/Intel PCI reset
    private const byte PciResetValue = 0x06;           // RESET_CPU | SYS_RST
    private const ushort KbcCommandPort = 0x0064;      // 8042 pulse reset line
    private const byte KbcResetCommand = 0xFE;

    private static bool _initialized;
    private static bool _available;
    private static bool _haveS5;
    private static bool _havePm1b;
    private static bool _haveResetReg;
    private static bool _slpTypFromAml;
    private static ushort _pm1aCntPort;
    private static ushort _pm1bCntPort;
    private static byte _pm1CntLen = 2;
    private static ulong _slpTypA;
    private static ulong _slpTypB;
    private static ushort _resetRegPort;
    private static byte _resetValue;

    /// <summary>True once Initialize() ran and a usable mechanism exists.</summary>
    public static bool IsAvailable => _initialized && _available;

    /// <summary>True when the SLP_TYP values came from evaluating \_S5.</summary>
    public static bool SlpTypFromAml => _slpTypFromAml;

    /// <summary>One-line detection summary (evidence for tests/logs).</summary>
    public static string Describe()
    {
        if (!_initialized)
            Initialize();
        if (!_available)
            return "ACPI power management unavailable (no FADT)";
        string s = "PM1a_CNT=0x" + Hex4(_pm1aCntPort) + " len=" + _pm1CntLen.ToString();
        s += " SLP_TYP=" + _slpTypA.ToString() + (_slpTypFromAml ? " (\\_S5)" : " (fallback)");
        if (_havePm1b)
            s += " PM1b_CNT=0x" + Hex4(_pm1bCntPort) + " b=" + _slpTypB.ToString();
        s += _haveResetReg ? (" reset=port 0x" + Hex4(_resetRegPort) + " val 0x" + Hex2(_resetValue)) : " reset=0xCF9";
        return s;
    }

    /// <summary>
    /// Probe the FADT and the AML tables. Idempotent and safe to call on
    /// machines without ACPI (returns false, power commands report an
    /// error instead of acting).
    /// </summary>
    public static bool Initialize()
    {
        if (_initialized)
            return _available;
        _initialized = true;

        if (!ACPI.IsInitialized)
            ACPI.Init();
        if (!ACPI.IsInitialized)
        {
            DebugConsole.WriteLine("[power] ACPI not initialized; power management unavailable");
            return false;
        }

        ACPIFADT* fadt = ACPI.FindFadt();
        if (fadt == null)
        {
            DebugConsole.WriteLine("[power] FADT (FACP) not found");
            return false;
        }

        // PM1a_CNT: prefer the 64-bit generic address (I/O space) when set.
        if (fadt->XPm1aCntBlk.AddressSpaceId == 1 && fadt->XPm1aCntBlk.Address != 0)
            _pm1aCntPort = (ushort)fadt->XPm1aCntBlk.Address;
        else
            _pm1aCntPort = (ushort)fadt->Pm1aCntBlk;

        if (fadt->XPm1bCntBlk.AddressSpaceId == 1 && fadt->XPm1bCntBlk.Address != 0)
        {
            _havePm1b = true;
            _pm1bCntPort = (ushort)fadt->XPm1bCntBlk.Address;
        }
        else if (fadt->Pm1bCntBlk != 0)
        {
            _havePm1b = true;
            _pm1bCntPort = (ushort)fadt->Pm1bCntBlk;
        }

        _pm1CntLen = fadt->Pm1CntLen;
        if (_pm1CntLen != 1 && _pm1CntLen != 2 && _pm1CntLen != 4)
            _pm1CntLen = 2;

        // Reset register (GenericAddress, I/O space only here).
        if (fadt->ResetReg.AddressSpaceId == 1 && fadt->ResetReg.Address != 0)
        {
            _haveResetReg = true;
            _resetRegPort = (ushort)fadt->ResetReg.Address;
            _resetValue = fadt->ResetValue;
        }

        // \_S5 from the DSDT, then from an SSDT when the DSDT lacks it.
        ulong dsdtAddr = fadt->XDsdt != 0 ? fadt->XDsdt : fadt->Dsdt;
        if (dsdtAddr != 0)
        {
            var dsdt = (ACPITableHeader*)dsdtAddr;
            _slpTypFromAml = Aml.TryReadNameIntegers(
                (byte*)dsdt, dsdt->Length,
                (byte)'_', (byte)'S', (byte)'5', (byte)'_',
                out _slpTypA, out _slpTypB);
        }
        if (!_slpTypFromAml)
        {
            var ssdt = ACPI.FindTable((byte)'S', (byte)'S', (byte)'D', (byte)'T');
            if (ssdt != null)
            {
                _slpTypFromAml = Aml.TryReadNameIntegers(
                    (byte*)ssdt, ssdt->Length,
                    (byte)'_', (byte)'S', (byte)'5', (byte)'_',
                    out _slpTypA, out _slpTypB);
            }
        }
        if (!_slpTypFromAml)
        {
            // Best-effort fallback for firmwares that hide _S5 in AML we
            // cannot evaluate yet: SLP_TYP 5/5 is the common value.
            _slpTypA = 5;
            _slpTypB = 5;
        }

        _haveS5 = _pm1aCntPort != 0;
        _available = _haveS5 || _haveResetReg;

        DebugConsole.Write("[power] ACPI: ");
        DebugConsole.WriteLine(Describe());
        return _available;
    }

    /// <summary>
    /// Soft power off (S5): write SLP_TYP | SLP_EN to PM1a_CNT (and
    /// PM1b_CNT when present). Does not return on success; halts with an
    /// error message when no S5 mechanism exists.
    /// </summary>
    public static void PowerOff()
    {
        if (!Initialize() || !_haveS5)
        {
            DebugConsole.WriteLine("[power] poweroff: no ACPI S5 mechanism available");
            return;
        }

        DebugConsole.Write("[power] ACPI S5 power off: ");
        DebugConsole.WriteLine(Describe());

        ulong valueA = (_slpTypA << Pm1CntSleepTypeShift) | Pm1CntSleepEnable;
        WritePm1Cnt(_pm1aCntPort, valueA);
        if (_havePm1b)
        {
            ulong valueB = (_slpTypB << Pm1CntSleepTypeShift) | Pm1CntSleepEnable;
            WritePm1Cnt(_pm1bCntPort, valueB);
        }

        // If the platform ignored the write, spin with interrupts off
        // rather than running on in an undefined state.
        ProtonOS.Arch.CPU.HaltForever();
    }

    /// <summary>
    /// System reset: FADT reset register when present, else the 0xCF9 PCI
    /// reset, else the keyboard-controller pulse. Does not return.
    /// </summary>
    public static void Reboot()
    {
        if (!Initialize())
        {
            DebugConsole.WriteLine("[power] reboot: no ACPI tables; using 0xCF9");
        }

        if (_haveResetReg)
        {
            DebugConsole.Write("[power] ACPI reset via port 0x");
            DebugConsole.WriteHex(_resetRegPort);
            DebugConsole.Write(" value 0x");
            DebugConsole.WriteHex(_resetValue);
            DebugConsole.WriteLine();
            ProtonOS.Arch.CPU.OutByte(_resetRegPort, _resetValue);
        }
        else
        {
            DebugConsole.WriteLine("[power] reset via PCI port 0xCF9 (0x06)");
            ProtonOS.Arch.CPU.OutByte(PciResetPort, PciResetValue);
        }

        // Fallback chain if the first pulse was ignored.
        DebugConsole.WriteLine("[power] reset fallback: keyboard controller 0x64 <- 0xFE");
        ProtonOS.Arch.CPU.OutByte(KbcCommandPort, KbcResetCommand);
        ProtonOS.Arch.CPU.HaltForever();
    }

    private static void WritePm1Cnt(ushort port, ulong value)
    {
        if (_pm1CntLen == 1)
            ProtonOS.Arch.CPU.OutByte(port, (byte)value);
        else if (_pm1CntLen == 4)
            ProtonOS.Arch.CPU.OutDword(port, (uint)value);
        else
            ProtonOS.Arch.CPU.OutWord(port, (ushort)value);
    }

    private static string Hex4(ushort v)
    {
        return new string(new char[] { HexDigit(v >> 12), HexDigit(v >> 8), HexDigit(v >> 4), HexDigit(v) });
    }

    private static string Hex2(byte v)
    {
        return new string(new char[] { HexDigit(v >> 4), HexDigit(v) });
    }

    private static char HexDigit(int v)
    {
        v &= 0xF;
        return v < 10 ? (char)('0' + v) : (char)('A' + (v - 10));
    }
}
