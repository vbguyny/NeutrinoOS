# PHASE6-USERS.md — minimal user management

Implemented in `src/ddk/Users/UserDatabase.cs` for SSH authentication.

## Files

`/etc/passwd` — `username:uid:gid:fullname:home:shell`

```
root:x:0:0:root:/root:/bin/shell.dll
user:x:1000:1000:user:/home/user:/bin/shell.dll
```

`/etc/shadow` — `username:hash:salt` where `hash` is a scrypt KDF output
(`N=16384, r=8, p=1, dkLen=32`, `ProtonOS.DDK.Crypto.Scrypt`) and `salt`
is the hex salt. The image builder (`build/p6-image-extras.sh`)
generates these with Python's `hashlib.scrypt` using the same
parameters, so host-created and guest-created entries interoperate.

`~/.ssh/authorized_keys` — one `ssh-ed25519 <base64> [comment]` key per
line; the home directories are `/home/root` and `/home/user`.

## API

| Method | Purpose |
|--------|---------|
| `LoadUsers()` | parse `/etc/passwd` |
| `Lookup(name)` | find an entry |
| `VerifyPassword(user, password)` | scrypt-hash + constant-ish compare against `/etc/shadow` |
| `SetPassword(name, password)` | add/replace a shadow entry |
| `AddUser(...)` | append a user (defaults matching the image) |
| `IsAuthorizedKey(user, key32)` | exact match against `authorized_keys` |

Policy: the `root` account is rejected for network authentication
(`SshConnection.CheckPassword/CheckPublicKey`); root remains console-only.

## Managing users on device

- Change a password: edit `/etc/shadow` with a scrypt hash produced by
  the same parameters (the `userdb` helper utilities are a Phase 7
  nicety; the Phase 6 flow is documented in PHASE6-ACCEPTANCE.md).
- Add an SSH key: append the public key line to
  `/home/<user>/.ssh/authorized_keys` (FAT-friendly path notes: leading
  dot directories exist in the image's FAT volume — the deploy scripts
  create them with mtools).

## Limitations

- No PAM, no groups beyond gid fields, no password aging/lockout fields
  (the shadow format keeps two trailing fields for round-tripping).
- Authorization is exact-key matching; no key revocation lists.
- Filesystem permissions are enforced by convention, not by ACLs.
