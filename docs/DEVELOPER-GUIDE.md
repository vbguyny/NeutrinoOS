# NeutrinoOS — Developer Guide (v1.0.0)

How to build, test, and extend NeutrinoOS. Assumes the WSL2 Ubuntu 24.04
development environment described in `docker-dev-environment.md` and the
toolchain pinned in `toolchain.lock`.

## 1. Repository layout

```
src/
  kernel/      AOT kernel (C#, bflat zero-library) + Tier-0 JIT
  ddk/         Driver Development Kit: drivers + services (sshd, webhost,
               TLS, network stack, crypto, users) — a normal .NET 10 lib
  korlib/      BCL replacement types used by JIT-world code
  lib/         ProtonOS.Net (HTTP client stack) etc.
  utilities/   37+ shell utilities (.NET 10 console apps, DDK consumers)
  apps/        sample applications (hello, args)
tests/
  benchmarks/  Phase 7 benchmark suite
  run-phase*-tests.ps1   Windows acceptance runners
build/         WSL-side build/deploy/test scripts (all the pN-*.sh helpers)
scripts/       Windows-side PowerShell (VM bring-up, OVA, installers, USB)
docs/          phase documentation (this file's neighbors)
specs/         phase specifications (input documents)
```

## 2. Build workflows

All builds happen in WSL2 (`wsl -d Ubuntu-24.04 -u root`).

```bash
bash build/wsl-rebuild.sh      # kernel + boot image  (rsync src/+tests/ to /root/neutrino)
bash build/p5-apps-build.sh    # DDK + utilities      (-> /root/phase5bin)
bash build/p7-apps-build.sh    # benchmarks
bash build/p5-deploy.sh        # compose /root/run.img (image + apps + DDK)
make reproducible              # clean-build determinism check
make release                   # dist/ artifacts (see PHASE7-RELEASE.md)
```

Notes:

- `wsl-rebuild.sh` **does not sync `tools/`** (the bflat fork lives at
  `/root/neutrino/tools/bflat` and is built once). New/changed `build/*.sh`
  must be copied manually and CRLF-stripped (`sed -i 's/\r$//'`)—Windows
  editors add CRLF, bash chokes on it.
- Kernel edits go through `src/` only; the Makefile compiles kernel +
  image; `SOURCE_DATE_EPOCH` + `mformat -N` keep it byte-stable.

## 3. Run & debug

```bash
bash build/p7-bootloop.sh 3     # 3 boots, counts pass/halt (crash-rate tool)
bash build/p7-bootmeasure.sh    # boot timeline + jitstats
bash build/p7-bench.sh          # full benchmark suite (≈4 min)
bash build/p7-stats.sh          # quick sanity counters
bash build/p6-qemu-serve.sh     # persistent VM with SSH 2222 / HTTP 8080 / HTTPS 8444 forwards
```

Debugging aids (see `CLAUDE.md` for the full GDB flow):

- `build/x64/BOOTX64.pdb` + `build/x64/kernel_syms.elf` — symbols
  (AOT: `kernel_Namespace_Type__Method`), regenerated every link.
- `tools/gen_elf_syms.py` — PDB → ELF symbol converter.
- `tools/gdb-protonos.py` — GDB helper (`proton-connect` loads symbols
  once the kernel publishes its load address; JIT symbols register at
  runtime).
- Serial is the ground truth: `-serial file:/root/bootloop.log` in all
  harness scripts; when in doubt, `strings <log>`.

## 4. Code conventions (learned the hard way)

- **Kernel code is AOT (bflat)**: no `lock`, no reflection, no
  `Span<T>` conversion operators, no `System.Linq` in kernel paths.
  Strings and formatting are manual (`IntToStr`, `WriteHex`).
- **DDK/JIT-world code** runs on korlib: avoid `switch` over strings with
  patterns, avoid implicit `byte[] -> ReadOnlySpan<byte>`, prefer pointer
  overloads, and keep allocations modest (the JIT world's GC is simple).
- **Syscalls**: validate user pointers with `UserAccess.Valid`
  (write-targets must range-check before storing). Return `-Errno.*`.
- **Services are cooperative**: no threads inside sshd/webhost; they get
  `Tick()` calls from the kernel idle hook. Never block.
- **Boot output**: quiet by default (`NetworkStack.Quiet`,
  `Tls13.Verbose`, `JitDiag.VerboseJit`) — serial bandwidth is a real
  performance budget (≈17k lines ≈ 50 s of wire time).
- **Tests**: ring-3 kernel tests emit raw machine code
  (`UserModeTests`); user-level test assemblies live in `src/JITTest`,
  `src/AppTest`. New syscall behavior goes into both a kernel-side test
  and the acceptance scripts.

## 5. Security checklist for changes

Run these before calling a change done:

1. `bash build/p7-bootloop.sh 1` → `pass=1 halt=0`, `[SEC] 4 pass 0 fail`.
2. Any syscall touching user memory → `UserAccess.Valid` + Tests.
3. New service surface → limits + audit logging (copy the sshd/webhost
   patterns: per-IP counters, `SshAuthGuard.Log`).
4. Crypto changes → run `build/p6-crypto-test.sh` (KATs must stay 30/30).
5. Release-facing → re-run `make reproducible`.

## 6. Performance workflow

- Measure first: `p7-bench.sh` (writes results to the serial log),
  `boottime`, `jitstats`, `gcstats`, `netstat -s`, `perf`.
- `/dev/profiler` + `perf` sample the kernel IP ring buffer; symbolicate
  with `build/p7-rt-sym.sh <addr>` (runtime-address resolver; the ELF is
  based at runtime − 0x138000000).
- Serial output is the top boot/throughput killer in debug images — gate
  traces, don't delete them (`if (Quiet)` pattern).
- Record every optimization before/after in
  `docs/PHASE7-PERF-RESULTS.md` (style: measured, reproducible command).

## 7. Release engineering

See `docs/PHASE7-RELEASE.md` (reproducible builds, signing, artifacts)
and `docs/PHASE7-INSTALL-WINDOWS.md` (end-user flows). Commit hygiene:
one wave per commit, messages via `git commit -F <file>` (PowerShell
mangles quotes/brackets in inline messages).
