# SDK — Testing in QEMU and VirtualBox (from Windows 11)

Three practical loops, fastest first.

---

## 1. QEMU (WSL) — the fast development loop

Everything runs from the WSL2 dev container that built the kernel:

```bash
./build.sh          # clean build (x64)          [in /root/neutrino]
./run.sh            # boot in QEMU; serial -> stdout + qemu.log
./kill.sh           # stop QEMU (always run between tests)
```

Get your package into the guest with the repository server:

```bash
# terminal 1 (WSL): build and serve
bash build/p8-sdk-build.sh
export PATH=/root/p8sdk:$PATH
cp MyApp/bin/Release/net10.0/MyApp.npkg /root/repo/
/root/p8sdk/npkg-host repo-index --dir /root/repo
cd /root/repo && python3 -m http.server 8080     # or npkg-repo-server
```

```text
# guest console (run.sh)
neutrinoos> npkg repo add local http://10.0.2.2:8080
neutrinoos> npkg update
neutrinoos> npkg install MyApp
neutrinoos> MyApp
```

`10.0.2.2` is the QEMU user-net gateway (the host) — always available.

Offline alternative (mcopy into the image, then `run`):

```bash
mcopy -o -i build/x64/neutrinoos.img MyApp.npkg ::/apps/MyApp.npkg
neutrinoos> run /apps/MyApp.npkg
```

## 2. QEMU (Windows 11)

`scripts\run-qemu.ps1` boots a `dist\*.qcow2`/raw image with QEMU for
Windows (`winget install qemu`):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\run-qemu.ps1 -Serial
```

* serial to the console; without `-Serial` the log goes to `dist\serial.log`
* host forwards: SSH `localhost:2222`, HTTP `8080`, HTTPS `8444`
  (after `dhcp` + `sshd` in the guest)
* guest → host: `10.0.2.2` (so `npkg repo add local http://10.0.2.2:8080`
  works with `scripts\start-repo-server.ps1` running on Windows)

## 3. VirtualBox (GUI)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\gui-vm.ps1            # build VM + start window
powershell -ExecutionPolicy Bypass -File scripts\gui-vm.ps1 -Rebuild   # rebuild kernel in WSL first
```

* window shows the `[Boot]` timeline and the shell; type commands directly
* serial transcript: `build\vbox-gui-serial.log`
* VirtualBox NAT: the host is also `10.0.2.2` for guest → host traffic

The functional acceptance VM (`scripts\test-vbox.ps1`, "NeutrinoOSTest")
boots `build\neutrinoos.img` headless and asserts the banner + prompt —
useful as a post-install check.

## Smoke checklist for a new package

1. `npkg-host verify MyApp.npkg` — checksums + (signed) OK.
2. `npkg install MyApp.npkg` on the target image — install path correct
   (`/apps`, `/bin`, `/drivers`, `/lib`).
3. Run it: `MyApp` (on `$PATH`) or `run /apps/MyApp.dll`.
4. Check `echo $?` after a failure path (exit codes flow through).
5. `npkg remove MyApp` leaves no stray files (`ls /apps`).

## Logs that help

| What | Where |
|---|---|
| WSL QEMU serial | `/root/neutrino/qemu.log` |
| Windows QEMU serial | `dist\serial.log` (or `-Serial` stdio) |
| VirtualBox serial | `build\vbox-gui-serial.log` |
| Guest kernel log | `cat /var/log/...`? — boot log is on the console; `dmesg`-style dump via `procinfo` |
| Package journal (guest) | `/var/lib/npkg/journal.jsonl` |
