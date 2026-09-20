# Upstream bug report (draft) — file on ProtonOS/ProtonOS

Copy-paste the sections below into a new issue on the upstream repository
(`New issue` on github.com/ProtonOS/ProtonOS). Reproduced on unmodified
upstream `c4f6db2`; full analysis in `PHASE1-REPORT.md` §4.3–4.6 of the
NeutrinoOS fork, fixes in the fork's `main` (`a91ec2f` and earlier).

---

## Bare-metal boot dies on three defects (driver-JIT #GP, ring-3 exit kills
## boot thread, unhandled IRQs never EOI'd)

**Environment:** QEMU (q35, KVM) + OVMF 4M pflash, `neutrinoos.img` on
virtio-blk, 1–4 vCPUs, 512M–2G RAM. Reproduced with both the fork toolchain
and a stock bflat/ILCompiler build of upstream `c4f6db2`.

**1. Tier-0 JIT → AOT call stack misalignment (driver-JIT GP fault).**
The JIT-emitted virtual/interface dispatch call sequences push an odd number
of 8-byte temporaries plus 32 bytes of shadow space, leaving
`RSP ≡ 8 (mod 16)` at the `call`. AOT callees whose prologues use aligned
SSE stores (`movaps [rsp+x]`) then take `#GP`. First hit during driver load
(`[Drivers] …`); the CPU ends in `Arch.DefaultHandler`.
*Fix (fork):* alignment shims (`JIT_ALIGN_SHIM` in `native.asm`) for every
JIT-called helper; the JIT also routes all register-argument method calls
through a generic `jit_align_call` shim (target in R11). Calls with
stack-passed arguments must not be shimmed (the shim frame would move the
callee's stack arguments).

**2. Ring-3 test process exit destroys the boot thread.**
The syscall test suites run in ring 3 on the *boot* thread; `exit(0)` calls
`Scheduler.ExitThread` and destroys it, so kernel initialization never
reaches the console.
*Fix (fork):* setjmp-style `kernel_context_save`/`kernel_context_restore`
(`native.asm` + `InitProcess`); resume stores RIP and post-return RSP
explicitly and uses `jmp`, not `ret`.

**3. Unhandled IRQ vectors are never EOI'd.**
`Arch.DefaultHandler` returns without `EOI` for IRQ vectors with no
registered handler; the vector stays in service in the LAPIC and blocks the
timer (and everything at ≤ its priority) forever — the first `HLT`-based
wait (console input) freezes the system.
*Fix (fork):* EOI unhandled vectors (one-time notice per vector); mask the
IRQ2/PIC-cascade IOAPIC entry.

**4. Bootloader: `ExitBootServices` key made stale by a later ConOut print
(VirtualBox EFI).**
The bootloader fetches the UEFI memory map (and key) and then prints
"[BOOT] Exiting boot services..." via `ConOut->OutputString` before calling
`ExitBootServices`. On VirtualBox's EFI firmware that console call changes
the memory map, so the first `ExitBootServices` fails with
`EFI_INVALID_PARAMETER`; the firmware then #GPs inside its own `CpuDxe`
teardown on the retry. Additionally, `GetUefiMemoryMap` ignored the
returned status and used a fixed 8 KB buffer adjacent to the key/size
fields — a larger firmware map silently overruns them.
*Fix (fork):* re-fetch the map as the very last boot-services call; 64 KB
buffer with status-checked, bounded retry (never pass a size larger than
the buffer); keep interrupts disabled from `ExitBootServices` until the
kernel installs its own IDT (UEFI runs with `IF=1` and the firmware IDT).
Note: VirtualBox's EFI firmware also requires ≥2 vCPUs — with 1 vCPU its
`ExitBootServices` teardown #GPs regardless of the caller.
