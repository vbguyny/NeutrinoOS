# ROLE

You are a senior systems engineer specializing in bare-metal TCP/IP stack
implementation, TLS and SSH server development on managed runtimes, and
hosting ASP.NET Core / Kestrel applications without a conventional
operating system. You are assisting in Phase 6 of a custom operating
system project.

# PROJECT CONTEXT

Project name: NeutrinoOS
Base project: ProtonOS (a managed OS written entirely in C# using bflat's
  zero-library mode, with a Tier-0 JIT compiler).

Phase 1 status: COMPLETE. Graphics/framebuffer/GOP removed. Serial console
  at COM1 (0x3F8, 115200 8N1).

Phase 2 status: COMPLETE. Production UART 16550 driver (`/dev/ttyS0`).
  Line discipline with canonical/raw modes, editing, Ctrl+C/D/U,
  32-entry history. Console Abstraction Layer (CAL) with `IConsoleDevice`
  and `ConsoleMultiplexer`. `korlib` implements `System.Console`,
  `System.IO.TextWriter`/`TextReader`, `System.ConsoleColor`,
  `System.ConsoleKey`, `System.Text.Encoding.UTF8`, `System.Environment`.

Phase 3 status: COMPLETE. VGA text-mode driver (`/dev/vga0`, 80x25/80x50,
  ANSI parser, CP437). PS/2 keyboard driver (IRQ1, scancode set 1, key
  repeat, modifier tracking). CAL routes output to both consoles and
  switches active input automatically.

Phase 4 status: COMPLETE. Tier-0 JIT validates .NET 10 assemblies
  (C# 14). `korlib` expanded with `System.IO`,
  `System.Collections.Generic`, `System.Linq`, `System.Threading`,
  `System.Threading.Tasks` (synchronous minimal), `System.Text`,
  `System`, `System.Net` (basic `Socket`, `TcpClient`, `TcpListener`,
  `Dns`, `HttpClient` GET/POST), `System.Globalization`,
  `System.Diagnostics`. Cross-assembly loading with
  `AssemblyLoadContext`. Standard .NET 10 console applications run on
  NeutrinoOS.

Phase 5 status: COMPLETE. Production shell with tokenizer, parser, pipes
  (`|`), redirection (`>`, `>>`, `<`, `2>`, `2>>`), background execution
  (`&`), sequential (`;`) and conditional (`&&`, `||`) execution.
  Built-in commands and external utilities (`.dll`, .NET 10) including
  `ls`, `cat`, `echo`, `mkdir`, `rm`, `cp`, `mv`, `wc`, `grep`, `ps`,
  `kill`, `sleep`, `df`, `mount`, `umount`, `uname`, `date`, `uptime`,
  `free`, `env`, `ifconfig`, `dhcp`, `ping`, `dns`, `netstat`, `wget`,
  `curl`, `ssh` (client), `gc`, `history`, `export`, `unset`. Shell
  initialization with `/etc/profile`, `PS1` customization, persistent
  history, basic tab completion.

Target of the overall project: A console-only, headless managed OS that
  runs .NET 10 console applications and utilities on bare metal, with
  TCP/IP networking, SSH, curl, and the ability to host .NET web apps.
  No GUI, no graphical framebuffer, no window manager.

# ENVIRONMENT

- Host OS: Windows 11 (x64)
- IDE: Visual Studio Code (latest stable) with the Remote - WSL extension
- Shell: PowerShell 7 on the host; bash inside WSL2 Ubuntu 24.04
- Toolchain (already installed in WSL2 from Phase 1):
  - .NET SDK 10.0 (with C# 14)
  - bflat (configured to target .NET 10)
  - clang / ld.lld (LLVM 17+)
  - GNU make
  - Python 3.11+
  - qemu-system-x86_64 with OVMF firmware
  - git
- Phase 5 boot verification used:
  `make run-qemu-vga` (boots to NeutrinoOS shell on serial and VGA).
  `tests/run-phase5-tests.ps1` verifies shell commands, pipes,
  redirection, background execution, and network utilities.
- Windows 11 host has VirtualBox 7.x installed for manual verification.
- Phase 6 introduces TLS/SSH and web hosting, which require cryptographic
  primitives. The design must avoid dependencies on OpenSSL or other
  native crypto libraries; all cryptography must be managed C#.

# PHASE 6 GOAL — "FULL NETWORKING, SSH SERVER, AND .NET WEB HOSTING"

Transform NeutrinoOS from a system that consumes network services
(`wget`, `curl`, `ssh` client from Phase 5) into a system that provides
them. Phase 6 delivers three integrated capabilities:

1. **A production-quality TCP/IP stack** with full socket semantics,
   connection management, and performance suitable for hosting services.
2. **An SSH server** (sshd) that accepts remote connections, authenticates
   users, and provides an interactive shell session over the encrypted
   channel — backed by the CAL and the Phase 5 shell.
3. **The ability to host .NET 10 web applications** (ASP.NET Core /
   Kestrel or a minimal HTTP server) on bare metal, serving HTTP/HTTPS
   over the network.

Phase 6 is complete when a user can SSH from a Windows 11 host into
NeutrinoOS, interact with the shell over the encrypted channel, and
browse to an HTTP endpoint served by a .NET 10 web application running
on NeutrinoOS.

Phase 6 does NOT include: a graphical browser, a full TLS certificate
management UI, multi-user account management with persistent home
directories (basic user authentication is in scope; full user management
is deferred), or a reverse proxy / load balancer. It is strictly about
providing network services and hosting .NET web apps.

# DETAILED TASKS

## Task 1 — TCP/IP stack hardening and socket API completion

The ProtonOS TCP/IP stack (Ethernet, ARP, IPv4, ICMP, UDP, TCP) is
inherited from earlier phases, but its socket API is incomplete. Harden
and complete it.

- **Socket layer**: Implement a POSIX-like socket API in `korlib`
  (`System.Net.Sockets`):
  - `Socket` class with `Bind`, `Listen`, `Accept`, `Connect`,
    `Send`, `Receive`, `Close`, `Shutdown`.
  - `SocketType.Stream` (TCP) and `SocketType.Dgram` (UDP).
  - `AddressFamily.InterNetwork` (IPv4). IPv6 is out of scope for
    Phase 6; document the limitation.
  - Non-blocking and blocking modes. Non-blocking mode must integrate
    with the kernel's scheduler (yield on `WouldBlock`, wake on
    readiness).
  - `SocketAsyncEventArgs` and `Task`-based async operations
    (`ConnectAsync`, `AcceptAsync`, `SendAsync`, `ReceiveAsync`).
    These must map to the Phase 4 synchronous `Task` implementation
    and document the limitation (they block the calling thread until
    the operation completes).
- **TCP state machine**: Verify the existing TCP implementation handles:
  - Three-way handshake (`SYN`, `SYN-ACK`, `ACK`).
  - Data transfer with sliding window and retransmission.
  - Graceful close (`FIN`, `FIN-ACK`, `ACK`) and `TIME_WAIT`.
  - Reset (`RST`) handling.
  - Simultaneous open and simultaneous close (best-effort).
  - Path MTU discovery (optional; document if deferred).
- **UDP**: Verify `SocketType.Dgram` supports `SendTo` and
  `ReceiveFrom` with correct address/port handling.
- **Connection backlog**: Implement a listen backlog for `Socket.Listen`
  so the SSH server and web server can accept multiple pending
  connections.
- **SO_REUSEADDR and SO_REUSEPORT**: Implement these socket options so
  servers can restart without `TIME_WAIT` blocking the bind.
- **Performance**: Add TCP segmentation offload (TSO) awareness if the
  VirtIO-Net driver supports it. If not, document the limitation.
- **VirtIO-Net and E1000 drivers**: Verify both drivers work correctly
  under QEMU and VirtualBox. The VirtIO-Net driver is preferred for
  performance; the E1000 driver is the fallback for VirtualBox's
  default NIC.
- Document the stack in `docs/PHASE6-TCPIP.md`, including the state
  machine diagrams for TCP, the socket API surface, and the known
  limitations.

## Task 2 — Cryptographic primitives (managed C#)

Phase 6 requires TLS (for HTTPS) and SSH (for the SSH server). Both
depend on cryptographic primitives. Implement them in managed C#,
with no native dependencies.

- **Hash functions**:
  - SHA-1 (required for SSH key exchange and HMAC-SHA1).
  - SHA-256, SHA-384, SHA-512 (required for TLS and SSH).
  - MD5 (required for legacy SSH compatibility; may be disabled by
    default).
- **HMAC**: HMAC-SHA1, HMAC-SHA256, HMAC-SHA384, HMAC-SHA512.
- **Symmetric ciphers**:
  - AES-128, AES-192, AES-256 in CTR and GCM modes.
  - ChaCha20-Poly1305 (required for modern SSH and TLS).
  - AES-CBC (for legacy compatibility; may be disabled by default).
- **Key exchange**:
  - Diffie-Hellman (finite-field and elliptic-curve).
  - ECDH with NIST P-256, P-384, P-521.
  - Curve25519 (required for modern SSH and TLS).
- **Public-key algorithms**:
  - RSA (signature verification and generation, with PKCS#1 v1.5
    and PSS padding).
  - ECDSA with P-256, P-384, P-521.
  - Ed25519 (required for modern SSH).
- **Random number generator**: A cryptographically secure RNG seeded
  from the kernel's entropy sources (RTC, HPET, interrupt timing,
  optionally VirtIO-RNG if available).
- **Implementation guidance**: Use the managed implementations from
  the .NET BCL where they are available and portable (e.g.,
  `System.Security.Cryptography.SHA256`). For algorithms not present
  in `korlib`, implement them in C#. Reference the Bouncy Castle
  C# port or the `NSec` library for algorithmic guidance, but do not
  introduce a dependency on either — implement the algorithms directly
  to avoid pulling in desktop-only BCL surface.
- Document the cryptographic inventory in `docs/PHASE6-CRYPTO.md`,
  including which algorithms are enabled by default, which are
  legacy, and which are disabled for security.

## Task 3 — TLS 1.2 / 1.3 implementation (for HTTPS)

Implement a minimal TLS stack in managed C# to support HTTPS for the
web hosting capability. TLS is a large undertaking; scope it carefully.

- **TLS 1.2**: Implement the handshake (ClientHello, ServerHello,
  Certificate, ServerKeyExchange, ServerHelloDone, ClientKeyExchange,
  ChangeCipherSpec, Finished), the record layer, and the cipher suites
  required for modern clients:
  - `TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256`
  - `TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384`
  - `TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256`
  - `TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384`
- **TLS 1.3**: Implement the handshake (ClientHello, ServerHello,
  EncryptedExtensions, Certificate, CertificateVerify, Finished),
  the record layer with HKDF, and the required cipher suites:
  - `TLS_AES_128_GCM_SHA256`
  - `TLS_AES_256_GCM_SHA384`
  - `TLS_CHACHA20_POLY1305_SHA256`
- **SNI**: Parse the Server Name Indication extension so the server can
  select a certificate based on the requested hostname.
- **Certificate handling**: Load X.509 certificates and private keys
  from PEM or DER files on the NeutrinoOS filesystem
  (`/etc/ssl/certs/`, `/etc/ssl/private/`). Implement X.509 parsing
  in managed C# (the BCL's `System.Security.Cryptography.X509Certificates`
  types may be ported or reimplemented as needed).
- **Known limitations**: TLS 1.3's 0-RTT mode, client certificates,
  and OCSP stapling are out of scope for Phase 6. Document them.
- Document the TLS implementation in `docs/PHASE6-TLS.md`.

## Task 4 — SSH server implementation

Implement an SSH server (sshd) that runs as a .NET 10 application on
NeutrinoOS, using the TCP/IP stack and cryptographic primitives from
Tasks 1–2.

- **Protocol**: SSH-2.0 (RFC 4251–4254 and related RFCs).
- **Transport layer**:
  - Key exchange: `curve25519-sha256`, `ecdh-sha2-nistp256`,
    `diffie-hellman-group14-sha256`.
  - Host key algorithms: `ssh-ed25519`, `rsa-sha2-256`,
    `rsa-sha2-512`.
  - Ciphers: `chacha20-poly1305@openssh.com`,
    `aes256-gcm@openssh.com`, `aes128-gcm@openssh.com`,
    `aes256-ctr`, `aes128-ctr`.
  - MACs: `hmac-sha2-256-etm@openssh.com`,
    `hmac-sha2-512-etm@openssh.com`,
    `hmac-sha2-256`, `hmac-sha2-512`.
  - Compression: `none` (zlib is out of scope; document the
    limitation).
- **Authentication layer**:
  - `password` (against the NeutrinoOS user database; see Task 5).
  - `publickey` (against `~/.ssh/authorized_keys`; see Task 5).
  - `keyboard-interactive` (optional; document if deferred).
- **Connection layer**:
  - `session` channel with `pty-req`, `shell`, `exec`, and
    `subsystem` requests.
  - `pty-req` must allocate a pseudo-terminal backed by the CAL
    (Phase 2/3), so the remote user sees the same interactive shell
    behavior as a local user.
  - `shell` must launch the Phase 5 shell with a PTY.
  - `exec` must run a single command and return its exit code.
  - `subsystem` must support `sftp` (optional; document if deferred).
  - `window-change` requests must resize the PTY (requires the CAL
    to support dynamic resizing; add this if not present).
- **Server configuration**: Read from `/etc/ssh/sshd_config` (a
  simplified INI-style subset of OpenSSH's format), supporting:
  - `Port` (default 22).
  - `ListenAddress` (default 0.0.0.0).
  - `HostKey` (path to host key files).
  - `PasswordAuthentication` (yes/no).
  - `PubkeyAuthentication` (yes/no).
  - `AuthorizedKeysFile` (default `~/.ssh/authorized_keys`).
- **Host keys**: Generate host keys on first boot if they do not exist,
  using the Phase 6 RNG and the key algorithms from Task 2. Store them
  in `/etc/ssh/ssh_host_ed25519_key` and `/etc/ssh/ssh_host_rsa_key`.
- **Implementation guidance**: Reference wolfSSH and CycloneSSH for
  protocol structure, but implement the server directly in C# to
  avoid C dependencies. The `SshNet` library is another reference,
  but it is a client library; do not depend on it.
- **Service model**: The SSH server runs as a background daemon process
  launched at boot (after the network is up) or on demand via
  `sshd` from the shell. Provide both modes.
- **Testing**: Verify that a Windows 11 host can `ssh` into NeutrinoOS
  using the built-in OpenSSH client (`ssh user@neutrinoos-ip`),
  authenticate with a password, and interact with the shell.
- Document the SSH server in `docs/PHASE6-SSH.md`.

## Task 5 — User management (minimal)

Phase 6 requires user authentication for SSH. Implement a minimal user
database.

- **User database**: `/etc/passwd` and `/etc/shadow` in a simplified
  format:
  - `/etc/passwd`: `username:uid:gid:home:shell`
  - `/etc/shadow`: `username:password_hash:...`
- **Password hashing**: Use a modern KDF. Argon2id or scrypt is
  preferred; bcrypt is acceptable. Implement the KDF in managed C#
  using the Phase 6 crypto primitives.
- **Default users**: Create a `root` user (uid 0) and a `user` user
  (uid 1000) on first boot. The `root` password is set via a kernel
  boot parameter (`root.password=...`) or a first-boot prompt on the
  serial console. The `user` password is similarly configurable.
- **Home directories**: Create `/home/root` and `/home/user` with
  appropriate permissions. `~/.ssh/authorized_keys` is read from
  the user's home directory.
- **PAM**: Full PAM is out of scope. A minimal authentication API
  (`Authenticate(username, password)` and
  `AuthenticatePublicKey(username, key)`) is sufficient.
- Document the user model in `docs/PHASE6-USERS.md`, including how to
  add users, change passwords, and manage `authorized_keys`.

## Task 6 — .NET 10 web hosting (ASP.NET Core / Kestrel)

Implement the ability to host .NET 10 web applications on NeutrinoOS.

- **Kestrel transport**: Kestrel requires a socket transport
  (`Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets`). Port
  the Kestrel transport layer to use the NeutrinoOS `System.Net.Sockets`
  API from Task 1. The `SocketsHttpHandler` in .NET 10 is 100% managed
  and based on the .NET Socket API, which is a favorable starting
  point for the port.
- **Kestrel server**: Port the Kestrel server core
  (`Microsoft.AspNetCore.Server.Kestrel.Core`) to NeutrinoOS. This
  includes the HTTP/1.1 parser, the request pipeline, and the response
  writer. HTTP/2 and HTTP/3 (QUIC) are out of scope for Phase 6;
  document the limitation.
- **Minimal API hosting**: Provide a template
  (`templates/NeutrinoWebApp`) that creates a minimal ASP.NET Core
  app using `WebApplication.CreateBuilder()` and `app.MapGet(...)`.
  The template must target `net10.0` and reference only the Kestrel
  packages that are portable to NeutrinoOS.
- **HTTPS support**: Integrate the TLS implementation from Task 3
  with Kestrel so the web app can serve HTTPS. Configure the
  certificate path via `appsettings.json` or environment variables.
- **Static file serving**: `UseStaticFiles()` must work, reading files
  from the VFS (`/var/www/` by default).
- **Deployment**: The web app is deployed as one or more `.dll` files
  in `/apps/webapp/`. The shell's `run` command (or a new `webhost`
  command) launches the app. The web app runs as a background process
  and logs to the console (which the CAL routes to the serial/VGA
  console and optionally to a log file).
- **Testing**: Verify that a Windows 11 host can browse to
  `http://neutrinoos-ip:5000/` (or `https://neutrinoos-ip:5001/`)
  and see the response from the .NET 10 web app running on NeutrinoOS.
- **Alternative if Kestrel port is too large**: If porting Kestrel in
  Phase 6 is infeasible, implement a minimal HTTP/1.1 server in C#
  that uses the NeutrinoOS socket API and can serve both static files
  and a minimal request-handler API. Document this as a fallback in
  `docs/PHASE6-WEB.md`, and note that full Kestrel support is a
  Phase 7+ goal.
- Document the web hosting architecture in `docs/PHASE6-WEB.md`.

## Task 7 — Kernel and boot integration

- **Boot sequence**: The network stack initializes after the storage
  and console drivers. The SSH server and web server do not start
  automatically at boot in Phase 6; they are launched from the shell
  (`sshd &` and `webhost /apps/webapp/webapp.dll &`) or via
  `/etc/rc.local` (a new startup script mechanism). Document the
  recommended startup sequence.
- **Entropy at boot**: The RNG must be seeded before any TLS or SSH
  operation. Collect entropy from the RTC, HPET, interrupt timing,
  and (if available) VirtIO-RNG. Add a `/dev/random` character device
  backed by the RNG.
- **Firewall**: Implement a minimal packet filter (allow/deny by port
  and source IP) so the SSH server and web server can be protected.
  Configuration via `/etc/firewall.conf`. A full stateful firewall is
  out of scope; a simple per-port allow/deny list is sufficient.
- **Boot parameters**: Add:
  - `net.ip=dhcp|static` (default `dhcp`).
  - `net.static.ip=...`, `net.static.gateway=...`, `net.static.dns=...`
    (for static configuration).
  - `sshd.autostart=yes|no` (default `no`).
  - `webhost.autostart=yes|no` (default `no`).

## Task 8 — Testing and documentation

- **Test suite**: Create `tests/run-phase6-tests.ps1` (PowerShell for
  Windows 11) that:
  - Boots NeutrinoOS in QEMU with VirtIO-Net and a user-mode network
    (or a tap interface) so the guest is reachable from the host.
  - Verifies that `ifconfig` shows a DHCP-assigned IP.
  - Verifies that `ping` from the guest to the host works.
  - Launches `sshd &` on the guest and verifies that the host can
    `ssh` into the guest using the Windows OpenSSH client.
  - Runs a scripted SSH session that executes `ls`, `cat`, and `gc`
    and verifies the output.
  - Launches a minimal .NET 10 web app on the guest
    (`webhost /apps/webapp/webapp.dll &`) and verifies that the host
    can `curl http://guest-ip:5000/` and receive the expected response.
  - Optionally verifies HTTPS with a self-signed certificate.
- **Sample SSH session script**: Provide `scripts/phase6-ssh-demo.ps1`
  that automates the SSH login and command execution from Windows 11.
- **Sample web app**: Provide `templates/NeutrinoWebApp` with a
  `Program.cs` that maps a few endpoints (`/`, `/health`, `/time`)
  and a `webapp.csproj` targeting `net10.0`.
- **Documentation**:
  - `docs/PHASE6-TCPIP.md` — TCP/IP stack, socket API, state machines.
  - `docs/PHASE6-CRYPTO.md` — cryptographic primitives and inventory.
  - `docs/PHASE6-TLS.md` — TLS 1.2/1.3 implementation.
  - `docs/PHASE6-SSH.md` — SSH server architecture, configuration,
    host key generation, testing.
  - `docs/PHASE6-USERS.md` — user database, password hashing,
    authorized_keys.
  - `docs/PHASE6-WEB.md` — Kestrel port (or fallback HTTP server),
    web app deployment, HTTPS configuration.
  - `docs/PHASE6-ACCEPTANCE.md` — step-by-step verification for every
    acceptance criterion below from a fresh Windows 11 machine.
  - `PHASE6-REPORT.md` — summary of changes, blockers, deviations.

# CONSTRAINTS

- All code must be C# (plus the existing assembly intrinsics). Do NOT
  add C or C++ files to the kernel, bootloader, drivers, `korlib`,
  shell, utilities, SSH server, or web server.
- Do NOT introduce a graphical framebuffer, GUI, mouse support, or a
  window manager. This phase is strictly console-only.
- Do NOT depend on OpenSSL, libssh, libcurl, or any other native
  library. All cryptography, TLS, and SSH must be implemented in
  managed C#.
- Do NOT implement IPv6, HTTP/2, or HTTP/3 (QUIC). Document these as
  deferred.
- Do NOT implement a full stateful firewall. A simple per-port
  allow/deny list is sufficient.
- Do NOT implement full PAM. A minimal authentication API is
  sufficient.
- Do NOT use the term "TTY" as a project name or suffix. It is fine
  to use the Unix term "tty" in device paths (`/dev/ttyS0`) and
  documentation.
- Do NOT rename the project; it is NeutrinoOS.
- Preserve the AGPL-3.0 license and attribution to ProtonOS.
- Do NOT scope-creep into Phase 7+ (e.g., reverse proxy, load
  balancer, multi-user web hosting, full certificate management UI).
  If a Phase 7+ concern arises, note it in the "Deferred to later
  phases" section.
- Every public type and method added to `korlib`, the SSH server, or
  the web server must have XML doc comments describing its Phase 6
  semantics.
- All user-visible strings must say "NeutrinoOS".

# DELIVERABLES

1. A hardened TCP/IP stack with a complete POSIX-like socket API
   (`Socket`, `TcpClient`, `TcpListener`, `SocketAsyncEventArgs`,
   async operations) in `korlib`.
2. Managed C# implementations of the cryptographic primitives required
   for TLS and SSH (hash functions, HMAC, AES-GCM, ChaCha20-Poly1305,
   ECDH, Curve25519, RSA, ECDSA, Ed25519, CSPRNG).
3. A TLS 1.2/1.3 implementation in managed C# supporting the required
   cipher suites, SNI, and X.509 certificate loading.
4. An SSH server (sshd) in C# supporting SSH-2.0, the required key
   exchange, host key, cipher, and MAC algorithms, password and
   publickey authentication, PTY-backed shell sessions, and
   `exec`/`subsystem` requests.
5. A minimal user database (`/etc/passwd`, `/etc/shadow`, home
   directories, `authorized_keys`) with modern password hashing.
6. .NET 10 web hosting via a ported Kestrel (or a fallback minimal
   HTTP/1.1 server) with HTTP and HTTPS support, a
   `templates/NeutrinoWebApp` template, and static file serving from
   `/var/www/`.
7. Kernel boot parameters for network configuration and service
   autostart; `/dev/random`; a minimal packet filter.
8. `tests/run-phase6-tests.ps1`, `scripts/phase6-ssh-demo.ps1`,
   `templates/NeutrinoWebApp`.
9. `docs/PHASE6-TCPIP.md`, `docs/PHASE6-CRYPTO.md`,
   `docs/PHASE6-TLS.md`, `docs/PHASE6-SSH.md`,
   `docs/PHASE6-USERS.md`, `docs/PHASE6-WEB.md`,
   `docs/PHASE6-ACCEPTANCE.md`, `PHASE6-REPORT.md`.

# ACCEPTANCE CRITERIA

Phase 6 is complete when ALL of the following are true:

- [ ] `make image` and `make run-qemu-vga` still boot to a NeutrinoOS
      banner on both consoles, with no regressions from Phase 5.
- [ ] `ifconfig` shows a DHCP-assigned IP address for `eth0`
      (or a static IP if configured via boot parameter).
- [ ] `ping` from the guest to the Windows 11 host succeeds.
- [ ] `sshd &` launches the SSH server on port 22, and the host can
      `ssh user@neutrinoos-ip` using the Windows OpenSSH client.
- [ ] Password authentication succeeds against the user database.
- [ ] Public-key authentication succeeds against
      `~/.ssh/authorized_keys`.
- [ ] The SSH session presents an interactive shell with the same
      line editing, history, and tab completion as the local serial
      console.
- [ ] `exec` over SSH runs a single command (`ssh user@host ls /`)
      and returns the correct exit code.
- [ ] `webhost /apps/webapp/webapp.dll &` launches a .NET 10 web
      app, and the host can `curl http://neutrinoos-ip:5000/` and
      receive the expected response.
- [ ] HTTPS works with a self-signed certificate
      (`curl -k https://neutrinoos-ip:5001/`).
- [ ] Static files served from `/var/www/` are reachable via HTTP.
- [ ] `Host key generation` on first boot produces
      `/etc/ssh/ssh_host_ed25519_key` and
      `/etc/ssh/ssh_host_rsa_key`.
- [ ] The RNG is seeded from multiple entropy sources, and
      `/dev/random` returns cryptographically secure random bytes.
- [ ] The minimal packet filter can allow/deny by port and source IP.
- [ ] No C or C++ files exist in the kernel, bootloader, driver,
      `korlib`, shell, utility, SSH server, or web server
      directories.
- [ ] `docs/BUILD-WINDOWS.md` (from Phase 1) and
      `docs/PHASE2-ACCEPTANCE.md` through
      `docs/PHASE5-ACCEPTANCE.md` still work, and
      `docs/PHASE6-ACCEPTANCE.md` provides step-by-step verification
      for every checklist item above from a fresh Windows 11 machine.

# OUTPUT FORMAT

Respond in the following order:

1. **Plan** — a numbered list of concrete steps mapped to the eight
   tasks above.
2. **Repository layout** — the target directory tree after Phase 6,
   highlighting new and modified files.
3. **Code changes** — for each file to be created, modified, or
   deleted:
   - Full path
   - Action (create / modify / delete)
   - The complete new file contents (for created files) OR a unified
     diff (for modifications) OR a precise description (for
     deletions).
   - For large files (e.g., the TLS implementation, the SSH server,
     the Kestrel port), provide the complete source; do not abbreviate
     with "..." unless the omitted region is boilerplate that is
     explicitly described.
4. **TCP/IP socket API surface** — a table listing each socket type
   and method, its blocking/non-blocking behavior, and its async
   mapping.
5. **Cryptographic inventory** — a table listing each algorithm,
   whether it is enabled by default, and its use (TLS, SSH, both).
6. **SSH server algorithm support** — a table listing each SSH
   algorithm category and the supported algorithms.
7. **Build and test commands** — exact WSL2 bash commands and
   PowerShell commands for Windows 11 to build, run, and verify
   Phase 6.
8. **Acceptance checklist** — reproduce the checklist above, with a
   one-line note for each item explaining how it is satisfied.
9. **Deferred to later phases** — anything that came up that belongs
   to Phase 7+ (reverse proxy, load balancer, multi-user web
   hosting, full certificate management UI, IPv6, HTTP/2, HTTP/3,
   full stateful firewall, full PAM, SFTP subsystem, compression).
10. **Open questions / assumptions** — anything ambiguous about the
    Phase 5 output, the existing TCP/IP stack, the existing `korlib`
    structure, the ProtonOS conventions, or the .NET 10 networking
    APIs that you assumed, and how the user can verify or correct
    them.

If any part of the Phase 5 output is unclear, or if the existing
TCP/IP stack or `korlib` layout does not match your assumptions,
state your assumptions explicitly and proceed with a reasonable
layout consistent with a bflat-based managed kernel, noting where
the user must adjust paths.

Do not skip ahead to Phase 7–8. Scope discipline is mandatory:
Phase 6 only.