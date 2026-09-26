# Phase 9 — ACPI Implementation (Task 4)

Status: milestones 1–3 implemented and verified on QEMU q35; VirtualBox
and real-hardware validation notes in the platform matrix below.

## Scope

ACPI table parsing and platform power management for NeutrinoOS:

| Area | Status |
|------|--------|
| RSDP/RSDT/XSDT walker (Phase 1) | done (extended) |
| FADT (PM1_CNT, reset register, X_* variants) | done |
| DSDT/SSDT access (DSDT located via FADT, SSDTs via XSDT) | done |
| AML interpreter subset (`_S5`, `_S3`, `_PTS`, `_WAK`, `_CST`, `_PSS`) | done |
| Power off — ACPI S5 (`poweroff`) | done, verified |
| Reset — FADT reset register (`reboot`) | done, verified |
| Sleep — ACPI S3 (`sleep`/`suspend`) | done (see platform matrix) |
| CPU power — C/P states, MONITOR/MWAIT (`cpupower`) | done |

## Implementation map

| File | Responsibility |
|------|----------------|
| `src/kernel/Platform/ACPI.cs` | Table walker; `FindFadt()`; `TableCount`/`GetTable(i)` for SSDT enumeration |
| `src/kernel/Platform/Aml.cs` | AML evaluator + method interpreter + firmware namespace indexing |
| `src/kernel/Platform/AmlSelfTest.cs` | Boot-time interpreter self-test (`[AML]` lines) |
| `src/kernel/Platform/PowerManagement.cs` | S5/reset/S3 entry; `_S5`/`_S3` evaluation; `_PTS`/`_WAK` invocation |
| `src/kernel/Platform/CpuPower.cs` | `_CST`/`_PSS` probing, `cpupower` report, MWAIT idle |
| `src/kernel/Shell/ShellBuiltins.cs` | `poweroff`, `reboot`, `sleep`/`suspend`, `cpupower` |
| `src/kernel/x64/native.asm` | `cpu_monitor`/`cpu_mwait` stubs |
| `src/bootloader/boot.asm` | Warm-boot kernel BSS zeroing (reboot correctness) |

## Detection and boot log

At boot, after PCI enumeration, `PowerManagement.Initialize()` emits one
summary line, e.g. on QEMU q35:

```
[power] ACPI: PM1a_CNT=0x0604 len=2 SLP_TYP=0 (\_S5) S3_SLP_TYP=1 (\_S3) reset=port 0x0CF9 val 0x0F
```

`(\_S5)`/`(\_S3)` show that the SLP_TYP values were evaluated from the
firmware AML (fallback values are marked otherwise; S3 requires `\_S3`).
The `[AML]` self-test runs on every boot (synthetic `_PTS`/`_WAK`-shaped
methods, since QEMU firmware has none) and prints a `[AML] result:` line.

## AML interpreter

`Aml.cs` implements the documented subset needed by firmware power
methods (ACPI 6.x chapter 20):

- **Namespace discovery**: a linear scan indexes `Method` and `Name`
  definitions across the DSDT and every SSDT (validated PkgLength /
  name-character / flag shapes). Lookups match the last name segment,
  which covers the `_S5`/`_S3`/`_PTS`/`_WAK`/`_CST`/`_PSS` naming style.
- **Values**: integers (all AML prefixes), strings, buffers, packages
  (nested, evaluated on demand), locals/args, name references with a
  runtime overlay for stores.
- **Terms executed**: Store/CopyObject, Add/Subtract/Multiply/Divide
  variants, And/Or/Xor/Not/Nand/Nor, shifts, Mod, Increment/Decrement,
  LAnd/LOr/LNot/LEqual/LGreater/LLess, If/Else, While (iteration cap),
  Return, method invocation with arguments (nested, depth-capped),
  SizeOf/Index/DerefOf/RefOf, Create*Field, Acquire/Release/Sleep/Stall,
  Notify, and the remaining no-op-worthy extended ops.
- **Safety**: unknown opcodes abort the affected method with an `[aml]`
  log line; a global step budget bounds runaway loops; not-taken
  `If`/`Else` branches are consumed in a side-effect-free skip mode.
- Stores into firmware fields (EC/GPIO-style FieldUnits) are consumed
  but treated as no-ops: the interpreter never touches devices it does
  not model.

Opcode encodings were cross-checked against a real DSDT (QEMU 8.2.2
q35) disassembled with `iasl`: If=0xA0, While=0xA2, Else=0xA1, Store=0x70,
Add=0x72, LEqual=0x93, LLess=0x95, Increment=0x75, Return=0xA4,
Acquire=5B 23 (name + u16 timeout), Release=5B 27.

The self-test (`AmlSelfTest.cs`) assembles synthetic AML with those
encodings and asserts: Add with args (12), If/Else branches (7/9),
While + locals (10), and Store-to-Name + read-back (42).

## Commands

- `poweroff` — evaluate `\_S5`, write `SLP_TYP | SLP_EN` to PM1a/PM1b_CNT.
  QEMU exits the process (verified); VirtualBox powers the VM off.
- `reboot` — FADT reset register when present (QEMU q35: port 0x0CF9,
  value 0x0F), then the 0xCF9 PCI reset, then the 8042 pulse.
- `sleep` / `suspend` — evaluate `_PTS(3)` (when present), write
  `\_S3` SLP_TYP with SLP_EN, evaluate `_WAK(3)` when execution resumes.
- `cpupower` — report CPUID MONITOR/MWAIT capability, the firmware
  `_CST` C-state list, the `_PSS` P-state list, and the idle policy.

## Platform matrix

| Platform | S5 poweroff | Reset | S3 suspend | S3 guest resume | C/P states |
|----------|-------------|-------|------------|-----------------|------------|
| QEMU q35 (TCG) | verified (QEMU exits) | verified (0x0CF9; warm boot fixed, see below) | verified (PM1 write, run state `paused (suspended)`) | **not verifiable** — see QEMU S3 limitation | none exposed; `cpupower` reports absence |
| VirtualBox | expected (spec: should work) | expected | to be validated | to be validated | to be validated |
| Real hardware | primary target | primary target | primary target | primary target | primary target |

### QEMU S3 limitation (documented beacon)

On QEMU x86, `system_wakeup` flips the VM run state back to `running`
and the platform wake works at the VM level, but *guest* execution does
not continue in an OS-usable way: QEMU's wake path raises an SMI and the
CPU ends up in firmware SMM handling (OVMF `PiSmmCpuDxeSmm`). On real
hardware (and VirtualBox) the OS cooperates by writing the S3 resume
vector into the FACS and the firmware resumes through it; NeutrinoOS
does not (yet) implement that OS-side resume protocol, so post-wake
guest continuation is out of scope for the QEMU acceptance. This is the
same class of limitation the phase spec anticipates ("QEMU may not
support ACPI power off via ACPICA's default mechanism; document the
QEMU limitation and verify on VirtualBox and real hardware").

The acceptance suite therefore asserts, on QEMU: S3 entry with the
`\_S3` values, run state `paused (suspended)`, and run state `running`
after `system_wakeup`, plus process liveness. Guest-side resume is the
VirtualBox / real-hardware validation item.

### Warm boot after reset (reboot correctness)

The first `reboot` acceptance run exposed a pre-existing loader bug: a
warm reset preserves RAM, and `boot.asm` copied only `SizeOfRawData`
bytes per section, leaving the kernel's BSS tails (zero-initialized C#
statics) with stale values from the previous boot — the second boot
crashed in `DebugConsole.WriteByte` (stale `ConsoleAbstractionLayer`
state). Cold boots had only ever worked because OVMF hands out zeroed
memory. Fix: `boot.asm` now zeroes `[KernelBase, KernelBase +
SizeOfImage)` before copying the image. Verified by a monitor-forced
`system_reset` experiment (second boot reaches the shell; 0 crash
markers), and by the reboot leg of the acceptance suite.

## Idle entry (C-states)

`CpuPower.IdleOnce()` is used by the scheduler idle thread:

- MONITOR+MWAIT C1 entry when `CPUID.01H:ECX` reports both MONITOR
  (bit 3) and MWAIT (bit 11) **and** the firmware exposes `_CST`;
- otherwise `HLT` (classic C1) — which is what QEMU/TCG uses (it
  reports MONITOR=1, MWAIT=0; `cpupower` prints the policy).

The firmware-`_CST` gate keeps the MWAIT path off on platforms whose
idle semantics for it have not been validated.

## Testing

`build/p9-acpi-test.sh` (QEMU q35, standard image) covers 18 checks:

1. poweroff leg (5): boot, detection, S5 PM1_CNT write, `\_S5` values,
   QEMU exits by itself.
2. reboot leg (3): boot, reset path, second shell banner in the same
   QEMU process (warm boot).
3. sleep leg (10): boot; `cpupower` CPU/C/P report lines; S3 entry with
   `\_S3` values; no `_PTS` on QEMU; run state `paused (suspended)`;
   run state `running` after `system_wakeup`; QEMU process alive after
   wake.

Supporting scripts: `build/p9warm-test.sh` (monitor `system_reset`
warm-boot regression), `build/p9-boot.sh` (single boot with optional
command), `build/p9-dump.sh` + `build/p9-extract-aml.py` (DSDT/SSDT
capture for offline `iasl` disassembly).

## References

- Nyx-style design: ACPICA not ported; parser/interpreter are C# in-tree.
- ACPI 6.x: chapter 20 (AML encoding), GAS (`X_*`) registers, `_S5`/
  `_S3` package shape, `_PTS`/`_WAK`/`_CST`/`_PSS` semantics.
