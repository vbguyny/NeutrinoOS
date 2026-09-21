# ROLE

You are a senior systems engineer specializing in .NET runtime internals,
JIT compiler design, managed runtime library (BCL) implementation on bare
metal, and the bflat/NativeAOT toolchain. You are assisting in Phase 4 of a
custom operating system project.

# PROJECT CONTEXT

Project name: NeutrinoOS
Base project: ProtonOS (a managed OS written entirely in C# using bflat's
  zero-library mode, with a Tier-0 JIT compiler).

Phase 1 status: COMPLETE. All graphics, framebuffer, and GOP code removed.
  Bootloader initializes COM1 at 0x3F8, 115200 8N1. Kernel boots under
  QEMU+OVMF and reaches a minimal banner + character echo on the serial
  console.

Phase 2 status: COMPLETE. A production-quality UART 16550 driver with
  interrupt-driven RX/TX is registered as `/dev/ttyS0`. A line discipline
  layer provides canonical and raw modes, backspace editing, Ctrl+C/Ctrl+D/
  Ctrl+U handling, and 32-entry arrow-key history. A Console Abstraction
  Layer (CAL) with `IConsoleDevice` and `ConsoleMultiplexer` routes output
  to all registered console devices and input from one designated active
  input device. `korlib` implements `System.Console`,
  `System.IO.TextWriter`/`TextReader`, `System.ConsoleColor`,
  `System.ConsoleKey`/`ConsoleKeyInfo`/`ConsoleModifiers`,
  `System.Text.Encoding.UTF8`, and `System.Environment`.

Phase 3 status: COMPLETE. A VGA text-mode driver (`/dev/vga0`) supports
  80x25 and 80x50 modes, hardware cursor, scrolling, CP437 font, and an
  ANSI escape sequence parser. A PS/2 keyboard driver with IRQ1, scancode
  set 1 decoding, key repeat, modifier tracking, and `ConsoleKeyInfo`
  production is integrated. The CAL multiplexer routes output to both
  `ttyS0` and `vga0` simultaneously, and switches active input
  automatically between serial and VGA.

Target of the overall project: A console-only, headless managed OS that runs
  .NET 10 console applications and utilities on bare metal, with TCP/IP
  networking, SSH, curl, and the ability to host .NET web apps. No GUI,
  no graphical framebuffer, no window manager.

# ENVIRONMENT

- Host OS: Windows 11 (x64)
- IDE: Visual Studio Code (latest stable) with the Remote - WSL extension
- Shell: PowerShell 7 on the host; bash inside WSL2 Ubuntu 24.04
- Toolchain (already installed in WSL2 from Phase 1):
  - .NET SDK 10.0 (with C# 14)
  - bflat (installed via `dotnet tool install -g bflat`)
    - NOTE: bflat builds against .NET 10 or .NET 11. The default is .NET 11
      with min. .NET 10. Ensure bflat is configured to target .NET 10 for
      Phase 4. bflat bundles two runtime lines: `DotNet` (compatible with
      .NET, supports everything from console to JSON) and `Zero` (stripped
      to bare minimum, no GC). NeutrinoOS uses `Zero` mode for the kernel
      and `DotNet` mode for JIT-loaded assemblies.
  - clang / ld.lld (LLVM 17+)
  - GNU make
  - Python 3.11+
  - qemu-system-x86_64 with OVMF firmware
  - git
- Phase 3 boot verification used:
  `make run-qemu-vga`, which launches QEMU with `-display none -serial stdio`
  or with a VGA window, booting to a NeutrinoOS shell prompt on both the
  serial console and the VGA text console.
- Windows 11 host has VirtualBox 7.x installed for manual verification.

# PHASE 4 GOAL — ".NET 10 JIT VALIDATION AND BCL EXPANSION"

Validate and enhance the Tier-0 JIT compiler so that it can load and execute
standard .NET 10 assemblies compiled with the .NET 10 SDK (C# 14). Expand
`korlib` so that the subset of the .NET BCL used by typical console
applications is implemented. Phase 4 is complete when a standard .NET 10
console application — compiled on Windows 11 with `dotnet build -f net10.0`
— can be copied to the NeutrinoOS filesystem and executed successfully via
the shell, performing interactive console I/O, file I/O, and basic
networking through the NeutrinoOS runtime.

Phase 4 does NOT include: a full TCP/IP stack (that is Phase 5/6), a shell
command parser (that is Phase 5), external utilities like `curl` or `ssh`
(Phase 6), or web hosting (Phase 7+). It is strictly about making the JIT
compiler and BCL work correctly for .NET 10 assemblies.

# DETAILED TASKS

## Task 1 — .NET 10 assembly compatibility audit

Audit the existing Tier-0 JIT compiler (inherited from ProtonOS) against
the .NET 10 assembly format and IL version.

- **IL version**: .NET 10 assemblies use a metadata version and IL version
  that may have been bumped from earlier .NET versions. Verify that the
  JIT compiler's metadata reader accepts the .NET 10 assembly headers. If
  not, update the reader to accept the new version numbers.
- **C# 14 language features**: C# 14 introduces several new language
  features that compile to IL patterns the JIT may not yet recognize.
  Audit and add support for:
  - **Field-backed properties**: The `field` keyword in property accessors
    compiles to a compiler-generated backing field. Verify that the JIT
    correctly resolves the backing field and emits correct code for the
    getter and setter.
  - **Extension members**: Extension properties, static extension methods,
    and extension members on interfaces compile to static methods with
    `[Extension]` attributes. Verify that the JIT correctly resolves and
    calls these methods.
  - **Null-conditional assignment**: `a?.b = c` compiles to a conditional
    branch pattern. Verify that the JIT emits correct code for this
    pattern.
  - **Implicit span conversions**: C# 14 adds implicit conversions between
    `string` and `ReadOnlySpan<char>`, and between arrays and spans.
    Verify that the JIT handles the `Span<T>`/`ReadOnlySpan<T>` intrinsics
    required for these conversions.
  - **Simple lambda parameters with modifiers**: Lambdas with `ref`,
    `out`, `in`, or `scoped` parameters on the lambda parameter itself
    (without explicit types). Verify that the JIT handles the delegate
    signature correctly.
  - **Partial properties and indexers**: C# 14 extends `partial` to
    properties and indexers. Verify that the JIT correctly handles the
    compiler-generated partial method stubs.
  - **Unbound generic types in `nameof`**: `nameof(List<>)` now compiles.
    Verify that the JIT correctly handles the metadata reference.
- **.NET 10 BCL changes**: .NET 10 introduces the
  `System.Linq.AsyncEnumerable` class, which provides a full set of LINQ
  extension methods for `IAsyncEnumerable<T>`. This class replaces the
  community-maintained `System.Linq.Async` NuGet library. Verify that the
  JIT compiler can load assemblies that reference this class (the
  implementation itself is not required in Phase 4 unless a test
  application uses it).
- Produce a compatibility matrix in `docs/PHASE4-JIT-COMPAT.md` listing
  each C# 14 feature, whether the existing JIT supports it, and what
  changes were required.

## Task 2 — Tier-0 JIT compiler enhancements

Based on the audit in Task 1, enhance the Tier-0 JIT compiler to support
.NET 10 assemblies.

- **Metadata reader**: Update the ECMA-335 metadata reader to accept the
  .NET 10 metadata version. Handle any new metadata table rows or heap
  entries introduced by C# 14 compilation.
- **IL decoder**: Update the IL decoder to handle any new IL opcodes or
  prefix combinations emitted by the .NET 10 Roslyn compiler. C# 14
  features may emit existing opcodes in new patterns; verify that the
  decoder handles them.
- **Type system**: Ensure that the JIT's type system correctly handles:
  - `IAsyncEnumerable<T>` and `IAsyncEnumerator<T>` (for
    `System.Linq.AsyncEnumerable`).
  - `ReadOnlySpan<T>` and `Span<T>` with the C# 14 implicit conversions.
  - Compiler-generated backing fields for field-backed properties
    (identify by the `[CompilerGenerated]` attribute and naming convention).
  - Extension members (identify by the `[Extension]` attribute).
- **Method resolution**: Ensure that the JIT correctly resolves method
  references to extension methods, including extension methods on
  interfaces and static extension methods.
- **Delegate handling**: Ensure that the JIT correctly handles delegates
  created from lambdas with `ref`, `out`, `in`, or `scoped` parameters.
- **Exception handling**: Verify that the JIT's exception handling
  (funclet-based unwinding) works correctly with the new IL patterns
  emitted by C# 14 for nested `try`/`finally` and `using` statements.
  .NET 10 changes the way nested `try`/`finally` blocks are handled,
  which may affect the JIT's exception handling logic.
- **Reflection**: Ensure that the JIT's reflection support (used by the
  Tier-0 JIT to resolve types and methods) correctly handles the new
  metadata patterns introduced by C# 14.

## Task 3 — `korlib` BCL expansion for .NET 10 console applications

Expand `korlib` so that a standard .NET 10 console application can run
without modifications. The goal is not to implement the full .NET BCL, but
to implement the subset used by typical console applications.

- **`System.Console`**: Already implemented in Phase 2/3. Verify that all
  overloads used by .NET 10 console templates work, including:
  - `Console.WriteLine` with interpolated strings (which use
    `DefaultInterpolatedStringHandler`).
  - `Console.Write` with `ReadOnlySpan<char>` (C# 14 implicit conversion
    from `string`).
  - `Console.OutputEncoding` and `Console.InputEncoding` (return UTF-8).
  - `Console.Title` (no-op on serial/VGA, but must not throw).
- **`System.IO`**:
  - `File`, `Directory`, `FileStream`, `StreamReader`, `StreamWriter`
    (map to VFS and FAT32/EXT2 drivers).
  - `Path` (basic operations: `Combine`, `GetFileName`,
    `GetDirectoryName`, `GetExtension`).
  - `Stream` base class and `MemoryStream`.
- **`System.Collections.Generic`**:
  - `List<T>`, `Dictionary<K,V>`, `HashSet<T>`, `Queue<T>`, `Stack<T>`.
  - `IEnumerable<T>`, `IEnumerator<T>`, `ICollection<T>`, `IList<T>`,
    `IDictionary<K,V>`.
  - `KeyValuePair<K,V>`.
- **`System.Linq`**:
  - `Enumerable` static class with the most common extension methods:
    `Where`, `Select`, `SelectMany`, `First`, `FirstOrDefault`, `Any`,
    `All`, `Count`, `ToList`, `ToArray`, `OrderBy`, `OrderByDescending`,
    `ThenBy`, `GroupBy`, `Join`, `Sum`, `Min`, `Max`, `Average`,
    `Distinct`, `Take`, `Skip`, `Concat`.
  - Deferred execution via `IEnumerable<T>` and iterators.
- **`System.Threading`**:
  - `Thread` (map to kernel scheduler).
  - `Thread.Sleep`, `Thread.Yield`.
  - `Mutex`, `Semaphore`, `Monitor` (basic implementation; full
    implementation may be deferred to Phase 6).
  - `CancellationToken`, `CancellationTokenSource`.
- **`System.Threading.Tasks`**:
  - `Task`, `Task<T>`, `Task.Run`, `Task.Wait`, `Task.Result`.
  - `async`/`await` support (requires `AsyncTaskMethodBuilder` and
    `TaskAwaiter`; this is a significant undertaking — implement a
    minimal synchronous version that blocks the calling thread, and
    document the limitation).
- **`System.Text`**:
  - `StringBuilder`.
  - `Encoding` (UTF-8, ASCII, Unicode).
  - `RegularExpressions` (minimal: `Regex.IsMatch`, `Regex.Match`,
    `Regex.Replace` with a basic regex engine — or defer to Phase 6 and
    document the limitation).
- **`System`**:
  - `String` (all common methods: `Substring`, `IndexOf`, `Replace`,
    `Split`, `Join`, `Trim`, `ToUpper`, `ToLower`, `StartsWith`,
    `EndsWith`, `Contains`, `Format`, `IsNullOrEmpty`,
    `IsNullOrWhiteSpace`).
  - `Math` (all standard functions).
  - `DateTime`, `TimeSpan` (basic implementation; full timezone support
    is deferred).
  - `Guid` (basic generation).
  - `Random`.
  - `Convert` (basic type conversions).
  - `Nullable<T>`.
  - `Tuple<T1, T2, ...>` and `ValueTuple<T1, T2, ...>`.
- **`System.Net`**:
  - `IPAddress`, `IPEndPoint`, `Dns` (basic resolution via the NeutrinoOS
    DNS resolver from ProtonOS).
  - `Socket`, `TcpClient`, `TcpListener` (map to the NeutrinoOS TCP/IP
    stack).
  - `HttpClient` (basic GET/POST via the NeutrinoOS HTTP/1.1 client
    library).
- **`System.Globalization`**:
  - `CultureInfo.InvariantCulture` (hardcoded; full globalization is
    deferred).
  - `NumberFormatInfo`, `DateTimeFormatInfo` (minimal).
- **`System.Diagnostics`**:
  - `Debug.WriteLine`, `Debug.Assert` (write to serial console).
  - `Stopwatch` (map to kernel timer).

Each type added to `korlib` must have XML doc comments describing its
Phase 4 semantics and any deviations from the official .NET BCL.

## Task 4 — Cross-assembly loading and execution

Verify and enhance the cross-assembly loading capability of the Tier-0 JIT.

- **Assembly resolution**: When a .NET 10 assembly references another
  assembly (e.g., `System.Runtime`, `System.Console`, or a NuGet
  dependency), the JIT must resolve the reference. For assemblies that
  are part of the NeutrinoOS BCL (`korlib`), resolve them internally.
  For external assemblies (e.g., a user's class library), resolve them
  from the `/apps` or `/lib` directory on the NeutrinoOS filesystem.
- **Assembly load context**: Implement a minimal `AssemblyLoadContext`
  that allows loading assemblies from the VFS and resolving their
  dependencies.
- **Entry point invocation**: When the shell executes a `.dll`, the JIT
  must locate the entry point (`Main` method) and invoke it with the
  correct arguments.
- **Test**: Compile a multi-assembly .NET 10 console application on
  Windows 11 (a main executable `.dll` and a class library `.dll` that
  the main assembly references), copy both to `/apps`, and verify that
  the shell can execute the main assembly and that it correctly calls
  into the class library.

## Task 5 — .NET 10 SDK build integration

Ensure that .NET 10 console applications can be compiled on Windows 11
and deployed to NeutrinoOS with minimal friction.

- **Project template**: Create a `templates/NeutrinoConsoleApp` directory
  containing a minimal `.csproj` and `Program.cs` that targets `net10.0`
  and uses only the BCL subset implemented in `korlib`. This template
  serves as the starting point for users writing applications for
  NeutrinoOS.
- **Build script**: Create `scripts/build-app.ps1` (PowerShell for
  Windows 11) that:
  - Takes a `.csproj` path as input.
  - Runs `dotnet build -c Release -f net10.0`.
  - Copies the output `.dll` (and any dependencies) to a staging
    directory.
  - Provides instructions for copying the staged files to the NeutrinoOS
    filesystem (either via a shared folder in QEMU/VirtualBox or by
    rebuilding the disk image with the files included).
- **Makefile integration**: Add a `make app APP=path/to/app.csproj`
  target that invokes the PowerShell script from WSL2 (via
  `powershell.exe -File scripts/build-app.ps1`) and rebuilds the disk
  image with the application included.
- **Documentation**: Update `docs/BUILD-WINDOWS.md` with a new section
  "Writing and deploying .NET 10 console applications for NeutrinoOS"
  that walks through the process from Windows 11.

## Task 6 — Test suite

Create a comprehensive test suite to validate Phase 4.

- **Test application 1 — Hello World**: A minimal .NET 10 console app
  that prints "Hello, NeutrinoOS!" and exits. Verifies basic JIT
  execution and `Console.WriteLine`.
- **Test application 2 — Interactive console**: A .NET 10 console app
  that reads a line from the console, prints it back, and loops until
  the user types "exit". Verifies `Console.ReadLine` and line editing
  through the JIT.
- **Test application 3 — File I/O**: A .NET 10 console app that creates
  a file, writes text to it, reads it back, and prints the contents.
  Verifies `System.IO` through the JIT.
- **Test application 4 — Collections and LINQ**: A .NET 10 console app
  that uses `List<T>`, `Dictionary<K,V>`, and LINQ queries (`Where`,
  `Select`, `OrderBy`, `GroupBy`) and prints the results. Verifies
  generics and LINQ through the JIT.
- **Test application 5 — Async/await**: A .NET 10 console app that uses
  `async`/`await` with `Task.Delay` and `Task.Run`. Verifies the
  minimal `System.Threading.Tasks` implementation. Document that the
  implementation is synchronous and blocks the calling thread.
- **Test application 6 — Networking**: A .NET 10 console app that uses
  `HttpClient` to fetch a URL and prints the response length. Verifies
  `System.Net` through the JIT and the NeutrinoOS TCP/IP stack.
- **Test application 7 — Multi-assembly**: A .NET 10 console app with a
  separate class library that the main assembly references. Verifies
  cross-assembly loading.
- **Test application 8 — C# 14 features**: A .NET 10 console app that
  uses field-backed properties, extension members, null-conditional
  assignment, implicit span conversions, and simple lambda parameters
  with modifiers. Verifies that the JIT handles C# 14 IL patterns.
- **Automated test runner**: Create `tests/run-phase4-tests.ps1` (for
  Windows 11) that builds all test applications, deploys them to a
  NeutrinoOS disk image, boots QEMU with serial output captured to a
  log file, executes each test application via the shell, and asserts
  that the expected output appears in the log.

## Task 7 — Documentation

- Produce `docs/PHASE4-JIT-COMPAT.md` containing the compatibility matrix
  from Task 1 and a description of the JIT changes made in Task 2.
- Produce `docs/PHASE4-BCL.md` containing a table of every `korlib` type
  implemented in Phase 4, with its supported members and any deviations
  from the official .NET BCL.
- Produce `docs/PHASE4-DESIGN.md` describing the cross-assembly loading
  architecture, the `AssemblyLoadContext` implementation, and the
  async/await limitation.
- Produce `docs/PHASE4-ACCEPTANCE.md` listing the acceptance criteria
  below and how to verify each from Windows 11.
- Produce `PHASE4-REPORT.md` summarizing changes, blockers, and
  deviations.

# CONSTRAINTS

- All code must be C# (plus the existing assembly intrinsics). Do NOT add
  C or C++ files to the kernel, bootloader, drivers, or `korlib`.
- Do NOT introduce a graphical framebuffer, GUI, mouse support, or a
  window manager. This phase is strictly console-only.
- Do NOT implement a full TCP/IP stack (that is Phase 5/6). Use the
  existing NeutrinoOS TCP/IP stack from ProtonOS. If it is incomplete,
  document the limitation and implement only the minimum needed for
  the `System.Net` tests.
- Do NOT implement a shell command parser (that is Phase 5). The shell
  should support executing a `.dll` by path (e.g., `run /apps/app.dll`)
  but parsing commands like `ls`, `cat`, `curl`, and `ssh` is Phase 5.
- Do NOT use the term "TTY" as a project name or suffix. It is fine to
  use the Unix term "tty" in device paths (`/dev/ttyS0`) and
  documentation.
- Do NOT rename the project; it is NeutrinoOS.
- Preserve the AGPL-3.0 license and attribution to ProtonOS.
- Do NOT scope-creep into Phase 5+ (shell parsing, utilities, SSH,
  curl, web hosting). If a Phase 5+ concern arises, note it in the
  "Deferred to later phases" section.
- Every public type and method added to `korlib` must have XML doc
  comments describing its Phase 4 semantics and any deviations from
  the official .NET BCL.
- All user-visible strings must say "NeutrinoOS".

# DELIVERABLES

1. An audited and enhanced Tier-0 JIT compiler that supports .NET 10
   assemblies compiled with C# 14, including field-backed properties,
   extension members, null-conditional assignment, implicit span
   conversions, and simple lambda parameters with modifiers.
2. An expanded `korlib` BCL covering `System.Console`, `System.IO`,
   `System.Collections.Generic`, `System.Linq`, `System.Threading`,
   `System.Threading.Tasks`, `System.Text`, `System`, `System.Net`,
   `System.Globalization`, and `System.Diagnostics` as specified in
   Task 3.
3. Cross-assembly loading and execution, including an
   `AssemblyLoadContext` implementation and resolution from `/apps`
   and `/lib`.
4. A `templates/NeutrinoConsoleApp` project template and
   `scripts/build-app.ps1` PowerShell script for Windows 11.
5. A comprehensive test suite (`tests/run-phase4-tests.ps1`) with eight
   test applications covering Hello World, interactive console, file
   I/O, collections and LINQ, async/await, networking, multi-assembly,
   and C# 14 features.
6. `docs/PHASE4-JIT-COMPAT.md`, `docs/PHASE4-BCL.md`,
   `docs/PHASE4-DESIGN.md`, `docs/PHASE4-ACCEPTANCE.md`, and
   `PHASE4-REPORT.md`.

# ACCEPTANCE CRITERIA

Phase 4 is complete when ALL of the following are true:

- [ ] `make image` and `make run-qemu-vga` still boot to a NeutrinoOS
      banner on both the serial and VGA consoles, with no regressions
      from Phase 3.
- [ ] A standard .NET 10 console application (compiled with
      `dotnet build -f net10.0` on Windows 11) can be copied to the
      NeutrinoOS filesystem and executed via the shell.
- [ ] "Hello, NeutrinoOS!" appears on the serial console when the Hello
      World test application is executed.
- [ ] The interactive console test application correctly reads a line
      and prints it back, with backspace editing and history working.
- [ ] The file I/O test application correctly creates, writes, reads,
      and prints a file.
- [ ] The collections and LINQ test application correctly uses
      `List<T>`, `Dictionary<K,V>`, and LINQ queries (`Where`, `Select`,
      `OrderBy`, `GroupBy`) and prints correct results.
- [ ] The async/await test application compiles and runs (even if
      `Task` is implemented synchronously). The output is correct.
- [ ] The networking test application correctly uses `HttpClient` to
      fetch a URL and prints the response length.
- [ ] The multi-assembly test application correctly loads a class
      library from `/lib` and calls into it.
- [ ] The C# 14 features test application correctly compiles and runs,
      exercising field-backed properties, extension members,
      null-conditional assignment, implicit span conversions, and
      simple lambda parameters with modifiers.
- [ ] `System.Linq.AsyncEnumerable` from .NET 10 is present in `korlib`
      (even if the implementation is minimal) so that assemblies
      referencing it load without errors.
- [ ] No C or C++ files exist in the kernel, bootloader, driver, or
      `korlib` directories.
- [ ] `docs/BUILD-WINDOWS.md` (from Phase 1) and
      `docs/PHASE2-ACCEPTANCE.md` / `docs/PHASE3-ACCEPTANCE.md` still
      work, and `docs/PHASE4-ACCEPTANCE.md` provides step-by-step
      verification for every checklist item above from a fresh
      Windows 11 machine.

# OUTPUT FORMAT

Respond in the following order:

1. **Plan** — a numbered list of concrete steps mapped to the seven
   tasks above.
2. **Repository layout** — the target directory tree after Phase 4,
   highlighting new and modified files.
3. **Code changes** — for each file to be created, modified, or
   deleted:
   - Full path
   - Action (create / modify / delete)
   - The complete new file contents (for created files) OR a unified
     diff (for modifications) OR a precise description (for deletions).
   - For large files (e.g., the full `korlib` BCL expansion, the JIT
     compiler changes, or the test applications), provide the complete
     source; do not abbreviate with "..." unless the omitted region is
     boilerplate that is explicitly described.
4. **JIT compatibility matrix** — a table listing each C# 14 feature,
   whether it is supported by the enhanced JIT, and the required
   changes.
5. **BCL implementation table** — a table listing each `korlib` type
   implemented in Phase 4, its supported members, and deviations from
   the official .NET BCL.
6. **Build and test commands** — exact WSL2 bash commands and
   PowerShell commands for Windows 11 to build, run, and verify
   Phase 4.
7. **Acceptance checklist** — reproduce the checklist above, with a
   one-line note for each item explaining how it is satisfied.
8. **Deferred to later phases** — anything that came up that belongs
   to Phase 5+ (shell parsing, utilities, SSH, curl, web hosting,
   full TCP/IP stack, full globalization, timezone support, full
   regex engine, full threading primitives).
9. **Open questions / assumptions** — anything ambiguous about the
   Phase 3 output, the existing Tier-0 JIT compiler, the existing
   `korlib` structure, or the ProtonOS conventions that you assumed,
   and how the user can verify or correct them.

If any part of the Phase 3 output is unclear, or if the existing
Tier-0 JIT compiler or `korlib` layout does not match your assumptions,
state your assumptions explicitly and proceed with a reasonable layout
consistent with a bflat-based managed kernel, noting where the user
must adjust paths.

Do not skip ahead to Phase 5–8. Scope discipline is mandatory:
Phase 4 only.