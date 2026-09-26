# Phase 8 — ARM64 Port (Task 3)

NeutrinoOS boots on **AArch64 UEFI** (QEMU `virt` + AAVMF) all the way to
the interactive shell, with interrupts live: VBAR_EL1 exception vectors,
a GICv2 driver, the ARM generic timer driving the scheduler, and shell
input arriving through the PL011 UART receive interrupt.

The kernel is the same managed codebase as x64; the architecture layer is
selected at compile time (`ARCH_X64` / `ARCH_ARM64`, namespaces
`ProtonOS.X64.*` / `ProtonOS.Arch.*`).

---

## 1. Boot flow

| Stage | What happens |
|---|---|
| UEFI | AAVMF loads `\EFI\BOOT\BOOTAA64.EFI` (single-stage, no bootloader — the x64 LOADER.EFI chain has no ARM64 equivalent yet) |
| `EfiEntry` (native.s) | Minimal hand-off stub; calls korlib `EfiMain` → `Kernel.Main()` |
| BootInfo | `Arm64BootSetup` builds BootInfo from **live** UEFI services: raw `GetMemoryMap` descriptors (loader/boot-service regions retyped reserved because boot services stay live), the `EFI_LOADED_IMAGE` kernel range, and the RSDP from the system table configuration tables. Stored via the `set_boot_info` native symbol (the x64 contract: `get_boot_info` returns the stored pointer) |
| Stage 1 | Disable interrupts (DAIF), install VBAR_EL1 vectors **first** (firmware IRQs would otherwise hit unconfigured vectors), GDT (UEFI leaves a valid one), keep the firmware identity map as the kernel map |
| Heap/GC/statics | Same managed subsystems as x64 (heap, GC, static constructors, code heap) |
| Stage 2 | GICv2 init → generic timer init (1 ms CNTP) → `EnableInterrupts` |
| Shell | Full shell (Phase 5) on the PL011 serial console, incl. line discipline input |

QEMU command used for every test:

```bash
qemu-system-aarch64 -machine virt -cpu cortex-a72 -m 2G -smp 1 \
  -drive if=pflash,format=raw,readonly=on,file=/usr/share/AAVMF/AAVMF_CODE.fd \
  -drive if=pflash,format=raw,file=build/arm64/AAVMF_VARS.fd \
  -drive id=hd0,if=none,format=raw,file=build/arm64/neutrinoos.img \
  -device virtio-blk-pci,drive=hd0,bootindex=1 \
  -nic none -display none -serial file:SERIAL_LOG -no-reboot -no-shutdown
```

Notes: copy a fresh `AAVMF_VARS.fd` per boot; `-serial stdio` through pipes
loses output — use `-serial file:` for headless runs and `-serial pipe:`
(pre-created FIFOs + a persistent reader) for interactive input.

## 2. Native layer (`src/kernel/arm64/native.s`)

A hand-written AArch64 assembly layer (clang `--target=aarch64-pc-windows-msvc`
→ COFF, linked with `lld-link -subsystem:efi_application -machine:arm64`)
provides the ~90 symbols the bflat AOT compiler and kernel expect. The ABI
contracts below were **discovered empirically** and are load-bearing:

| Symbol(s) | Contract |
|---|---|
| `RhpAssignRefArm64`, `RhpCheckedAssignRefArm64` | GC write barriers: **x14 = destination, x15 = value** (not x0/x1) |
| `RhpInitialDynamicInterfaceDispatch` | Interface dispatch stub: the call site is `mov x11,cell; ldr x16,[x11]; blr x16`. The stub must preserve **all argument registers and x30 (LR)** across the call to the managed resolver `RhpResolveInterfaceMethod`, then tail-call the resolved method |
| `jit_*_shim_addr` (ensure-compiled, virtual, vtable-slot, interface, new-array, new-fast) | Must be **functions returning** the shim address (calling a data word executes data and crashes with PC=0/1) |
| `get_boot_info` / `set_boot_info` | `get` returns the stored pointer; `set` stores it (x64 contract) |
| `switch_context` | CPUContext mapping (x64 field offsets, ARM64 registers): `0x08`=x23, `0x28`=x29, `0x30`=x24, `0x38`=x25, `0x40`=x26, `0x48`=x27, `0x50`=x28, `0x58`=x19, `0x60`=x20, `0x68`=x21, `0x70`=x22, `0x78`=resume PC (LR), `0x80`=SP, `0x88`=DAIF (`& 0x3C0`) |
| `arm64_vectors` | VBAR_EL1 table: 16 entries × 0x80 bytes, 2 KB aligned, 4 kinds (sync/IRQ/FIQ/SError) × 4 origin classes |

## 3. Exceptions and interrupts

`ExceptionVectors.cs` + the vector stubs build an `Arm64ExceptionFrame`
whose `InterruptNumber` (0x88), `Esr` (0x90), `Elr` (0x98), `Spsr` (0xA0)
and `EntrySp` (0xB0) offsets match the x64 `InterruptFrame`, so the shared
dispatch/handler machinery works unchanged. IRQs are renumbered to the
x64 convention (vector = 32 + interrupt ID) and dispatched through
`Arch.DispatchInterruptManaged`; sync/SError print a full dump and halt.

## 4. GICv2 (QEMU virt) — the group-0 ack finding

Distributor `0x08000000`, CPU interface `0x08010000`. Firmware leaves its
own timer source pending+enabled, so `Init()` disables **everything**
first, then enables only kernel-owned sources: PPI 30 (EL1 physical timer,
vector 62) and SPI 33 (PL011 UART0, vector 65).

Three non-obvious facts, each learned the hard way (an IRQ-ack livelock
during bring-up):

1. **All interrupts must be configured GROUP 0.** A single-security-view
   GICv2 acknowledges only via **GICC_IAR (0x00C)**. Group-1 interrupts are
   deliberately *hidden* from that view — IAR reads `1022` (spurious)
   while the distributor still asserts the IRQ line, so the CPU re-enters
   the vector forever with nothing ackable. (QEMU `arm_gic.c`:
   `group == 1 && secure && !AckCtl → return 1022`; `AckCtl` is not
   writable without the security extensions, so group 1 is a dead end.)
2. **There are no aliased ack registers.** The "AIAR/AEOIR" registers at
   0x020/0x024 do not exist in this model — reads return constant 0 from
   an unmapped CPU-interface offset. Treating that 0 as "SGI 0 acked"
   was the visible symptom of the livelock.
3. **Debugger reads of the GIC are misleading** (gdbstub MMIO semantics
   differ from device semantics). The storm was ultimately diagnosed with
   a capped in-kernel trace printing the real per-entry IAR value.

Ack flow: read IAR (1020-1023 = spurious) → EOI via GICC_EOIR (0x010)
before dispatch (handlers may switch threads) → dispatch. Group 0 with
`FIQEn=0` signals as IRQ, which is what the VBAR_EL1 IRQ vector handles.

## 5. Generic timer and the scheduler

`GenericTimer` (CNTP): 1 ms periodic, `CNTP_CTL=1` (enable, unmasked),
`CNTVCT`-based frequency from `CNTFRQ` (62.5 MHz on QEMU). Each tick:
advance the profile sample, `Scheduler.TimerTick()`, program the next
expiry. `Scheduler.TimerTick` was changed to use an arch-neutral internal
tick counter (it previously read x64-only `APIC.TickCount`, which is
always 0 on ARM64 — the scheduler never actually ticked).

## 6. Console input (PL011 RX interrupt)

`Pl011` gained RX-interrupt support (`EnableRxInterrupt`, RX FIFO level,
`RxInterruptPending`). On ARM64 the Uart16550 driver branch enables GIC
SPI 33 + PL011 RX instead of 16550 registers, and the serial IRQ handler
drains the PL011 FIFO into the line discipline → shell. Verified by
typing `help` / `version` over a FIFO-fed serial pipe. The console echo
sink now installs whenever a serial console exists (VGA is no longer
required), which fixed missing echo on ARM64.

## 7. x64 vs ARM64 (current state)

| Concern | x64 | ARM64 |
|---|---|---|
| Boot | LOADER.EFI → KERNEL.BIN (ExitBootServices) | single-stage BOOTAA64.EFI (boot services stay live) |
| Interrupts | IDT, APIC/IOAPIC, PIC | VBAR_EL1 vectors, GICv2 (group 0) |
| Timer | HPET + APIC timer | CNTP generic timer (1 ms) |
| Serial IRQ | 16550 IRQ 4 via IOAPIC | PL011 SPI 33 via GICv2 |
| Video | VGA text console | none (serial only) |
| SMP | 4 CPUs (SMP bring-up) | single CPU |
| User mode | ring 3 + syscalls | not yet (kernel threads only; x64 user tests skipped) |
| PCI | ECAM works (AHCI/nvme/virtio) | ECAM unmapped — enumeration returns zeros (deferred) |
| RTC/HPET timing in BootLog | HPET | not wired (boot log shows t=0ms) |
| JIT | Tier-0 x64 emitter | not applicable yet (AOT runs; tests are x64-only) |

## 8. Verification

`build/p8-arm64-test.sh` (tracked; the `arm64` leg of
`tests/run-phase8-tests.ps1`) rebuilds the image and asserts:

```
PASS: arm64 rebuild (0 compiler errors, BOOTAA64.EFI present)
PASS: VBAR_EL1 vectors + GICv2 + Stage 2 live
PASS: generic timer ticking at shell start (ticks=49)
PASS: shell prompt reached, no sync exceptions / raw faults
PASS: typed 'help' over serial RX -> shell help text
PASS: typed 'version' over serial RX -> aarch64 banner
=== arm64 summary: ALL-PASS (6 passed) ===
```

x64 regression (same commit): boots with byte-identical test output —
2964 PASS / 0 FAIL, same category sequence, same raw-fault/exception
diagnostics as the pre-change baseline.

Boot-time comparison (Phase 8 benchmark, standard `make image`
products, QEMU 2 GB / 1 vCPU, wall clock to the shell prompt):
**x64 18.1 s vs ARM64 6.0 s** — the x64 standard image runs the
x64-only boot suites (Ring-3 syscalls, JIT tests) that ARM64 does not;
re-run with `bash build/p8-t6-bench.sh` (see `docs/PHASE8-REPORT.md`).

## 9. Debugging notes

* Kernel runtime base comes from the serial line
  `[BootInfo] ARM64 built from UEFI: ... image=0x...`; PE VA = `0x140000000 + (runtime − base)`.
* Symbols: `python3 tools/gen_elf_syms.py build/arm64/BOOTAA64.pdb build/arm64/kernel_syms_arm64.elf`;
  AOT names are `kernel_*` in NativeAOT but resolve in gdb for the arm64 image.
* AAVMF prints `Synchronous Exception at <PC>` for faults taken in firmware context.
* `qemu -d int` shows ELR/FAR/ESR for unhandled exceptions.
* Prefer capped in-kernel traces over debugger MMIO reads when the
  behavior involves interrupt-controller semantics.
