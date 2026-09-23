# PHASE6-TCPIP.md — TCP/IP stack, socket API, and the packet filter

Phase 6 built on the existing ProtonOS network stack (virtio-net driver,
Ethernet/ARP/IPv4/ICMP/UDP/TCP, DHCP/DNS clients) and hardened it for
server workloads.

## Socket API (DDK, `ProtonOS.DDK.Network`)

| Type | Purpose |
|------|---------|
| `NetworkManager` | interface registry (`eth0`…), DHCP + static configuration |
| `NetworkStack` | per-interface stack state (IP/mask/gateway/DNS) |
| `NetworkPump` | moves frames in/out (`Pump`, `FlushTx`) — servers must call it every tick |
| `TcpServer` / `TcpListener` | bind + backlog + `Pending()`/`Accept()` |
| `TcpSocket` | `Send(byte*,int)`, `Receive(byte*,int)`, `Close()` |
| `Firewall` | inbound TCP filter from `/etc/firewall.conf` (Phase 6) |

Server pattern used by both daemons (sshd, webhost):

```
Tick():  NetworkPump.Pump(stack, 4)
         while (listener.Pending()) accept into a fixed slot array
         for each connection: conn.Tick()   // bounded, non-blocking
```

There are no kernel threads: services are **cooperative**, driven from
the shell idle hook (`ShellMain.IdlePump -> ServiceRegistry.Tick`) on
the boot thread. A remote session therefore stays alive while the local
console is idle; typing at the console also keeps the stack pumping.

## Socket API completion / hardening (Task 1)

- `TcpListener.Start(reuseAddress)` — SO_REUSEADDR so a restarted
  service can rebind immediately.
- `TcpListener.AcceptSocket(timeoutMs)` — blocking-accept helper that
  pumps the stack while waiting (used by client utilities).
- Partial-send handling: all Phase 6 servers loop `Send` until the
  record is fully queued; `Receive` returning ≤ 0 means "nothing now"
  and never blocks.
- Pointer-based `Send`/`Receive` overloads are used everywhere because
  the guest Tier‑0 JIT cannot resolve `byte[] -> ReadOnlySpan<byte>`
  implicit conversions inside service graphs.

## Firewall (Task 7)

`/etc/firewall.conf`, evaluated on every inbound SYN in
`TcpListener.HandleIncomingSyn` — denied sources never complete the
handshake:

```
# first matching rule decides; no rules = allow all
deny ip 10.0.2.99      # block a source address
deny tcp 443           # block a port
allow ip 10.0.2.2      # explicit allow
allow all | deny all   # catch-all
```

Verified in `build/p6-firewall-test.sh`: with `deny tcp 443`,
`curl http://127.0.0.1:8080/health` → 200 while
`curl https://127.0.0.1:8444/health` is refused, and the guest logs
`[TcpListener] SYN from 10.0.2.2 denied by /etc/firewall.conf`.

## Boot parameters and startup scripts (Task 7)

`/etc/boot.params` (key=value per line) is applied by the kernel shell
init (`ShellInit.ApplyBootParameters`) after the profiles and before
the first prompt:

| Key | Meaning |
|-----|---------|
| `net.ip=dhcp` | run the DHCP client on eth0 at boot |
| `net.ip=static` + `net.static.ip/mask/gateway/dns` | apply `ifconfig eth0 static …` |
| `sshd.autostart=yes` | start the SSH server (default `no`) |
| `webhost.autostart=yes` | start the web host (default `no`) |

`/etc/rc.local` is sourced after boot parameters (comment lines with
`#`), exactly like a Linux start script; both are verified by
`build/p6-web-test.sh` (boot with no console input → dhcp + webhost
come up, an `uname` line from rc.local appears in the boot log).

## Entropy (Task 7)

`Kernel_GetEntropy` (xorshift64* over TSC/HPET/RTC and interrupt
timing) seeds `ProtonOS.DDK.Crypto.Csprng` before any TLS/SSH use.
SSH host keys and the TLS certificate are generated on first boot from
this RNG. A dedicated `/dev/random` character device is not provided
(services read `Csprng` directly); this is documented as a Phase 7
item.

## Limitations

- No IPv6, no TCP options beyond MSS basics, single-connection-per-slot
  server model, RX window is polling-based (no interrupt coalescing).
- The firewall is a per-port/per-source SYN filter — not stateful and
  not a full packet filter.
