# PHASE4-ACCEPTANCE.md - verification guide and current status

How to verify each Phase 4 acceptance criterion from a Windows 11 host
(WSL2 Ubuntu for the build/QEMU side), and the status measured on the
current tree. Statuses: **PASS** (verified), **PARTIAL** (works with
caveats), **BLOCKED** (known JIT issue, see PHASE4-JIT-COMPAT.md),
**PENDING** (implemented but not yet verified on hardware).

## 0. Prerequisites

```powershell
# Windows: build source of truth is d:\Projects\Code\NeutrinoOS
wsl -d Ubuntu-24.04 -u root
```

```bash
cd /root/neutrino            # mirror synced from the Windows tree
bash /mnt/d/Projects/Code/NeutrinoOS/build/wsl-rebuild.sh   # sync + build + image
bash /mnt/d/Projects/Code/NeutrinoOS/build/p4-apps-build.sh # build the 8 test apps
bash /mnt/d/Projects/Code/NeutrinoOS/build/p4-deploy.sh     # /root/run.img with markers + apps
```

## 1. Boot regression (Phases 1-3 intact) - **PASS**

```bash
cp /root/neutrino/build/x64/neutrinoos.img /root/test.img
bash /mnt/d/Projects/Code/NeutrinoOS/build/ab-test.sh /root/test.img fixtest1 55
# expect: HALTED=0, boot tests complete=1, AppTest results identical to
# the pre-Phase-4 baseline (20 passed / 4 network-environment failures).
```

`make run-qemu-vga` (VGA mirror) was not modified in Phase 4; the boot
path and console layer are unchanged by the JIT/korlib additions.

## 2. Standard .NET 10 app executed via the shell - **PASS**

```bash
# interactive QEMU with serial stdio (see build/phase4-qemu.sh), then:
neutrinoos> run /apps/p4hello.dll
Hello, NeutrinoOS!
[run] exited with code 0
```

`p4hello` is a plain `dotnet build -f net10.0` app (no NoStdLib); its BCL
references resolve to korlib via the virtual-assembly redirect.

## 3. Hello World output - **PASS** (line above).

## 4. Interactive console test - **PENDING**

```text
neutrinoos> run /apps/p4inter.dll
[interactive] type lines; 'exit' quits
<type a line>          -> [interactive] echo: <line>
exit                   -> [interactive] bye   (exit code 0)
```

Builds and deploys; the interactive session still needs a manual pass.

## 5. File I/O test - **BLOCKED**

`run /apps/p4fileio.dll` currently faults (`#GP`) on the first write:
the JIT's virtual/interface call sites with stack arguments are not yet
parity-corrected, which misaligns the interface-dispatch chain in the FAT
driver (details in PHASE4-JIT-COMPAT.md). Reads work.

## 6. Collections + LINQ test - **PASS**

```bash
bash /mnt/d/Projects/Code/NeutrinoOS/build/p4-build-linq.sh   # build p4linq
bash /mnt/d/Projects/Code/NeutrinoOS/build/run-linq-check.sh # deploy + run + capture
# expect: all 34 [linq] checks ok, "[linq] PASS", "[run] exited with code 0"
```

Covers `List<T>`, `Dictionary<K,V>`, `HashSet<T>`, `Queue/Stack`, the
korlib LINQ operators (Where/Select/SelectMany/OrderBy/ThenBy/GroupBy/
Join/Distinct/Take/Skip/Concat/Aggregates) and the `string.Join`/`Split`
helpers. Two real korlib bugs found along the way were fixed (ThenBy key
priority in `CompareKeyChain`; string ordering + boxed-int equality in
`ObjectComparer<T>` - see PHASE4-JIT-COMPAT.md), and three stale test
expectations were corrected to match real .NET 10 semantics (verified
against a net10.0 ground-truth run).

### Root causes fixed for this item (summary)

- Generic `newobj` MemberRef resolution: array type arguments
  (`ELEMENT_TYPE_SZARRAY`/`ELEMENT_TYPE_ARRAY`) were rejected by the
  TypeSpec parser, and the GenParamCount byte was not skipped for generic
  method signatures; MemberRef-backed generic methods could also reuse
  code compiled for another instantiation.
- `List<T>.Enumerator` vtable registration: explicit-interface
  protection previously masked a whole interface, misregistering
  `MoveNext`/`Reset` at sequential slots instead of interface slots and
  leading to an int-flavored `MoveNext` being executed for
  `List<string>.Enumerator` (buffer corruption during `OrderBy`).

## 7. Async/await test - **PARTIAL**

`run /apps/p4async.dll` completes `Task.Run(...).Result` and
`Task.Run(...).Wait()` (both observed ok) but hangs later in the await
state-machine sequence. Tracked in PHASE4-JIT-COMPAT.md.

## 8. Networking test (HttpClient) - **PENDING (degraded-capable)**

The harness QEMU has no NIC, so `run /apps/p4net.dll` reports
`[net] no network stack available (expected: no NIC in the test harness)`
and exits 0. For a live fetch, boot with a virtio-net device on a
user-mode network and a local HTTP server on 10.0.2.2:8080.

## 9. Multi-assembly test - **PENDING (loader implemented)**

Deploy `p4math.dll` to `/lib`, `p4multi.dll` to `/apps`, then
`run /apps/p4multi.dll` should print `[multi] PASS` (exit 0). The
on-demand `/lib` loader is implemented (PHASE4-DESIGN.md 1.2); the
end-to-end run still needs a verified capture.

## 10. C# 14 features test - **PENDING**

`run /apps/p4cs14.dll` - the app builds; the run produced no captured
output yet and needs a dedicated serial capture + fix pass.

## 11. `System.Linq.AsyncEnumerable` in korlib - **PENDING**

Not yet present; see PHASE4-BCL.md "Pending BCL items".

## 12. No C/C++ in kernel/bootloader/driver/korlib - **PASS**

All Phase 4 code is C# (plus the existing `.asm` intrinsics in
`src/kernel/x64/`). Verify: `git ls-files | grep -E '\.(c|cc|cpp|h)$'`
returns only pre-existing non-kernel hits, if any.

## 13. Prior-phase docs still valid - **PASS (with this document)**

`docs/BUILD-WINDOWS.md` and `docs/PHASE2-ACCEPTANCE.md` /
`docs/PHASE3-ACCEPTANCE.md` flows are unchanged; this document adds the
Phase 4 verification flow. A BUILD-WINDOWS.md section covering
"Writing and deploying .NET 10 console applications" is still to be
added (the template's README and `scripts/build-app.ps1` cover the
steps today).

## Automated runner

`tests/run-phase4-tests.ps1` (Windows host) builds the apps, deploys
them, boots QEMU with the scripted serial session and asserts the
expected markers per app. Use it after each JIT fix to re-check the
matrix above.
