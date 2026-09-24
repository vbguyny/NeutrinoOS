# NeutrinoOS v1.0.0 — User Manual

NeutrinoOS is a console-only (headless-capable) operating system that
runs .NET 10 console applications on bare metal. You interact with it
through one of its consoles:

- **Serial console** (COM1, 115200 8N1) — the primary interface; every
  release VM/appliance logs here,
- **VGA text console** (80x25/80x50) — when a display is attached; the
  boot messages mirror there and the active input switches automatically.

## 1. Getting in

- **VirtualBox/OVA**: start the VM; you land at the shell prompt after
  the boot timeline (≈ 3-7 s on the release image).
- **Serial**: connect at 115200 8N1 (`putty -serial COM1 -sercfg 115200,8,n,1`).
- **SSH**: after `dhcp` + `sshd` on the guest:

  ```
  ssh -p 2222 user@127.0.0.1     (with the VM port-forward, or adapt)
  ```

  Public-key auth is the default; see `docs/PHASE7-INSTALL-WINDOWS.md`
  to provision keys or enable password auth explicitly. Five failed
  logins lock your IP out for five minutes (configurable; events land in
  `/var/log/auth.log`).

## 2. The shell

Familiar tokens: pipes `|`, redirects `> >> < 2> 2>>`, sequencing
`; && ||`, background `&`, history (`history`, ↑/↓), editing (Ctrl+A/E/U/K/W),
tab completion, Ctrl+C (interrupt), Ctrl+D (EOF).

Built-ins: `cd pwd exit export unset env set help history jobs fg bg kill`
plus configuration via `set key=value` (see §5).

Utilities live in `/bin` (37+ of them), including:

```
ls cat echo mkdir rm cp mv wc grep ps kill sleep df mount umount
uname date uptime free ifconfig dhcp ping dns netstat wget curl ssh
gc jitstats gcstats boottime perf netstat -s sshd webhost passwd useradd
```

Examples:

```
neutrinoos> uname -a
neutrinoos> dhcp
neutrinoos> ifconfig
neutrinoos> ping 10.0.2.2
neutrinoos> wget http://example.com/      # or: curl -k https://...
neutrinoos> sshd                          # start the SSH server
neutrinoos> webhost                       # start the web host
neutrinoos> gcstats                       # GC health
neutrinoos> jitstats                      # JIT compiler stats
neutrinoos> boottime                      # boot timeline
```

## 3. Storage

- The boot volume is a FAT32 filesystem (label NEUTRINOOS) at `/`.
  `/bin`, `/etc`, `/home`, `/var`, `/apps` hold the usual things.
- A second disk (if attached, e.g. `test.img` in the dev flow) can be
  mounted with `mount` (FAT32/AHCI and EXT2 support are built in).
- `df` shows mounted volumes; `mount` with no arguments lists them.

## 4. Networking quick start

```
neutrinoos> dhcp                 # obtain a lease (QEMU/VBox NAT)
neutrinoos> ifconfig             # show addresses
neutrinoos> ping 10.0.2.2        # gateway in QEMU user-net
neutrinoos> dns example.com      # resolve
neutrinoos> curl -k https://10.0.2.2/...   # TLS 1.3 client
```

A minimal packet filter exists (`/etc/firewall.conf`); it is **allow-all
by default**. `netstat` lists sockets; `netstat -s` shows stack counters.

## 5. Configuration

Boot parameters and shell defaults live in key=value config files read at
boot (see `docs/PHASE6-ACCEPTANCE.md` for the reference list), e.g.:

```
net.dhcp=on            net.static.ip=...       net.gateway=...
sshd.autostart=yes     webhost.autostart=yes
```

Service configuration:

- `/etc/ssh/sshd_config` — `Port`, `PasswordAuthentication` (default
  `no`), `BanThreshold`, `BanSeconds`, `MaxAuthAttempts`.
- `/etc/webhost.conf` — `Port`, `HttpsPort`.
- `/etc/passwd`, `/etc/shadow` — accounts (`passwd`, `useradd`).

## 6. Security features you will notice

- **/var/log/auth.log** — SSH connections, logins, failures, bans;
  password/account changes. Grows unbounded (truncate manually:
  `echo -n "" > /var/log/auth.log` via a tool or delete the file).
- **Lockout** — repeated bad logins from one IP get refused at connect
  time for `BanSeconds`.
- **429** — hammering the web host returns `HTTP 429 Too Many Requests`.
- **ASLR** — stack/heap addresses differ per boot/process (visible in
  some debug logs; nothing user-visible depends on it).

## 7. Version and identity

```
neutrinoos> cat /etc/neutrinoos-release
NAME="NeutrinoOS"  VERSION="1.0.0"  ...
neutrinoos> uname -a              # NeutrinoOS 1.0.0 x86_64
```

## 8. Troubleshooting

| Symptom | Fix |
|---------|-----|
| No prompt after boot | Watch the boot timeline; ensure EFI firmware (no BIOS boot) |
| `dhcp` fails | NIC attached? (virtio-net/E1000); try again; check `ifconfig` |
| SSH `Permission denied (publickey)` | Password auth is off by default — provision `authorized_keys` |
| SSH refused entirely | Your IP may be banned — wait `BanSeconds` or reboot |
| Web `429` | Rate limit; wait a second and retry |
| Shell seems frozen | Ctrl+C; background jobs with `jobs`; the kernel runs services cooperatively between commands |

## 9. Getting help

- `help` in the shell (command list + usage).
- Developer/build docs: `docs/DEVELOPER-GUIDE.md`, `docs/ARCHITECTURE.md`.
- Release process: `docs/PHASE7-RELEASE.md`.
