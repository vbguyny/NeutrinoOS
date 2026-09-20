# Building NeutrinoOS on Windows 11 (WSL2 + Ubuntu 24.04)

This guide takes a fresh Windows 11 machine with Visual Studio Code all the way
to a booting NeutrinoOS console image, verified under QEMU with OVMF UEFI
firmware and serial output visible in the VS Code integrated terminal.

NeutrinoOS is built **inside WSL2 Ubuntu 24.04**. The build scripts
(`build.sh`, `run.sh`, `Makefile`, `tools/*.sh`) are Bash/Linux tools and are
not supported in native PowerShell.

---

## 1. Prerequisites

| Requirement | Notes |
| --- | --- |
| Windows 11 (x64) | 22H2 or later recommended |
| Hardware virtualization | Enabled in BIOS/UEFI (required by WSL2) |
| WSL2 | Installed with an Ubuntu 24.04 distro |
| Visual Studio Code | With the **WSL** extension (`ms-vscode-remote.remote-wsl`) |
| ~20 GB free disk space | The toolchain build (runtime + bflat) is the large part |

Install WSL2 and Ubuntu 24.04 from an **Administrator** PowerShell:

```powershell
wsl --install -d Ubuntu-24.04
```

Reboot if asked, then launch "Ubuntu 24.04" once and create your Linux user.
Verify from PowerShell:

```powershell
wsl -l -v
#   NAME            STATE      VERSION
# * Ubuntu-24.04    Running    2
```

> **Tip:** building inside WSL's native filesystem (e.g. `~/src/NeutrinoOS`) is
> several times faster than building on `/mnt/c/...` or `/mnt/d/...`. A clone in
> the Windows filesystem still works for convenience.

## 2. Get the source into WSL2

```bash
sudo apt update && sudo apt install -y git
mkdir -p ~/src && cd ~/src

# Clone your fork of NeutrinoOS (or start from the upstream fork point):
git clone https://github.com/<your-account>/NeutrinoOS.git
cd NeutrinoOS
```

If you already have the repository on a Windows drive (for example
`D:\Projects\Code\neutrino`), it is visible in WSL2 as
`/mnt/d/Projects/Code/neutrino`; you can build it from there with the same
commands.

## 3. Install the toolchain

The supported path is the repository's own installer:

```bash
git submodule update --init --recursive   # tools/bflat and tools/runtime forks
make install-deps                         # system packages + .NET SDK 10 (sudo)
make deps                                 # build runtime + bflat (~10-15 min first time)
```

`make install-deps` runs `tools/install-deps.sh`, which on Ubuntu 24.04 installs:

```
cmake llvm lld clang build-essential python-is-python3 curl git libicu-dev
liblttng-ust-dev libssl-dev libkrb5-dev ninja-build cpio nasm mtools
qemu-system-x86 ovmf
```

plus the **.NET SDK 10** (via `dotnet-install.sh`) and the `dotnet-ildasm`
global tool. The `ovmf` package provides the UEFI firmware used by
`make run-qemu`; `qemu-system-x86` provides the emulator; `mtools`
(`mformat`/`mcopy`/`mmd`) builds the FAT32 boot image.

<details>
<summary>Manual equivalent (if you cannot run <code>make install-deps</code>)</summary>

```bash
sudo apt update
sudo apt install -y cmake llvm lld clang build-essential python-is-python3 \
    curl git libicu-dev liblttng-ust-dev libssl-dev libkrb5-dev ninja-build \
    cpio nasm mtools qemu-system-x86 ovmf

# .NET SDK 10
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 10.0
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
echo 'export DOTNET_ROOT="$HOME/.dotnet"' >> ~/.bashrc
echo 'export PATH="$DOTNET_ROOT:$PATH"'      >> ~/.bashrc

# Optional: IL disassembly tool used by ./build.sh
dotnet tool install --global dotnet-ildasm
export PATH="$PATH:$HOME/.dotnet/tools"
```

</details>

### About `bflat`

NeutrinoOS does **not** use the stock NuGet `bflat` global tool. The kernel
requires the ProtonOS fork of bflat, built from the `tools/bflat` submodule
against the matching ILCompiler from the `tools/runtime` submodule - that is
exactly what `make deps` produces
(`tools/bflat/src/bflat/bin/Release/net10.0/bflat.dll`, wired up in the
Makefile as `$(BFLAT)`).

Installing the stock tool (`dotnet tool install -g bflat`) is useful only for
experimenting with bflat itself; it cannot compile this kernel.

## 4. Open the repository in VS Code (Remote - WSL)

1. In VS Code on Windows, install the **WSL** extension
   (`ms-vscode-remote.remote-wsl`) from the Extensions view.
2. From the Ubuntu shell:

   ```bash
   cd ~/src/NeutrinoOS
   code .
   ```

   VS Code opens attached to WSL2; the title bar shows `WSL: Ubuntu-24.04`.
3. Open the integrated terminal (`` Ctrl+` ``) - it is a WSL2 Bash terminal,
   which is where every build/run command in this guide should be executed.

## 5. Build the image

```bash
make image
```

This compiles, in order: the kernel `native.asm`, the AOT kernel via bflat
(korlib + kernel sources), links `build/x64/BOOTX64.EFI` with `lld-link`,
assembles and links the UEFI bootloader (`build/x64/LOADER.EFI`), builds the
managed assemblies with the .NET SDK, and finally assembles the 64 MB FAT32
image:

```
build/x64/neutrinoos.img
├── /EFI/BOOT/BOOTX64.EFI     <- LOADER.EFI (NeutrinoOS bootloader)
├── /EFI/BOOT/KERNEL.BIN      <- BOOTX64.EFI (AOT kernel PE)
├── /JITTest.dll, /korlib.dll, /TestSupport.dll, /ProtonOS.DDK.dll, ...
├── /drivers/...              <- JIT-loaded drivers
└── /lib/ProtonOS.Net.dll
```

## 6. Run it in QEMU (serial console in your terminal)

```bash
make run-qemu
```

`tools/run-qemu.sh` boots `neutrinoos.img` with QEMU:

```
qemu-system-x86_64 -machine q35 -m 2G
    -drive if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd
    -drive if=pflash,format=raw,file=build/x64/OVMF_VARS.fd
    -drive file=build/x64/neutrinoos.img,format=raw,if=virtio
    -display none -serial stdio -no-reboot -no-shutdown
```

Serial output appears directly in the VS Code terminal. Exit QEMU with
`Ctrl+A` then `X`.

Expected output (abridged - the kernel also runs its test suites):

```
NeutrinoOS v0.1 (x86-64 UEFI)
[BOOT] Loading kernel...
[BOOT] Relocating kernel...
[BOOT] Copying files...
[BOOT] Exiting boot services...
  NeutrinoOS v0.1 (x86-64 UEFI)
[CONSOLE] Serial console initialized (ttyS0 @ 115200 8N1)
...
[SHELL] NeutrinoOS console ready.
neutrinoos>
```

Type characters: the kernel echoes them back over the serial console. Enter
starts a fresh line; backspace edits.

To capture the transcript to a file instead:

```bash
timeout 60 qemu-system-x86_64 ... -serial file:serial.log
```

### Firmware overrides

`make run-qemu` searches the standard Ubuntu OVMF locations. Override with:

```bash
OVMF_CODE=/path/to/OVMF_CODE.fd OVMF_VARS=/path/to/OVMF_VARS.fd make run-qemu
```

If the boot stalls at the OVMF shell, your firmware or QEMU build may lack the
virtio-blk UEFI driver; use `make run` (full test environment boots the image
via the default SATA/IDE path) or attach the image as IDE/SATA manually.

### Phase 2: interactive console + scripted acceptance

```bash
make run-qemu-serial        # interactive console on the terminal (Ctrl+A X quits)
make run-qemu-serial-log    # same, tees to build/x64/serial.log
```

From Windows 11 (PowerShell 7), the scripted acceptance boots QEMU in WSL,
drives the console with keystrokes and asserts the captured log:

```powershell
scripts/test-console.ps1              # rebuild + acceptance (~1-2 min)
scripts/test-console.ps1 -SkipBuild   # reuse the current image
```

Expected end state: `=== PHASE 2 CONSOLE CHECK: PASS ===` (includes the
JIT-app console test: `console_io_test: passed=46 failed=0`) —
(log: `build/x64/serial-conio.log`; see `docs/PHASE2-ACCEPTANCE.md`).

> **Quick note on boot markers:** the acceptance runner adds a
> `skip-boot-tests` marker file to the FAT image so the full in-boot test
> suites (several minutes) are skipped during console work. Markers persist
> in the image; `make image` rebuilds a clean one, and the runner deletes
> stale markers before each run.

## 7. VirtualBox (optional)

A reproducible script (convert → create VM → headless boot → assert
banner/shell → power off) is provided; it caps the wait at 60 s by default:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\test-vbox.ps1
powershell -ExecutionPolicy Bypass -File scripts\test-vbox.ps1 -TimeoutSec 240
```

**Current result (VirtualBox 7.1.8):** the loader runs under VirtualBox's EFI
firmware (`NeutrinoOS v0.1`, kernel relocation, `[BOOT] Exiting boot
services...`), after which VirtualBox's own firmware
(`VBoxEfiFirmware\...\CpuDxe`) raises a #GP and prints its exception dump to
the serial log (`build/vbox-serial.log`). QEMU + OVMF is unaffected — this
VirtualBox-EFI interaction is a tracked follow-up (see `PHASE1-REPORT.md`
§5).

To configure the VM manually instead:

```bash
make run-vbox          # requires VBoxManage on PATH
```

From Windows, `VBoxManage` is
`"C:\Program Files\Oracle\VirtualBox\VBoxManage.exe"`; run the script inside
WSL with that path added, or execute the printed commands directly in
PowerShell:

```powershell
VBoxManage createvm --name "NeutrinoOS" --ostype "Other_64" --register
VBoxManage modifyvm "NeutrinoOS" --firmware efi --chipset ich9 --memory 2048 --cpus 2
VBoxManage modifyvm "NeutrinoOS" --graphicscontroller vmsvga
VBoxManage storagectl "NeutrinoOS" --name "SATA" --add sata --controller IntelAhci
VBoxManage storageattach "NeutrinoOS" --storagectl "SATA" --port 0 --device 0 `
    --type hdd --medium build\x64\neutrinoos.vdi
VBoxManage modifyvm "NeutrinoOS" --uart1 0x3F8 4 --uartmode1 file C:\temp\neutrinoos-serial.log
VBoxManage startvm "NeutrinoOS" --type headless
```

(all paths adjusted for the Windows filesystem). The VM is headless - the only
interactive channel is the serial log file. A VGA **text-mode** console
(`vga0`) is on the roadmap but not part of Phase 1.

## 8. Troubleshooting

| Symptom | Fix |
| --- | --- |
| `make: command not found` | You are in PowerShell, not WSL. Run commands in the Ubuntu terminal / VS Code WSL terminal. |
| `dotnet: command not found` or wrong SDK | `make install-deps` installs SDK 10 to `$HOME/.dotnet`; open a new shell or re-source `~/.bashrc`. Check with `dotnet --list-sdks` (must show `10.x`). |
| `git submodule update` fails (auth) | The submodules are `github.com/ProtonOS/{bflat,runtime}` - public. Check network/proxy. |
| `make deps` takes very long | Expected: it builds a CLR subset plus the bflat fork (~10-15 min on a typical desktop). |
| `make image` fails with `Directory '.../tools/bflat/src/bflat/bin/Release/net10.0/lib/uefi/x64' doesn't exist` | The compiler is missing its target support files. Run `cd tools/bflat && dotnet build src/bflat/bflat.csproj -t:BuildLayouts -c Release` and copy `layouts/linux-glibc-x64/*` into `src/bflat/bin/Release/net10.0/`. |
| `make image` fails compiling `src/korlib/obj/.../korlib.AssemblyInfo.cs` (`AssemblyCompanyAttribute does not exist`) | Stale .NET build artifacts are being picked up by bflat. Remove them (`rm -rf src/korlib/obj src/korlib/bin`) and re-run `make image` - a full `./build.sh` already does this via `clean.sh`. |
| `make deps` fails with `MSB3030: Could not copy ... ilc/ILCompiler.Compiler.dll` | The ILCompiler pack step needs an absolute `IntermediateOutputPath` (pass the full path to the artifacts `ilc/` directory). Also make sure `clang` is installed. |
| Boot halts with `General Protection Fault` right after `[Drivers]`/`[JIT call]` logs | Known **pre-existing upstream issue** that reproduces on unmodified ProtonOS; the fault is in the kernel JIT runtime (`EnsureVtableSlotCompiled`). See [PHASE1-REPORT.md](../PHASE1-REPORT.md) sections 4.3-4.4 for the full analysis. |
| `Error: OVMF firmware not found` | `sudo apt install ovmf`, or pass `OVMF_CODE=... OVMF_VARS=...`. |
| QEMU runs but is slow | Without KVM, QEMU falls back to TCG emulation. Enable nested virtualization or accept slower boots. |
| `make run-qemu` hangs at boot | Try `make run` (uses the default SATA path); check that `build/x64/neutrinoos.img` exists. |
| `ARM64 not yet implemented` | Only x64 is supported; do not set `ARCH=arm64`. |
| Garbled serial output in VS Code terminal | The console is 115200 8N1 with CRLF translation; ensure no other program owns the serial device. |
