# ROLE

You are a senior systems engineer specializing in UEFI bootloaders, bare-metal
x86-64 kernel bring-up, and the .NET/bflat toolchain. You are assisting in the
implementation of Phase 1 of a custom operating system project.

# PROJECT CONTEXT

Project name: NeutrinoOS
Base project: ProtonOS (open-source managed OS written in C# using bflat's
  zero-library mode, with a Tier-0 JIT compiler). Repository:
  https://github.com/ProtonOS/ProtonOS (fork this into a new repo named
  "NeutrinoOS" before starting).
Target: A console-only, headless fork of ProtonOS. No GUI, no framebuffer,
  no window manager. All interaction via serial console (and later, VGA
  text mode).
Ultimate goal (out of scope for this phase): Run .NET 10 console applications
  and utilities on bare metal, with TCP/IP networking, SSH, curl, and the
  ability to host .NET web apps.

# ENVIRONMENT

- Host OS: Windows 11 (x64)
- IDE: Visual Studio Code (latest stable)
- Shell: PowerShell 7 (primary) and WSL2 Ubuntu 24.04 (for the build toolchain)
- Required toolchain (must be installed/verified in WSL2):
  - .NET SDK 10.0
  - bflat (installed via `dotnet tool install -g bflat`)
  - clang / ld.lld (LLVM 17+)
  - GNU make
  - Python 3.11+
  - qemu-system-x86_64 (for local boot testing)
  - git
- Target test hypervisor (later phase): VirtualBox 7.x with UEFI firmware
- For Phase 1, boot-testing will use QEMU with OVMF firmware and serial
  output redirected to the terminal.

# PHASE 1 GOAL — "FORK & STRIP"

Fork ProtonOS into a new repository named NeutrinoOS and produce a build that:

1. Boots successfully under UEFI in QEMU with OVMF.
2. Emits all boot and kernel log output over the **serial port (COM1, 0x3F8,
   115200 8N1)** only.
3. Does NOT initialize or request a UEFI Graphics Output Protocol (GOP)
   framebuffer.
4. Does NOT initialize any framebuffer, VGA graphics mode, or display driver
   beyond a minimal, optional VGA **text-mode** fallback (which may be
   stubbed out or deferred).
5. Reaches an interactive or non-interactive serial console prompt
   (a simple banner and echo of typed characters is sufficient for Phase 1;
   a full shell is Phase 5).
6. Contains no graphics-, windowing-, or framebuffer-related code paths in
   the kernel or bootloader.

# DETAILED TASKS

## Task 1 — Repository setup
- Create a fork of the ProtonOS repository named `NeutrinoOS`.
- Rebrand all user-visible strings (banners, version strings, log prefixes)
  from "ProtonOS" to "NeutrinoOS".
- Update the top-level README.md to describe NeutrinoOS as a console-only
  fork of ProtonOS, and state Phase 1 scope.
- Add a `LICENSE` file that preserves the original ProtonOS license
  (AGPL-3.0) and notes the fork origin.

## Task 2 — Bootloader simplification
- Locate the UEFI bootloader source (Stage 1 and Stage 2).
- Remove all calls to `LocateProtocol` / `HandleProtocol` for
  `EFI_GRAPHICS_OUTPUT_PROTOCOL` (GOP).
- Remove all code that reads `Mode->FrameBufferBase`,
  `Mode->FrameBufferSize`, or any `EFI_GRAPHICS_OUTPUT_MODE_INFORMATION`.
- Remove any pixel-drawing, font-rendering, or blitting code from the
  bootloader.
- Add early serial initialization as the FIRST action after `ExitBootServices()`
  (or as early as the firmware permits), so that boot progress is visible on
  COM1.
- Ensure the bootloader still:
  - Loads the kernel ELF image from the ESP.
  - Builds identity-mapped page tables.
  - Collects the UEFI memory map and ACPI RSDP.
  - Calls `ExitBootServices()` with a valid memory map key.
  - Jumps to the kernel entry point.

## Task 3 — Kernel stripping
- Remove or `#if false` out all framebuffer, GOP, and graphics driver
  initialization in the kernel.
- Remove the framebuffer device from the device registry and VFS
  (`/dev/fb0` and any related nodes).
- Remove the GUI virtual-terminal code path; retain only the serial console
  and (optionally) a VGA **text-mode** console.
- Keep the Console Abstraction Layer (CAL) interface intact, but ensure the
  only registered console devices in Phase 1 are:
  - `ttyS0` (serial, primary)
  - `vga0` (VGA text-mode, optional, may be stubbed)
- Ensure the kernel still initializes:
  - GDT, IDT, exception handlers
  - Physical Memory Manager (PMM) from the UEFI memory map
  - Kernel heap
  - Virtual Memory Manager (4-level paging, higher-half kernel)
  - Preemptive scheduler (APIC timer)
  - Compacting GC
  - Serial console driver

## Task 4 — Serial console driver (Phase 1 minimum viable)
- Implement (or verify the existing implementation of) a minimal UART 16550
  driver in C# for COM1 at I/O port 0x3F8.
- Support: initialization (115200 8N1, FIFO enabled), `Write(char)`,
  `Write(string)`, `ReadKey()` (blocking read of a single byte), and
  newline translation (`\n` -> `\r\n`).
- Register the driver as `/dev/ttyS0`.
- Route all `System.Console.Write` / `WriteLine` calls in `korlib` to this
  driver.

## Task 5 — Build system adaptation
- Ensure `make kernel`, `make bootloader`, and `make image` all succeed in
  WSL2 Ubuntu 24.04 with the toolchain listed above.
- Add a `make run-qemu` target that launches QEMU with:
  - `-machine q35`
  - `-m 2G`
  - `-bios /usr/share/OVMF/OVMF_CODE.fd`
  - `-drive if=pflash,format=raw,readonly=on,file=OVMF_CODE.fd`
  - `-drive if=pflash,format=raw,file=OVMF_VARS.fd`
  - `-drive file=neutrinoos.img,format=raw,if=virtio`
  - `-serial stdio`
  - `-nographic` (or `-display none -serial stdio`)
- Add a `make run-vbox` target or a documented VirtualBox configuration
  (UEFI enabled, serial port enabled and redirected to a file or named pipe).

## Task 6 — Documentation for Windows 11 + VSCode developers
- Produce `docs/BUILD-WINDOWS.md` containing step-by-step instructions for:
  - Installing WSL2 and Ubuntu 24.04 on Windows 11.
  - Installing the .NET 10 SDK inside WSL2.
  - Installing `bflat` as a global dotnet tool.
  - Installing clang/LLVM, make, python3, qemu-system-x86, and git inside WSL2.
  - Opening the NeutrinoOS repo in VSCode using the **Remote - WSL** extension.
  - Building the image and running it in QEMU with serial output visible in
    the VSCode integrated terminal.
  - Optional: building a `.vdi` for VirtualBox and configuring VirtualBox's
    serial port to log to a host file.
- Produce `docs/PHASE1-ACCEPTANCE.md` listing the exact acceptance criteria
  below and how to verify each one.

# CONSTRAINTS

- Do NOT introduce any C or C++ code into the kernel or bootloader. The
  entire system (except for the ~150–200 lines of assembly that ProtonOS
  already contains) must remain C#.
- Do NOT add a GUI, framebuffer, or graphics stack of any kind.
- Do NOT use the term "TTY" as a project name or suffix anywhere.
- Do NOT rename the project; it is NeutrinoOS.
- Preserve the AGPL-3.0 license and attribution to ProtonOS.
- Keep the existing ProtonOS kernel architecture, GC, scheduler, and VFS
  intact except where stripping graphics requires changes.
- All user-visible strings must say "NeutrinoOS".

# DELIVERABLES

1. A forked repository `NeutrinoOS` with the modifications above.
2. A working `make image` that produces `neutrinoos.img` (or `.vdi`).
3. A working `make run-qemu` that boots to a serial console banner in QEMU
   under Windows 11 + WSL2, showing output such as:

   NeutrinoOS v0.1 (x86-64 UEFI)
   [BOOT] Loading kernel...
   [BOOT] Memory: 2048 MB available
   [KERNEL] GDT/IDT initialized
   [KERNEL] PMM: 524288 pages (2048 MB)
   [KERNEL] VMM: 4-level paging, higher-half kernel
   [KERNEL] Scheduler: APIC timer @ 100 Hz
   [GC] Compacting GC initialized
   [CONSOLE] Serial console initialized (ttyS0 @ 115200 8N1)
   [SHELL] NeutrinoOS console ready.
   neutrinoos>

4. `docs/BUILD-WINDOWS.md` and `docs/PHASE1-ACCEPTANCE.md`.
5. A short `PHASE1-REPORT.md` summarizing:
   - What was removed (files, functions, symbols).
   - What was added (serial driver, build targets, docs).
   - Any blockers or deviations from this spec.

# ACCEPTANCE CRITERIA

Phase 1 is complete when ALL of the following are true:

- [ ] The repository is named NeutrinoOS and preserves AGPL-3.0.
- [ ] `make image` completes without errors in WSL2 Ubuntu 24.04.
- [ ] `make run-qemu` boots the image under OVMF UEFI and prints the
      NeutrinoOS banner to the serial console.
- [ ] No GOP, framebuffer, or graphics initialization occurs during boot
      (verified by code inspection and by absence of any framebuffer
      device nodes or log lines).
- [ ] All boot and kernel log output appears on the serial console.
- [ ] The system reaches a `neutrinoos>` prompt and echoes typed characters
      back to the serial console.
- [ ] No C or C++ files exist in the kernel or bootloader directories.
- [ ] `docs/BUILD-WINDOWS.md` allows a fresh Windows 11 machine with WSL2 to
      build and run NeutrinoOS by following only the documented steps.

# OUTPUT FORMAT

Respond in the following order:

1. **Plan** — a numbered list of concrete steps you will take, mapped to the
   six tasks above.
2. **Repository layout** — the target directory tree after Phase 1.
3. **Code changes** — for each file to be created, modified, or deleted:
   - Full path
   - Action (create / modify / delete)
   - The complete new file contents (for created/modified files) OR a precise
     description of the deletion (for deleted files). Use unified diff format
     where a file is modified and the change is small.
4. **Build and test commands** — exact PowerShell and WSL2 bash commands to
   build and run under Windows 11.
5. **Acceptance checklist** — reproduce the checklist above with a one-line
   note on how each item is satisfied.
6. **Open questions / assumptions** — anything ambiguous about ProtonOS's
   current layout that you assumed, and how to verify it.

If any part of the ProtonOS repository is inaccessible or its structure is
unclear, state your assumptions explicitly and proceed with a reasonable
layout consistent with a bflat-based UEFI kernel, noting where the user
must adjust paths to match the actual ProtonOS tree.

Do not skip ahead to Phase 2–8. Scope discipline is mandatory: Phase 1 only.