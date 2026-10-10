# SSH on NeutrinoOS — setup and connecting

The NeutrinoOS SSH server (`sshd`) implements SSH-2.0 with enough of the
protocol that **stock OpenSSH clients, `scp`, `sftp` and WinSCP work**
against it — including file transfer over the SFTP subsystem. This guide
covers preparing a VM image, starting the server, and connecting from
**Windows 11**.

| Layer | Supported |
|-------|-----------|
| Key exchange | `curve25519-sha256` |
| Host key | `ssh-ed25519` (generated on first start) |
| Ciphers | `aes128-ctr`, `aes256-ctr` |
| MACs | `hmac-sha2-256/512` and `-etm@openssh.com` variants |
| Auth | `publickey` (Ed25519, `~/.ssh/authorized_keys`) and `password` (scrypt `/etc/shadow`) |
| Sessions | interactive shell (cursor keys, history, Ctrl+C/D) and one-shot `exec` |
| Transfer | **`sftp` subsystem (SFTP v3)** — `sftp`, modern `scp`, WinSCP |
| Robustness | client-initiated rekey, 4 concurrent connections, brute-force lockout |

Limits (by design, see below): no port/agent forwarding, no SFTP symlinks,
and file transfers stream in chunks (no size cap; individual file
positions are bounded to 2 GiB).

## Quick start (Windows 11 + WSL2 + QEMU)

```powershell
# 1. Build the OS image (once; WSL handles the toolchain):
wsl -d Ubuntu-24.04 -u root -- bash /mnt/d/Projects/Code/NeutrinoOS/build/rebuild-cli-image.sh

# 2. Prepare it for SSH (adds the `user` account + sshd_config, and
#    optionally installs your public key):
wsl -d Ubuntu-24.04 -u root -- bash /mnt/d/Projects/Code/NeutrinoOS/build/ssh-cli-image.sh

# 3. Boot it with guest port 22 forwarded to host port 2222:
wsl -d Ubuntu-24.04 -u root -- bash /mnt/d/Projects/Code/NeutrinoOS/build/ssh-vm.sh
```

In the VM console type:

```text
sshd
```

Then, **from Windows 11** (OpenSSH client is built in; check with `ssh -V`):

```powershell
ssh -p 2222 user@127.0.0.1        # password: neutrino (set by the setup script)
```

WSL2 forwards `127.0.0.1:2222` to Windows automatically.

### VirtualBox alternative

`scripts\cli-vm.ps1` already NAT-forwards host 2222 → guest 22. Run
`ssh-cli-image.sh` first (it makes `build\neutrinoos-cli.img` SSH-ready),
then create/start the VM as usual and `ssh -p 2222 user@127.0.0.1`.

## Using your own key (recommended)

```powershell
ssh-keygen -t ed25519                              # if you don't have one yet
wsl -d Ubuntu-24.04 -u root -- bash /mnt/d/Projects/Code/NeutrinoOS/build/ssh-cli-image.sh /mnt/c/Users/<you>/.ssh/id_ed25519.pub
```

Then connect with:

```powershell
ssh -i $env:USERPROFILE\.ssh\id_ed25519 -p 2222 user@127.0.0.1
```

The host key is generated fresh per image, so the first connection shows
the usual authenticity prompt — accept it (or use
`-o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL` in throwaway
scripts; on Windows use `NUL`, in WSL use `/dev/null`).

## Running commands and transferring files

```powershell
# One-shot command execution
ssh -p 2222 user@127.0.0.1 "uname"
ssh -p 2222 user@127.0.0.1 "ls /bin"

# Interactive session, then transfer files:
sftp -P 2222 user@127.0.0.1
  pwd                       # /home/user
  put local.txt remote.txt
  get remote.txt back.txt
  ls -l
  rename remote.txt renamed.txt
  rm renamed.txt
  mkdir dirx
  rmdir dirx
  bye

# scp works in both directions (modern OpenSSH scp uses the SFTP subsystem)
scp -P 2222 .\notes.txt user@127.0.0.1:/home/user/notes.txt
scp -P 2222 user@127.0.0.1:/home/user/notes.txt .\notes-back.txt
```

**WinSCP**: new site → protocol *SFTP*, host `127.0.0.1`, port `2222`,
user `user`, password `neutrino` (or your key). No known-hosts quirks.

**Large files (chunked VFS access, no size cap).** Transfers stream
through offset-based file access - `Kernel_BootFileReadRange` /
`Kernel_BootFileWriteRange` in the kernel (`src/kernel/Exports/DDK/FileRangeExports.cs`),
wrapped by `BootFiles.ReadRange` / `WriteRange` in the DDK
(`src/ddk/Kernel/BootFiles.cs`). The file is never loaded whole into
memory and per-request buffers stay at 32 KiB, safely below the 85 KB
large-object threshold (the LOH free list is known to corrupt live
objects), so `sftp`/`scp`/WinSCP transfer files of any size. Individual
positions are 32-bit, so offsets under 2 GiB work (files may be
larger); writing past end-of-file is refused (no sparse files).
Chunked access covers the FAT boot volume; paths served by mounted
volumes (exFAT sticks) or virtual `/dev` files answer FAILURE for
range I/O.

## Configuration

`/etc/ssh/sshd_config` (created by `ssh-cli-image.sh`; key=value):

| Key | Default | Meaning |
|-----|---------|---------|
| `Port` | `22` | listen port |
| `ListenAddress` | – | accepted (informational) |
| `PasswordAuthentication` | `no` | set `yes` to allow password logins (the setup script does) |
| `PubkeyAuthentication` | `yes` | key logins via `AuthorizedKeysFile` |
| `AuthorizedKeysFile` | `~/.ssh/authorized_keys` | mapped to `/home/<user>/.ssh/authorized_keys` |
| `MaxAuthAttempts` | `6` | per-connection failures before disconnect |
| `BanThreshold` / `BanSeconds` | `10` / `60` | per-IP lockout after N failures for N seconds |

Notes:

- The host key lives at `/etc/ssh/ssh_host_ed25519_key` (32-byte seed,
  hex — NeutrinoOS format) and is generated on the first `sshd` start.
- `root` network logins are refused by policy (console only) — use `user`.
- Autostart at boot: add `sshd.autostart=yes` to `/etc/boot.params`, or a
  `sshd` line to `/etc/rc.local`.
- The image fixture (users, password, sshd_config) is applied by
  `build/ssh-cli-image.sh`; **a rebuild regenerates a pristine image, so
  re-run it after `rebuild-cli-image.sh`.**

## Verify

```bash
# In WSL: boots the CLI image, starts sshd, runs the full matrix
bash build/ssh-probe.sh
```

Expected: `PASSED=21 FAILED=0` — exec, interactive shell, cursor editing,
history, forced rekey, password auth, `sftp` batch, `scp` round-trips
(byte-identical), 2 MiB chunked transfers (byte-identical), and negative
key/password checks.

The original Phase 6 suite `build/p6-ssh-test.sh` is green again as well:
it boots the *plain* deploy image (boot tests included) and runs exec,
a piped interactive session, a negative wrong-key check and password
auth — verified together with JITTest 2978/0 and AppTest 24/0 completing
in ~13 s. As of v0.1.119 the SFTP server streams files of any size
through chunked VFS access (see "Large files" above; previously capped
at 64 KiB).

> History (resolved, v0.1.104): plain-image boots used to stall forever in
> the JITTest phase. A `[Conditional("NEUTRINO_TRACE")]` trace call whose
> argument carried the loop advance — `while (*p != 0) { JitTrace.WriteChar((char)*p++); }`
> — had the call *and the increment* compiled away, leaving an empty
> non-advancing loop in `JitStubs.TryResolveDefaultInterfaceMethod`
> (RIP verified pinned to a register self-loop with the gdbstub). A second
> fix: `FindDefaultConstructor` searched only assemblies < 16 while the cap
> is 128, so `new T()` on JITTest's own types never ran the ctor. Keep side
> effects out of conditional-trace call arguments.

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `Connection refused` | `sshd` not started in the guest, or wrong port (`sshd` prints `[sshd] listening on port 22`) |
| Password rejected | `PasswordAuthentication=yes` missing from `/etc/ssh/sshd_config` (re-run `ssh-cli-image.sh`) |
| Key rejected | public key not in `/home/user/.ssh/authorized_keys` (pass it to `ssh-cli-image.sh`), wrong key perms on the client |
| Connect times out | guest has no NIC/eth0, or QEMU missing `hostfwd=tcp::2222-:22` |
| `sftp`/`scp` fail at init | update the client (very old clients need `scp -O`), or a firewall middlebox |
| Transfer fails mid-file on a mounted exFAT disk | chunked access covers the boot volume only (see "Large files" above) |
| After rebuild: no users | re-run `build/ssh-cli-image.sh` |

## How it is implemented

- `src/ddk/Services/SshService.cs` — cooperative service (listener, 4
  connection slots, host key, config, lockout) driven by the kernel idle hook.
- `src/ddk/Services/Ssh/SshConnection.cs` — the per-connection state
  machine: version exchange, binary packet layer (aes-ctr + hmac + EtM),
  curve25519 kex, userauth, session channels, interactive line editor
  (left/right/Home/End/Delete/history), rekey, exec via the kernel shell
  bridge (`Kernel_ShellExec`).
- `src/ddk/Services/Ssh/SftpServer.cs` — the SFTP v3 subsystem over the
  session channel: realpath/stat/open/read/write/close/opendir/readdir/
  remove/mkdir/rmdir/rename. File data streams through chunked VFS access
  (`BootFiles.ReadRange`/`WriteRange`); oversized `WRITE` packets
  (OpenSSH >= 9 sends up to 256 KiB per request) bypass the receive
  buffer and stream to disk through a 32 KiB scratch buffer; metadata and
  directories use the VFS (`File`/`Directory`).
- `src/ddk/Kernel/BootFiles.cs` + `src/kernel/Exports/DDK/FileRangeExports.cs`
  — the chunked (offset-based) boot-volume file bridge that lifts the
  old 64 KiB transfer cap.
- `build/ssh-cli-image.sh`, `build/ssh-vm.sh` — image prep + launcher.
- `build/ssh-probe.sh` — the 21-check end-to-end suite used above.
