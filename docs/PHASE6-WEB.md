# PHASE6-WEB.md — .NET web hosting on NeutrinoOS

## Decision: fallback HTTP/1.1 server for Phase 6

Porting Kestrel (transport + core + HTTP/1.1 parser + pipeline) to the
Tier‑0 JIT world is a large multi-phase effort: Kestrel depends on
`System.IO.Pipelines`, `ValueTask`/async machinery, `Span`-heavy APIs
and thread-pool scheduling that the current JIT world does not provide.
Phase 6 therefore ships the **fallback HTTP/1.1 + TLS 1.3 server**
implemented directly in the DDK (`ProtonOS.DDK.Services.WebService`),
exactly as the Phase 6 spec allows, and keeps Kestrel porting as a
Phase 7+ goal.

The template `templates/NeutrinoWebApp` (minimal API, `net10.0`,
`WebApplication.CreateBuilder`) is provided as the **target interface**
for that future work; it builds and runs on desktop .NET 10 today. The
on-device fallback server implements the same routing surface —
`/`, `/health`, `/time` — plus `/var/www` static files.

## On-device server (`webhost`)

- Start/stop/status via the `webhost` utility (service name `webhost`
  → `WebService` in the kernel ServiceRegistry; cooperative `Tick()`).
- Config `/etc/webhost.conf`: `Port` (default 80), `HttpsPort`
  (default 443).
- Certificates: `/etc/ssl/certs/neutrinoos.crt` +
  `/etc/ssl/private/neutrinoos.key`; self-signed Ed25519 PEM generated
  on first start when missing (see PHASE6-TLS.md).
- Routes:
  - `/` — built-in HTML page
  - `/health` — `{"status":"ok"}`
  - `/time` — `{"utc":"YYYY-MM-DDTHH:MM:SS","uptime_s":N}` (the date
    string comes from the proven `date` utility through the kernel
    shell bridge)
  - everything else — static files under `/var/www` (path traversal
    rejected; `/dir/` serves `dir/index.html`; extension-based
    content types; 404 otherwise)
- HTTP: keep-alive for HTTP/1.1 (up to 100 requests, 12 s idle
  timeout), `Connection: close` honored; GET/HEAD/POST accepted.
- HTTPS: TLS 1.3 (X25519 + AES‑128‑GCM) on `HttpsPort`; verified with
  `curl -k https://…/health` → 200.

## Deployment model

```
webhost                          # starts HTTP (80) + HTTPS (443)
/etc/boot.params: webhost.autostart=yes   # start at boot
/etc/rc.local: webhost                    # alternative
```

`.NET web apps` deployed as DLLs under `/apps/webapp/` run through the
normal Tier‑0 JIT (`run /apps/webapp/webapp.dll`); hosting *ASP.NET
Core* binaries on device requires the Kestrel port and is deferred.

## Testing (all green)

- `build/p6-web-test.sh`: boots with `net.ip=dhcp` +
  `webhost.autostart=yes` (no console input), asserts `/`, `/health`,
  `/time`, `/var/www/hello.txt`, 404 handling, and HTTPS over the
  capture proxy (TLS handshake logs + `code=200`).
- `build/p6-firewall-test.sh`: `deny tcp 443` blocks HTTPS while HTTP
  stays up.
- `build/p6-tls-proxy.py` + `build/p6-tlsparse.py`: wire-level capture
  and record parser used during TLS bring-up.

## HTTPS configuration for clients

The certificate is self-signed, so clients must trust it or skip
verification (`curl -k`, browser exception). On Windows:

```powershell
wsl curl -sk https://127.0.0.1:8444/health     # OpenSSL client -- works
```

**Client compatibility note.** The server's certificates and
CertificateVerify use Ed25519. Windows `curl.exe` and .NET clients use
Schannel, which does not offer Ed25519 signature algorithms and fails
the handshake (`SEC_E_ILLEGAL_MESSAGE`, curl exit 35; the guest logs
`hello rejected ... sig=0`). OpenSSL-based clients (WSL `curl`,
Git Bash curl), and Chromium/Firefox (BoringSSL/NSS) work. ECDSA/RSA
certificates for Schannel are a Phase 7 item.

## Phase 7+ items

- Kestrel transport/core port (sockets adapter, pipes, thread pool),
  HTTP/2/3, `UseStaticFiles()` fidelity beyond basic mapping, range
  requests and compression, SNI multi-certificate selection, session
  resumption.
