# PHASE6-SSH.md — SSH server (sshd)

`ProtonOS.DDK.Services.SshService` is a cooperative SSH‑2.0 server
(RFC 4251–4254) with a full client interop test against OpenSSH 9.6 and
the Windows OpenSSH client.

## Algorithms

| Layer | Implemented |
|-------|-------------|
| KEX | `curve25519-sha256` |
| Host key | `ssh-ed25519` |
| Cipher | `aes128-ctr`, `aes256-ctr` |
| MAC | `hmac-sha2-256-etm@openssh.com`, `hmac-sha2-512-etm@openssh.com`, `hmac-sha2-256`, `hmac-sha2-512` |
| Compression | `none` |

`chacha20-poly1305@openssh.com`, `aes*gcm@openssh.com`,
`ecdh-sha2-nistp256`, `diffie-hellman-group14-sha256` and RSA host keys
are **deferred** (crypto exists for some; wiring is Phase 7 work — see
the report).

## Authentication

- `password` — scrypt hashes from `/etc/shadow` (see PHASE6-USERS.md).
- `publickey` — `ssh-ed25519` keys from `~/.ssh/authorized_keys`; the
  query/`true` signature flow (`SSH_MSG_USERAUTH_PK_OK` + signed
  `session_id || 50 || …` blob) is fully implemented.
- `root` is intentionally refused for **network** logins (console only);
  use the `user` account.

## Sessions and the shell bridge

- `session` channel with `pty-req` (accepted; no dynamic resize yet),
  `shell`, `exec`, `window-change` (accepted), `env`.
- `exec`/`shell` lines run through the **kernel shell bridge**
  (`Kernel_ShellExec`): output is captured via `Console.SetOut` into a
  StringWriter, capped at 64 KB, and returned with the command's exit
  status. The interactive line editor supports arrows, history (8),
  Ctrl+C/Ctrl+D and backspace.
- `subsystem`/SFTP deferred.

## Configuration

`/etc/ssh/sshd_config` (key=value subset): `Port` (default 22),
`ListenAddress` (accepted), `HostKey`, `PasswordAuthentication`,
`PubkeyAuthentication`, `AuthorizedKeysFile` (default
`~/.ssh/authorized_keys` mapped to `/home/<user>/.ssh/authorized_keys`).

Host keys: generated on first start at
`/etc/ssh/ssh_host_ed25519_key` (32‑byte seed, hex — NeutrinoOS format)
from `Csprng`.

## Service model

- `sshd` (utility) → `Services.Start("sshd")` → the kernel
  ServiceRegistry compiles `SshService.Start/Tick/Stop` via the Tier‑0
  JIT and calls `Tick()` from the shell idle hook.
- Autostart at boot via `/etc/boot.params` (`sshd.autostart=yes`) or
  `/etc/rc.local`.
- Up to 4 concurrent connections.

## Interop bugs found during bring-up (fixed; useful history)

1. `V_S` in the exchange hash must exclude CR/LF (RFC 4253 §8).
2. Key derivation must use the **OpenSSH encoding**: length-prefixed
   mpint(K), then *raw* H, letter, *raw* session_id; extension blocks
   `mpint(K) || H || K1..Kn-1`. Length-prefixing H/session_id produced
   keys that still let the signature verify (H is inside the signature)
   but broke every MAC.
3. MAC key length is the algorithm's key length (hmac-sha2-256 → 32
   bytes) — deriving 64 and using them all was the final handshake
   blocker.
4. EtM framing (OpenSSH style): cleartext length, MAC over
   `seq || len || ciphertext`, padding aligned so that `packet_length`
   itself is a multiple of the block size — and the CTR keystream skips
   the four AAD bytes entirely.
5. Name-list negotiation details: compression lists must be matched as
   name-lists (`"none,zlib@openssh.com" != "none"`), and the
   `first_kex_packet_follows` guessed packet is dropped only when the
   client's first KEX choice differs from the negotiated one.

## Testing

- `build/p6-ssh-test.sh` (all-green): exec (`echo`, `uname`),
  interactive `-tt` session, negative wrong-key auth; host log shows
  the exec sessions flowing through the shell bridge.
- `scripts/phase6-ssh-demo.ps1` — Windows client demo session.
- `tests/run-phase6-tests.ps1` — includes Windows OpenSSH checks.
