# NeutrinoOS Phase 8 — Acceptance Verification

Step-by-step verification for every Phase 8 acceptance criterion, from a
fresh Windows 11 machine. Everything is machine-checkable;
`tests/run-phase8-tests.ps1` runs the whole set in order.

## Acceptance results (recorded on this codebase)

| # | Criterion (spec) | Evidence | Result |
|---|------------------|----------|--------|
| 1 | `make image` / `make run-qemu-vga` boot to the banner on both consoles | standard x64 image boots to `[SHELL] NeutrinoOS console ready.` + `neutrinoos>` on serial; VGA window via `make run-qemu-vga` / `scripts/gui-vm.ps1`; bench x64 boot 18.1 s (incl. boot tests) | ✅ |
| 2 | `npkg install` installs + dependencies + signature + database | `p8-npkg-test.sh` 34/34: install utility/app/driver/chain, `installed <pkg>` lines, `npkg list` shows the database | ✅ |
| 3 | `npkg remove` refuses dependents without `--force` | `remove-guard` step: `tests.chain-c is required by ...` | ✅ |
| 4 | `npkg upgrade` to the latest compatible version | `tests.hello-utility 1.0.0 -> 1.1.0`; `npkg info` shows `version: 1.1.0` | ✅ |
| 5 | `npkg list/search/info/repo add/list/remove` | acceptance steps + priority/label assertions (`local@10` before `backup@70`) | ✅ |
| 6 | Tampered payload rejected by verification | `npkg verify /repo/tampered.npkg` → `checksums: FAIL` (guest) and `[FAIL] checksums (payload/helloutil.dll)` (host CLI) | ✅ |
| 7 | Driver framework loads the ported drivers | `p8-t2probe.sh`: 7 drivers registered, devices started (`e1000`, `ahci`, `uart16550`, `ps2-keyboard`, `vga-text`, virtio-net/blk nodes) | ✅ (hosts: see deviations) |
| 8 | Hot-plug: add/remove VirtIO device loads/unloads driver | `p8-hotplug-test.sh` 9/9: `[drv] bound 'virtio-net'` 334 ms after `device_add`; `[drv] stopped 'virtio-net'` 329 ms after `device_del` | ✅ |
| 9 | ARM64 boots to shell (QEMU virt + AAVMF + PL011) | `p8-arm64-test.sh` ALL-PASS: VBAR_EL1, GICv2, ticks>0, prompt, `help`/`version` over PL011 RX; boot-to-shell 6.0 s | ✅ |
| 10 | `make run-qemu-arm64` works on Windows 11 + WSL2 | Makefile target (`qemu-system-aarch64`, AAVMF, virtio-blk) | ✅ |
| 11 | `dotnet new neutrino-console -n MyApp` works | SDK leg: all five templates install and build | ✅ |
| 12 | `dotnet build -c Release` produces `.npkg` | SDK leg: MSBuild targets emit the package after Release builds | ✅ |
| 13 | `npkg sign` / `npkg publish` from Windows 11 | host CLI `bin/npkg-host sign <pkg> <key>` / `publish <pkg> --repo <dir>` (runs on Windows with .NET 10; SDK leg exercises both) | ✅ |
| 14 | Local repository server serves packages | repo leg: HTTP index/sig/package serving with byte-parity vs host CLI | ✅ |
| 15 | `npkg install MyApp` then runs from the shell | npkg leg: application installed, wrapper in `/bin` runs (`hello-app ok`) | ✅ |
| 16 | Driver template packages/installs/loads | samples leg packs `neutrino-driver` output; drivers leg loads packaged driver (`preplaced.drvtest`) through the loader | ✅ |
| 17 | SDK docs enable a third-party developer | `docs/PHASE8-SDK.md`, `SDK-GETTING-STARTED.md`, `SDK-PACKAGING.md`, `SDK-TESTING.md`, `SDK-CICD.md`, `SDK-API-REFERENCE.md`; archive leg assembles `dist/neutrinoos-sdk-1.0.0.tar.gz` | ✅ |
| 18 | No C/C++ in kernel/bootloader/drivers/korlib/shell/utility/ssh/web/npkg/SDK | `find` over those trees returns no `.c/.cpp/.cc/.h` | ✅ |
| 19 | Earlier docs still work; PHASE8-ACCEPTANCE does the same for Phase 8 | this document + `tests/run-phase8-tests.ps1` | ✅ |

Known deviations (driver hosts in-kernel, HTTPS repo fetch host-side only,
etc.) are documented in `PHASE8-REPORT.md` and the per-area docs.

## 0. Prerequisites (fresh Windows 11)

1. Follow `docs/BUILD-WINDOWS.md` §1–3: WSL2 + Ubuntu 24.04,
   `make install-deps`, `make deps` (builds bflat + runtime), plus QEMU
   and the ARM64 firmware:

   ```bash
   sudo apt install -y qemu-system-x86 qemu-system-arm ovmf qemu-efi-aarch64 \
                       mtools python3 rsync
   ```

2. Clone/enter the repository (inside WSL or on `/mnt/d/...`):

   ```bash
   cd /mnt/d/Projects/Code/NeutrinoOS
   ```

3. Generate/confirm the test signing keys (`tests/npkg/keys/`) if absent:

   ```bash
   ls tests/npkg/keys/private.key tests/npkg/keys/public.key
   # missing? -> bash build/p8-t5-keygen.sh
   ```

## 1. Build everything

```bash
# x64 kernel + standard image (build/x64/neutrinoos.img)
make image

# ARM64 kernel + image (build/arm64/neutrinoos.img, BOOTAA64.EFI)
make image ARCH=arm64

# Host tooling + fixtures + signed test repository (fixtures incl. the
# benchmark set and the tampered package)
bash build/p8-npkg-tests-build.sh        # -> /root/p8repo (NPKG TEST PACKAGES OK)

# npkg test image (base image + /bin utilities + /repo + trusted key)
bash build/p8-npkg-deploy.sh             # -> /root/npkgtest.img  (NPKG IMAGE OK)
# (P8_PREPLACE=off for the plain npkg acceptance baseline; full is the default)
```

The utilities (incl. `npkg.dll`) come from `bash build/p5-apps-build.sh`;
the SDK-side host tools from `bash build/p8-sdk-build.sh` (both are run
automatically where needed by the scripts above / the test runner).

## 2. One-shot suite

```powershell
# Windows 11 PowerShell; runs all nine legs (~45-60 min)
pwsh -File tests\run-phase8-tests.ps1

# individual legs
pwsh -File tests\run-phase8-tests.ps1 -Only npkg
pwsh -File tests\run-phase8-tests.ps1 -Only hotplug
pwsh -File tests\run-phase8-tests.ps1 -Only arm64
```

Legs: `npkg`, `drivers`, `hotplug`, `arm64`, `sdk`, `repo`, `samples`,
`archive`, `ecosystem`. The npkg/hotplug legs need the deployed
`/root/npkgtest.img` (step 1). Each leg prints PASS/FAIL lines with a
summary; the runner ends with `ALL-PASS (<n> checks)`.

## 3. Criterion-by-criterion walkthrough

### 3.1 Boot on both consoles (criterion 1)

```bash
make run-qemu-vga        # VGA window + serial; "neutrinoos>" prompt
make run-qemu-serial     # serial only
make run-qemu-arm64      # ARM64 (QEMU virt + AAVMF + PL011)
```

Expected: `[OK] Kernel initialization complete` / `[SHELL] NeutrinoOS
console ready.` / `neutrinoos>`.

### 3.2 npkg on the device (criteria 2–6)

Boot the npkg image and run the acceptance steps manually (the suite
automates exactly these):

```text
npkg repo add local /repo
npkg repo add backup /repo --priority 70
npkg repo add local /repo --priority 10
npkg repo list                       # PRIO column: local 10, backup 70
npkg search hello                    # [local] entries listed before [backup]
npkg list                            # "no packages installed" on a fresh image
npkg install tests.hello-utility@1.0.0
npkg install tests.hello-app
npkg install tests.hello-driver
npkg install tests.chain-a           # chain-b, chain-c install transitively
npkg remove tests.chain-c            # refused: "is required by"
npkg upgrade                         # tests.hello-utility 1.0.0 -> 1.1.0
npkg info tests.hello-utility        # version: 1.1.0
npkg verify /repo/tests.hello-utility-1.0.0.npkg   # checksums: OK
npkg verify /repo/tampered.npkg      # checksums: FAIL   (tamper rejection)
npkg install tests.conflict-w        # conflicting version requirements ...
npkg install tests.conflict-x        # installs libz 1.0.0 + conflict-x
npkg install tests.conflict-y        # "... is already installed and does not satisfy ..."
npkg install does.not.exist          # package not found in any repository
npkg remove tests.conflict-x
npkg remove tests.libz
```

Run the installed application from the shell: `helloapp` →
`hello-app ok` (wrapper in `/bin` invokes `/apps/...`).

### 3.3 Driver framework (criterion 7)

```bash
bash build/p8-t2probe.sh
```

Expected: `[drv] <n> driver(s) registered, <m> device(s) started` with
`bound 'e1000' / 'ahci' / 'uart16550' / 'ps2-keyboard' / 'vga-text'`
lines, and the packaged driver loaded on a `P8_PREPLACE=full` image.

### 3.4 Hot-plug (criterion 8)

```bash
bash build/p8-hotplug-test.sh
```

The script boots q35 with an empty `pcie-root-port`, hot-adds a
virtio-net-pci through the QEMU monitor and then removes it:

```text
[hotplug] root port pci/00:03.0 bus=1 slot=yes
[hotplug] monitoring 1 PCIe root port bus(es), poll 200ms
(qemu) device_add virtio-net-pci,id=hpnic0,bus=hp0
[hotplug] device added: pci/01:00.0 vid:did=1AF4:1041
[drv] bound 'virtio-net' to pci/01:00.0/virtio-net        # 334 ms
(qemu) device_del hpnic0
[hotplug] acpi eject bsel=1 slots=0x1
[hotplug] device removed: pci/01:00.0
[drv] stopped 'virtio-net' (pci/01:00.0/virtio-net)       # 329 ms
```

Expected verdict: `hotplug summary: ALL-PASS` (9 checks).

### 3.5 ARM64 (criteria 9–10)

```bash
make run-qemu-arm64          # interactive
bash build/p8-arm64-test.sh  # automated: rebuild + boot + PL011 input
```

Expected: `arm64 summary: ALL-PASS` (vectors, GICv2, ticks, prompt,
`help`/`version` over PL011 RX).

### 3.6 SDK workflow (criteria 11–17)

On Windows 11 with .NET 10 (the SDK archive from `bash
build/p8-sdk-archive.sh` gives `bin/npkg-host`, libs, MSBuild targets,
templates and docs — see `docs/SDK-GETTING-STARTED.md`):

```powershell
# one-time: templates + host tools
dotnet new install .\neutrinoos-sdk\templates
$env:PATH = "$PWD\neutrinoos-sdk\bin;$env:PATH"   # gives npkg-host

dotnet new neutrino-console -n MyApp
cd MyApp; dotnet build -c Release      # -> bin\Release\net10.0\MyApp.npkg
cd ..
dotnet new neutrino-driver -n MyDriver
cd MyDriver; dotnet build -c Release; cd ..   # -> MyDriver.npkg

npkg-host sign MyApp\bin\Release\net10.0\MyApp.npkg tests\npkg\keys\private.key
npkg-host publish MyApp\bin\Release\net10.0\MyApp.npkg --repo $env:USERPROFILE\.neutrinoos\repo
scripts\start-repo-server.ps1          # serves the repo over HTTP
```

Then on NeutrinoOS (QEMU user networking: host = `10.0.2.2`):

```text
npkg repo add local http://10.0.2.2:8080
npkg update
npkg install MyApp
MyApp
```

The automated equivalents are the `sdk`, `repo`, `samples`, `archive`
legs (ALL-PASS).

### 3.7 Constraints (criterion 18)

```bash
find src/kernel src/bootloader src/drivers src/ddk src/utilities src/lib sdk \
     -name '*.c' -o -name '*.cpp' -o -name '*.cc' -o -name '*.h'
# -> no results
```

### 3.8 Backwards compatibility (criterion 19)

`docs/BUILD-WINDOWS.md` and `docs/PHASE2-ACCEPTANCE.md` …
`docs/PHASE7-ACCEPTANCE.md` continue to apply; `make run-qemu-vga` and
the Phase 7 release/security behaviors were regression-checked in the
Phase 8 work (boot banner, SEC self-test, shell).

## 4. Benchmarks

Re-run:

```bash
bash build/p8-t6-bench.sh        # x64 boot, arm64 boot, 10-dep npkg install
bash build/p8-hotplug-test.sh    # driver load/unload times
```

Recorded on this machine (QEMU 8.2, 2 GB / 1 vCPU, WSL2; ±5% run-to-run):

| Benchmark | Result | Notes |
|-----------|--------|-------|
| `npkg install` with 10 dependencies | **38.2 s** | 11 packages (10 deps + root), each fetched from the local FAT repo, Ed25519-verified, checksum-verified, extracted and journalled |
| Driver load on hot-plugged VirtIO | **334 ms** | `device_add` (monitor) → `[drv] bound`; includes the 200 ms poll interval |
| Driver unload (hot-remove) | **329 ms** | `device_del` → `[drv] stopped` (ACPI eject handshake + bus rescan) |
| x64 boot to shell | **18.1 s** | standard `make image` product (boot test suites enabled) |
| ARM64 boot to shell | **6.0 s** | same method, standard ARM64 image (x64-only suites such as Ring-3 do not run on ARM64) |

## 5. Deviations

See `PHASE8-REPORT.md` (driver hosts run as managed in-kernel host
contexts; HTTPS repositories are fetched host-side only; the guest npkg
uses HTTP or local paths; ARM64 hot-plug is x64-only as documented in
`docs/PHASE8-DRIVER.md`).
