# Phase 9 Task 2 — IPv6, DHCPv6, DNS

Status: **implemented and validated** (QEMU slirp acceptance 8/8, see below).

## Scope

Dual-stack networking for NeutrinoOS:

| Area | Deliverable |
|------|-------------|
| IPv6 core | `src/ddk/Network/Stack/Ipv6.cs` — `Ipv6Address`, packet parse/build, extension-header walking, checksums (pseudo-header based, pointer-arg API) |
| ICMPv6 / NDP | `src/ddk/Network/Stack/Icmpv6.cs` — echo, RS/RA, NS/NA, checksums, message builders |
| Stack integration | `src/ddk/Network/Stack/NetworkStack.Ipv6.cs` (receive/dispatch, NDP cache, SLAAC adoption, UDPv6, ping6 support) + `NetworkStack.Ipv6.Tcp.cs` (TCPv6 listener/connect) |
| DHCPv6 | `src/ddk/Network/Stack/Dhcp6.cs` — DUID-LL client: stateful SOLICIT/REQUEST **and** stateless INFORMATION-REQUEST (RFC 3736) |
| DNS | `DNS.cs` (AAAA query/parse) + `DnsResolver.ResolveV6` with DNSv6-first, IPv4 fallback |
| Utilities | `ping6`, `dns6`, `dhcp6`; `ifconfig` (`inet6`, `up` brings v6 up, `static6`), `netstat` (`-s` IPv6 section) |
| Frame pump | `NetworkPump`: IPv6 loopback, `ResolveNdp`, `BringUpV6`, real-time `ResolveArp` |

## Behavior

- **Bring-up**: `ifconfig eth0 up` sends Router Solicitations and adopts the first
  autonomous /64 prefix as a SLAAC address (EUI-64 IID from the MAC) plus the
  default router and any RDNSS server.
- **DHCPv6**: `dhcp6` first tries a stateful exchange, then falls back to a
  stateless INFORMATION-REQUEST (configuration only). DNSv6 servers learned via
  DHCPv6 are adopted with `SetV6Dns` (also used by RDNSS).
- **Ping**: `ping6 -c N host` — `::1` is served by the stack's IPv6 loopback
  (build → parse → checksum → reply, end to end); other targets run NDP
  (solicitation/registration) before echo requests and pump until replies match.
- **DNS**: `dns6 host` resolves AAAA through the DNSv6 server when one is known
  (Happy-Eyeballs style fallback to the IPv4 server otherwise).
- **Netstat** shows IPv6 addresses/neighbours plus ICMPv6/NDP/UDP6/TCP6 counters.

## Acceptance (QEMU slirp, real virtio-net NIC)

Script: `build/p9-ipv6-acc3.sh` (WSL copy at `/root/p9-ipv6-acc3.sh`).
Latest run: **PASS=8 FAIL=0**, zero JIT faults.

| Check | Evidence |
|-------|----------|
| SLAAC adopted | `[NetStack] IPv6 SLAAC address: fec0::5054:ff:fe12:3456` |
| Default router | `[NetStack] IPv6 router: fe80::2` |
| DHCPv6 stateless reply | `[DHCP6] stateless REPLY received` → `server fec0::2, dns6 fec0::3` |
| DNSv6 learned | `ifconfig`/`netstat` show `dns6 fec0::3` |
| Loopback ping | `ping6 -c 2 ::1` → `2 packets transmitted, 2 packets received` |
| Network ping (link-local) | `ping6 -c 3 fe80::2` → `3/3`, `64 bytes from [fe80::2] ... time=0-2 ms` |
| Network ping (global) | `ping6 -c 2 fec0::2` → `2/2`, `64 bytes from [fec0::2]` |
| DNSv6 wire path | pcap shows `fec0::5054...:53001 -> fec0::3:53` AAAA query (NDP-resolved first) |

`ping6 fe80::2`/`fec0::2` are genuine network exchanges: NDP resolution against
slirp followed by ICMPv6 echo request/reply over the virtio-net device,
verified frame-by-frame in the capture.

## QEMU slirp findings (ground truth, libslirp 4.7 / QEMU 8.2.2)

These were established from source (`libslirp` `dhcpv6.c`, `ip6_icmp.c`,
`slirp.c`) plus `-object filter-dump` captures:

1. **NDP messages must use hop limit 255.** slirp (like real routers)
   silently drops RS/NS with any other hop limit. This sent the initial
   SLAAC bring-up into the void; fixing `BuildIpv6Frame` to take a hop-limit
   parameter (255 for RS/NS/NA) unlocked real RA/SLAAC on QEMU.
2. **slirp answers RS with a full RA** (`fec0::/64`, router `fe80::2`,
   autonomous flag set) — but only when the RS is valid (HL=255, code 0).
3. **DHCPv6 is stateless-only.** `dhcpv6.c` implements INFORMATION-REQUEST
   exclusively; any message carrying an IA option is discarded per RFC 3315.
   The `dhcp6` utility's stateful attempt therefore always times out on slirp;
   the stateless fallback returns the DNS server (`vnameserver_addr6`, option 23).
4. **DHCPv6 replies come from an ephemeral source port** (observed 8962, not
   547). Clients must match replies by destination port; `ReceiveUdp6To` was
   added for this (optionally matched by source port before).
5. **No RDNSS and no DNS-over-IPv6 answers unless the host has an IPv6
   nameserver** (WSL here does not). The v6 DNS path is wire-verified (query
   emitted to `fec0::3:53` after NDP resolution); AAAA resolution itself cannot
   complete in this environment. The dual-stack fallback covers real networks.
6. **IPv4 ARP is not answered in this environment either** (even for
   10.0.2.2/10.0.2.3, verified on the wire). IPv4-over-slirp should be
   considered unvalidated; IPv6 has no such problem.
7. slirp re-queues outbound packets while it resolves the guest address with
   NS (1 s window). Answering the NS with a correct NA unblocks delivery —
   which requires the guest's solicited-node multicast join to be exact
   (see bug list below).

## Bugs found & fixed during bring-up (worth remembering)

- **Solicited-node multicast address was mis-computed** (`0x000001FF00000000`
  instead of `0x00000001FF000000`), so NS addressed to the guest's
  solicited-node group were dropped by the MAC/group filter and slirp never
  learned the guest's addresses. Everything else (echo, DHCPv6 replies) then
  starved. Fixed in `Ipv6Address.SolicitedNode()`.
- **Multicast source selection**: link-scope multicast (ff02::/16) now uses the
  link-local source address (RFC 4291; DHCPv6 RFC 8415), which also lets
  slirp address replies without an NDP round-trip.
- **`ResolveArp` used instant poll counts** — far too fast for an emulated
  NIC. Converted to a real-time budget with 500 ms retransmits (mirrors
  `ResolveNdp`/`BringUpV6`).

## Tier-0 JIT hazards encountered (documented for future DDK work)

The IPv6 code hit several Tier-0 JIT miscompilations; all are avoided in the
shipped code, and the patterns below should be treated as project rules:

1. Copying a 16-byte struct out of a **field of a local/out struct** into
   another local (`Ipv6Address p = ra.Prefix;`) compiles a mis-sized store
   (JIT logs `[stloc16] ... MISMATCH!`). Pass `field.Hi/Lo` primitives or use
   `&local.Field` instead.
2. Calling `.ToString()` **directly on a 16-byte field of a large out-struct**
   (`lease.Dns.ToString()`) turns into a method-table load through the field
   *value* (#GP, `rax = fec0::…`). Stage through primitive stores into a fresh
   local first.
3. Frames mixing many live object references with 16-byte struct locals can
   alias slots (observed: an `eth.Stack` reference slot replaced by an
   `Ipv6Address` value). Keep methods small; pass big structs by pointer;
   split reporting/adoption into tiny helpers.
4. By-value 16-byte struct **arguments** across JIT boundaries remain forbidden
   (use `Ipv6Address*`). Struct returns by value are fine (hidden buffer).

Fault triage helpers (used to find the above): kernel `!!! RAWV` register dump +
`[j|token at addr` method dump; `build/p9-faultmap.sh` maps a faulting RIP to
its JIT method, `tools/mdlook` resolves tokens to names.

## Deferred / validated elsewhere

- Stateful DHCPv6 (SOLICIT/REQUEST) — code complete, cannot be validated
  against slirp (by design); validate on VirtualBox NAT/real hardware.
- AAAA resolution via DNSv6 — needs an environment whose DNS has an IPv6
  listening path (host IPv6 nameserver or VBox NAT).
- TCPv6 beyond loopback exercise — listener/connect paths are dual-stack;
  end-to-end coverage continues with the HTTP tasks of Phase 9.
