# Installing NeutrinoOS v1.0.0 on Windows 11

Three supported paths. All need the release artifacts (`neutrinoos-1.0.0.img`
or `.ova`) and, ideally, `SHA256SUMS` verification first:

```powershell
# verify the download (after importing RELEASE-KEY.asc and checking SHA256SUMS.asc)
Get-FileHash -Algorithm SHA256 .\neutrinoos-1.0.0.img
```

---

## Option A — VirtualBox (OVA appliance, recommended)

Prerequisites: [VirtualBox 7.x](https://www.virtualbox.org/) (free).

1. Import the appliance:

   ```powershell
   VBoxManage import .\neutrinoos-1.0.0.ova
   # adjust resources if desired:
   # VBoxManage import .\neutrinoos-1.0.0.ova --vsys 0 --memory 2048 --cpus 1
   ```

   The appliance ships with: EFI firmware, 2 GB RAM, 1-4 vCPU, virtio-net
   NIC, serial port redirected to `%USERPROFILE%\NeutrinoOS\serial.log`.

2. Start it:

   ```powershell
   VBoxManage startvm NeutrinoOSCli        # GUI window
   # or headless with serial only:
   # VBoxManage startvm NeutrinoOSCli --type headless
   ```

3. In the VM window you land in the NeutrinoOS shell (VGA console).
   Type commands directly. `Ctrl+Click` the window to release the mouse.

4. Bring up networking (VirtualBox NAT DHCP):

   ```
   neutrinoos> dhcp
   neutrinoos> sshd          # start the SSH server
   neutrinoos> webhost       # start the web host (HTTP + HTTPS)
   ```

5. From the Windows host (add a port-forward in VirtualBox Network >
   Advanced > Port Forwarding: TCP 2222→22, 8080→80, 8444→443), then:

   ```powershell
   ssh -i <key> -p 2222 user@127.0.0.1
   curl.exe -k https://127.0.0.1:8444/health
   ```

   Note: password logins are **disabled by default**. Either provision an
   `authorized_keys` entry for `user`, or set
   `PasswordAuthentication=yes` in `/etc/ssh/sshd_config` and restart `sshd`.

---

## Option B — Hyper-V (Generation 2 VM from the raw image)

Prerequisites: Windows 11 Pro/Enterprise with the Hyper-V feature enabled
("Turn Windows features on or off" → Hyper-V → restart).

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-neutrinoos.ps1 `
    -Image .\neutrinoos-1.0.0.img
```

The script:

1. converts the raw image to a dynamic `.vhdx` (`Convert-VHD`),
2. creates a Generation-2 (UEFI) VM named `NeutrinoOS`,
   2 GB RAM / 1 vCPU, **Secure Boot disabled** (NeutrinoOS loads an
   unsigned image), COM1 redirected to `.\NeutrinoOS-serial.log`,
3. attaches the disk and starts the VM.

Connect with `vmconnect localhost NeutrinoOS`. Re-run with `-Force` to
recreate. Watch progress in `NeutrinoOS-serial.log` if the window is slow.

---

## Option C — QEMU on Windows (qcow2)

Prerequisites: QEMU for Windows (`winget install qemu`) + an OVMF build
(often bundled as `OVMF_CODE.fd` under the QEMU install directory).

```powershell
& $env:PROGRAMFILES\qemu\qemu-system-x86_64.exe `
  -machine q35 -m 2G -smp 1 `
  -drive if=pflash,format=raw,readonly=on,file=C:\Program Files\qemu\share\edk2-x86_64-code.fd `
  -drive format=qcow2,file=.\neutrinoos-1.0.0.qcow2 `
  -netdev user,id=n0,hostfwd=tcp::2222-:22,hostfwd=tcp::8080-:80 `
  -device virtio-net-pci,netdev=n0 `
  -serial stdio
```

The exact argument list (including OVMF hints) is embedded in
`release.json` under `install.qemu`.

---

## USB stick (UEFI hardware)

> ⚠️ The write is destructive. Boot the target machine from the stick via
> its UEFI boot menu.

```powershell
# elevated PowerShell
powershell -ExecutionPolicy Bypass -File scripts\flash-usb.ps1 `
    -Image .\neutrinoos-1.0.0.img -Drive E:
```

The script verifies the target is a USB disk and asks you to type the disk
number before writing. If your firmware does not offer the stick in its
boot menu (some expect a partition table), use Rufus in "DD image" mode or
prefer a hypervisor.

---

## First-boot checklist

- `uname -a` → `NeutrinoOS 1.0.0`
- `cat /etc/neutrinoos-release`
- `dhcp` → `ifconfig` shows a lease
- `sshd` / `webhost` → from the host: `ssh`, `curl -k https://.../health`
- `df` → boot volume mounted
- Security spot-check: the console log around boot contains
  `[SEC] result: 4 pass, 0 fail`.

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| VM window shows nothing | Check `serial.log`; ensure EFI firmware (not BIOS) is selected |
| Secure Boot refuses the VM | Disable Secure Boot (unsigned image) — the installer does this |
| No network | `dhcp`, then check `ifconfig`; attach a virtio-net or E1000 NIC |
| SSH: "Permission denied (publickey)" | Expected default: provision `authorized_keys` or enable password auth in `/etc/ssh/sshd_config` |
| SSH locked out | Per-IP lockout after 5 failed logins (default 5 min) — wait or reboot |
