# NeutrinoOS project signing keys

```
private.key   Ed25519 seed, 64 hex characters
public.key    public key, 64 hex characters
fingerprint   5da1ed4fca245a0a08db88a42b187583a8eabc640f8a7eb129111f0fc7e1967b
```

**Policy (read this first):**

* This is the **official NeutrinoOS project key** used to sign the
  official repository index and official packages (see
  `docs/PHASE8-ECOSYSTEM.md`).
* The private key is **intentionally published in this repository** for
  development images and reproducible test setups. It is *not* a secret.
  Anyone can produce packages that appear "signed by the project".
* For any real release, generate a fresh key **offline**, publish only
  `public.key` + the fingerprint, and sign release artifacts with the
  offline key. Devices pin the fingerprint at `npkg repo add` time, so
  key substitution is detected.

## Using the project key

```bat
:: sign a package
npkg-host sign MyApp.npkg keys\private.key

:: generate + sign a repository index
npkg-host repo-index --dir repo --key keys\private.key --name main
```

## Third-party developers

Do **not** use the project key for your own packages. Generate your own:

```bat
npkg-host keygen                :: -> %USERPROFILE%\.neutrinoos\{private,public}.key
npkg-host fingerprint %USERPROFILE%\.neutrinoos\public.key
```

Publish your public key + fingerprint with your repository so users can
pin it (`npkg repo add <name> <url> --fingerprint <hex64>`), and follow
`docs/PACKAGE-GUIDELINES.md`.
