# Phase 9 Task 3: HTTP/2 and HTTP/3

## Summary

The web host now speaks **HTTP/1.1, HTTP/2 (h2c + TLS/ALPN) and HTTP/3
(QUIC v1)** with real-world clients:

| Protocol | Path | Verified with |
|----------|------|---------------|
| HTTP/1.1 | TCP 80 | host `curl` (tap link) |
| HTTP/2 h2c prior knowledge | TCP 80 | host `curl --http2-prior-knowledge`, in-guest `h2test` |
| HTTP/2 h2c Upgrade (101) | TCP 80 | host `curl --http2`, in-guest `h2test` |
| HTTP/2 over TLS (ALPN h2) | TCP 443 | host `curl --http2 https://` |
| HTTP/1.1 over TLS | TCP 443 | host `curl --http1.1 https://` |
| **HTTP/3 over QUIC v1** | **UDP 443** | **`aioquic` 1.3.0 HTTP/3 client** |

All requests return the shared route table from `WebService.BuildRoute`
(`/`, `/health`, `/time`, ...), so the three front ends stay in sync.

## Components

### HPACK (`src/ddk/Services/Hpack.cs`, `HpackTables.cs`)
- Generated tables from the reference `hpack` Python module
  (`build/gen-hpack-tables.py`): Huffman codes, static table.
- Encoder: static exact match -> indexed; name match -> incremental
  indexing; otherwise literal with new name.
- Decoder: complete integer + string codec, dynamic table, and a
  prefix-tree Huffman decoder with strict tail-bit validation (≤7 pad
  bits, all ones).

### HTTP/2 (`src/ddk/Services/Http2.cs`)
- Connection preface, SETTINGS/PING/ACK, HEADERS + CONTINUATION
  (padding/priority stripped), DATA, WINDOW_UPDATE, RST_STREAM,
  GOAWAY; per-stream and connection flow control.
- Server push: when the client enables it, `GET /` also pushes
  `/health` on stream 2.
- Two entry modes, both server-side:
  - **Prior knowledge**: first bytes `PRI` on a plain connection.
  - **Upgrade**: `Upgrade: h2c` + `HTTP2-Settings` -> `101 Switching
    Protocols`, the original request becomes stream 1.

### TLS ALPN (`src/ddk/Tls/Tls13.cs`)
- ClientHello ALPN extension parsed; preference `h2` > `http/1.1`;
  selection echoed in EncryptedExtensions (RFC 7301).
- `WebConnection` starts the HTTP/2 engine on ALPN `h2`; the client
  still sends the connection preface (RFC 9113 3.4).

### QUIC v1 (`src/ddk/Services/Quic.cs`, `QuicTls.cs`)
- Long/short headers, packet number spaces (Initial, Handshake, 1-RTT),
  AES-128-GCM packet protection + AES-ECB header protection,
  Initial keys from the client's DCID and the v1 salt.
- Server-side TLS 1.3 handshake carried in CRYPTO frames
  (`QuicTlsServer`): ClientHello parse, X25519, key schedule, ALPN
  `h3`, transport parameters, CertificateVerify (Ed25519), Finished.
  The TLS transcript feeds `tls13` expand-labels for QUIC keys
  (`quic key`/`quic iv`/`quic hp`).
- Frames: PADDING, PING, ACK, CRYPTO, STREAM, MAX_DATA,
  MAX_STREAM_DATA, PATH_CHALLENGE/RESPONSE, CONNECTION_CLOSE,
  HANDSHAKE_DONE. Server Initial flight padded for the 1200-byte
  minimum.

### QPACK (`src/ddk/Services/Qpack.cs`, `QpackTables.cs`)
- Static-table-only codec (capacity 0 is advertised in SETTINGS).
  Request decoding handles indexed, literal-with-name-reference and
  literal-with-literal-name field lines; Huffman values reuse the
  HPACK/RFC 7541 decoder. Response sections never use dynamic refs.

### HTTP/3 (`in Quic.cs`)
- Control stream (SETTINGS with QPACK capacity 0), QPACK encoder and
  decoder streams, request streams: HEADERS -> route lookup ->
  HEADERS + DATA + FIN.

### UDP plumbing (`NetworkStack`, `QuicServer`)
- New `NetworkStack.ReceiveUdpTo(port, ...)`: destination-port match
  that stops scanning at the first non-matching entry, so service
  polls never swallow DHCP/DNS datagrams.
- `QuicServer.Pump` (driven from `WebService.Tick`) drains UDP/443,
  processes datagrams, transmits replies; the service tick flushes
  queued TX each slice.

## Testing methodology

QEMU user-mode networking (slirp) never delivered host-forwarded
connections in this environment (ARP completes, no SYN), so host-side
acceptance runs over a **tap link**: WSL2 owns `10.0.2.2/24` on
`n0tap`, the guest is `10.0.2.15`. Host `curl` talks to the guest
directly; no NAT quirks.

Scripts (all in `build/`):

| Script | Purpose |
|--------|---------|
| `p9-h2test.sh` | boots QEMU (slirp), runs the in-guest `h2test` utility (12 checks: HPACK round-trip, SETTINGS, streams, push, upgrade) |
| `p9-h2-tap.sh` | tap link; host curl matrix: h1.1, h2 prior knowledge, h2c upgrade, multiplexed requests, TLS h1.1, TLS h2 (ALPN) |
| `p9-quic-test.sh` | tap link; aioquic HTTP/3 GETs (`/health`, `/`) |
| `p9-tls-isolate.sh` | TLS-only bisection (curl `--no-alpn`, openssl s_client) |
| `p9-h3-relay.py` | raw-aioquic relay harness used to debug the QUIC flight |
| `gen-hpack-tables.py`, `gen-qpack-tables.py` | table generators |

## Debugging notes (Tier-0 JIT hazards hit in this task)

The managed JIT miscompiles several patterns; all of these were found
the hard way and fixed:

1. **Lost local writes in huge frames** — `QuicTlsServer` originally
   assembled the whole server flight in one method; `en/cn/vn/hn`
   length counters read back as garbage, producing near-empty
   messages. Fix: every message builder is now a small method.
2. **Property reads in large frames** — `TlsWriter.Length` returned
   inconsistent values; the EncryptedExtensions builder now uses
   plain ints (`BuildEeBody`).
3. **Double-drain bookkeeping** — TLS `ReadApp` and `ProcessRecords`
   both drained staged app data; the redundant drain in the big
   method corrupted `_appPendingLen`, stalling h2-over-TLS. Fix:
   single drain in `ReadApp`.
4. **Out-parameter corruption** — `SysInfo.GetWallClock` out values
   were scrambled in `X509.BuildValidity` (day/hour garbage), which
   strict X.509 parsers (aioquic/OpenSSL 3.x) reject. Fix: read the
   clock in a dedicated small method and validate every field.
5. String `==` compares references — all header names use char-wise
   comparisons (`Hpack.StrEq`, `Qpack.StrEq`).

## Protocol bugs found by real clients (and fixed)

- `Http2Connection.Fill` never advanced `_inLen` (bytes read but
  invisible); the original in-guest test only passed because the
  first segment arrived via `Seed`.
- WebService never flushed queued TX when no RX frame arrived.
- QUIC Initial keys were derived *after* decrypting the Initial that
  required them (chicken-and-egg) - now derived when the first
  long-header DCID is known.
- Receive path used the server's keys instead of the client's.
- ServerHello was not appended to the TLS transcript -> wrong
  handshake secrets.
- ServerHello advertised the wrong extension block length (44 vs 46).
- STREAM frame type omitted the LEN bit (0x0C vs 0x0E).
- QPACK literal-with-name-reference omitted the T (static) bit ->
  clients treated it as a dynamic reference (0x200 error).
- Server Initial datagram was 2 bytes under the 1200-byte minimum.

## Limitations (documented, by design)

- QPACK dynamic table: disabled (capacity 0); no server push in H3.
- QUIC: no retry, no 0-RTT, no key update, single connection slot,
  no connection migration; ACKs are immediate (no delayed ACK).
- Initial packets for a second concurrent client are handled by
  replacing the connection (fine for test workloads).
- slirp egress limitation: host-forwarded *connections into* the
  guest do not deliver under QEMU 8.2 slirp; all host-side tests use
  the tap link instead.
