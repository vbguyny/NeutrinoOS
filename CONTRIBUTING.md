# Contributing to NeutrinoOS

Thanks for wanting to help! This guide covers the practical steps.

## Ground rules

* Read `CODE_OF_CONDUCT.md`.
* Search existing issues/PRs before opening a new one.
* For anything larger than a small fix, open an issue first so we can
  agree on the approach.

## Building

The build runs in WSL2 (Ubuntu) with the pinned toolchain
(`toolchain.lock`, see `docker-dev-environment.md`):

```bash
bash build.sh          # clean x64 build + image
./run.sh               # boot in QEMU (serial to stdout + qemu.log)
./kill.sh              # always stop QEMU after a test run
bash build/p8-arm64-test.sh    # ARM64 acceptance (QEMU virt + AAVMF)
```

Phase acceptance suites:

```powershell
powershell -ExecutionPolicy Bypass -File tests\run-phase7-tests.ps1
powershell -ExecutionPolicy Bypass -File tests\run-phase8-tests.ps1 [-Only <leg>]
```

Run the relevant suite before opening a PR; note the results in the PR
description ("`run-phase8-tests.ps1 -Only arm64` -> ALL-PASS", etc.).

## Code conventions

* Kernel: C# for the bflat AOT/`korlib` runtime (no stdlib assumptions —
  check `docs/PHASE4-BCL.md`); native entry stubs live in
  `src/kernel/{x64,arm64}/native.*`; keep `ARCH_X64` / `ARCH_ARM64`
  gating explicit (`#if ARCH_ARM64`).
* Utilities/apps: standard .NET 10 compiled against the BCL, executed on
  the Tier-0 JIT — avoid BCL APIs outside `docs/PHASE4-JIT-COMPAT.md`,
  and prefer the existing `src/utilities/Common` helpers.
* Docs: every phase lands a `docs/PHASE<N>-*.md`; user-facing tools get
  help text updates with the change.
* Commit messages: `Phase N Task M (part K): summary` for phase work.
  Keep each commit buildable.

## Pull requests

1. Fork, branch from `main`.
2. Keep the change focused; update docs + `tests/` where behavior changes.
3. CI expectations: `tests/run-phase8-tests.ps1` legs relevant to the
   touched area (sdk, repo, samples, arm64, npkg, drivers) pass.
4. Describe *what* and *why*, plus the verification you ran.

## Reporting bugs / requesting features

* Use GitHub issues; include: what you ran, what you expected, what
  happened, and the relevant log (`qemu.log`, `build\vbox-gui-serial.log`,
  or the acceptance script output).
* Kernel crashes: attach the `SYNC EXCEPTION` / `!!! RAWV` dump if one
  was printed.

## Community packages

See `docs/COMMUNITY-GUIDE.md` for publishing packages to the community
repository and `docs/PACKAGE-GUIDELINES.md` for the rules every package
must follow.
