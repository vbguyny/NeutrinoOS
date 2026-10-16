# NuGet packages on NeutrinoOS (M8)

**Policy: .NET 10 or newer, nothing less.** Packages are used through
their normal `lib/net10.0` (or `lib/net10.0+`) assets. Anything whose
newest asset targets net8.0/net9.0 or lower is out of scope.

The end-to-end example is `src/apps/libapi` with its Windows twin in
`samples/nuget-compat/libapi.windows`, using two real NuGet packages:

| Package | Version | What the app uses |
|---|---|---|
| Microsoft.Extensions.ObjectPool | 10.0.13 | `DefaultObjectPoolProvider`, `DefaultObjectPool<T>`, `StringBuilderPooledObjectPolicy` |
| Microsoft.Extensions.Primitives | 10.0.13 | `StringValues` (ctor/`Count`/`ToArray`), `StringSegment` (ctor/`StartsWith`/`Equals`/`IndexOf`/`Value`) |

`bash build/m8-nuget-probe.sh` byte-compares the device responses against
the Windows twin (same `Program.cs`, same packages).

## How a package dll reaches the device

1. **Declare** — `src/apps/<app>/packages.txt`, one `PackageId [Version]`
   per line (`#` comments allowed). Example (`src/apps/libapi`):
   ```
   Microsoft.Extensions.ObjectPool 10.0.13
   Microsoft.Extensions.Primitives 10.0.13
   ```
2. **Build** — `build/p5-apps-build.sh` turns `packages.txt` into
   `PackageReference` items in the generated app project and publishes
   the app plus its package closure into `/root/appsbin`.
3. **Deploy** — `build/p5-deploy.sh` copies the app dll to
   `::/apps/webapp` and every dependency dll to **`::/lib`** (the boot
   volume's package library).
4. **Load** — the kernel resolves a missing assembly reference by name:
   `TryLoadAssemblyFromLib` reads `/lib/<name>.dll` through the kernel
   file bridge and loads it as a JIT assembly. BCL scopes are mapped to
   korlib ("virtual assemblies": `System.Runtime`, `System.Memory`,
   `System.Collections`, `System.Collections.Concurrent`,
   `System.Runtime.CompilerServices.Unsafe`, ...).
5. **Execute** — methods are JIT-compiled lazily on first call; member
   references resolve against the loaded assembly's IL metadata, the
   korlib IL metadata, or the AOT method registry for korlib types that
   are not part of the shipped korlib.dll IL.

## Device surface added for these packages

korlib / kernel additions made during M8 (all exercised by the two
packages above):

- `System.AppContext` (`TryGetSwitch`, `GetData`), `System.Resources.ResourceManager`
  (`ctor(Type)`, `GetString`) — used by generated `SR` resource helpers.
- `Exception` family: `ThrowIfNull`, `ThrowIfNullOrEmpty`,
  `ArgumentOutOfRangeException` constructors (AOT registry).
- `String.ToCharArray` (AOT registry wrapper around the AOT-compiled korlib body).
- `Span<T>`/`ReadOnlySpan<T>`: `ctor(char[])`, `ctor(char[], int, int)`,
  `Slice(int)`, `CopyTo(ReadOnlySpan<T>)` with element-size variants
  (`$i2/$i4/$i8`); `String.GetPinnableReference` bridging.
- `Unsafe.As<T>(object)` JIT intrinsic (legacy single-arg form used by
  `StringValues`); `Unsafe.As<TFrom,TTo>` already existed.
- `Array.Empty<T>` JIT intrinsic (korlib's `System.Array` is AOT-only).
- Generic `MemoryExtensions.IndexOf<T>/LastIndexOf<T>` matching the BCL
  signatures exactly (the JIT matches overloads by element types).
- `Interlocked.CompareExchange<T>`, `Volatile`, `ConcurrentQueue<T>`
  (`System.Collections.Concurrent` mapping), `Environment.ProcessorCount`,
  `Type.IsAssignableFrom` (AOT registry).
- `MemoryExtensions.AsSpan(string...)` produces spans **into the string's
  chars** (no temporary copy), so span lifetimes are tied to the string
  the caller already holds.

## Kernel JIT fixes landed for these packages (M8)

Three latent JIT bugs surfaced by real package IL (all fixed; covered by
`src/apps/spprobe`, run on device via `run /apps/webapp/spprobe.dll`):

1. **Two large struct arguments in a 3-argument call** (e.g.
   `MemoryExtensions.StartsWith(span, span, comparisonType)`): the
   argument loader assumed only arg0 was a 16-byte struct and popped
   arg1's first qword (the span's data pointer) into RDX. Fixed in
   `CompileCall` (both-args-large branch passes both by pointer).
2. **Element-strided `get_Item` selection inside generic method bodies**:
   memberrefs on `ReadOnlySpan<!0>` had no concrete element type, so the
   byte-strided default entry was picked and reads misaligned
   (`IndexOf<char>` found 'l' at byte index 4 instead of 2). Fixed by
   using the current instantiation's type-argument size
   (`MetadataIntegration` spanElemSizeVariant).
3. **`ldvirtftn` on an interface method** (building the pooled-object
   `Func<T>` from `IPooledObjectPolicy<T>.Create`): interface methods
   resolve without a native address, and the old code fell back to
   emitting `call <metadata token>` - a wild jump. Fixed: interface
   targets now resolve at runtime through `GetInterfaceMethod`, the same
   helper `callvirt` uses.

## M8.1: package-driven fixes (string.Create path, Tomlyn)

Probe apps: `src/apps/thirdprobe` (S1 `string.Create` + `SpanAction`,
S2 `StringValues.ToString()`, S2b instance-delegate joiner, S2c span
chain, S3 Tomlyn syntax) and `src/apps/humprobe` (Humanizer boundary).

- **`StringValues.ToString()` now works on device** (S2 green): the
  `string.Create<TState>(int, TState, SpanAction<char, TState>)` path
  plus the full span chain (`MemoryExtensions.AsSpan(string)`,
  `Span<char>.Slice(int)`, `ReadOnlySpan<char>.CopyTo(Span<char>)`)
  compile and execute; the joiner writes through the span
  (`joined=alpha,beta,gamma`, `match=True`).
- **Tomlyn 2.10.1 syntax parsing now works on device** (S3 green):
  `SyntaxParser.Parse` runs for key-only/string/table/full documents and
  the structure counts (`KeyValues`/`Tables`/`ChildrenCount`) are
  correct.

### M8.1 kernel/JIT fixes

1. **EH info RVA bound** (`ExceptionHandling.GetNativeAotEHInfo`):
   JIT methods registered once the CodeHeap had grown past 1MB of
   compiled code had their EH info silently dropped (the `RVA > 0x100000`
   sanity cap), so their catch clauses never matched and exceptions
   escaped to an outer frame. Cap raised to 64MB - this is why deep
   package chains "lost" their try/catch on device.
2. **Large-struct arguments in 3- and 4-argument calls** (`ILCompiler`
   `CompileCall`): only some argument positions were handled ("arg0/arg1
   large struct"). A large struct at arg2/arg3 fell into the all-small
   path and popped struct qwords straight into the argument registers -
   `this` in RCX became a struct qword (Tomlyn's `Parser.Open` loaded
   `_lexer` from a bogus `this`; `SourceSpan..ctor` wrote to [0]).
   Both shapes now use no-pop staging: the struct bytes stay on the eval
   stack and deferred LEAs point the argument registers (R8/R9 included)
   at them.
3. **Generic-method signature hashes** (`ComputeSignatureHashFromBlob`):
   the GENERIC calling-convention flag was not skipped while hashing, so
   every generic method's hash landed on the return-type marker. Fixed:
   overloads are now distinguished by their real parameter element types
   (used for the new `MemoryExtensions.AsSpan<string>(string)` /
   `AsSpan<char>(char[])` 1-arg registrations).
4. **Additional BCL surface**: `MemoryExtensions.AsSpan(string)` /
   `AsSpan(char[])` (hidden-return spans), `DateTimeOffset`,
   `ConcurrentDictionary`, `ReadOnlyDictionary`, `CollectionsMarshal`,
   `SpanAction<T, TArg>` (korlib), plus culture/date/char/math registry
   entries exercised by the packages.

### M8.2: span dispatch fixes (Tomlyn typed values)

Three device bugs in span handling made every parsed Tomlyn string
read back as `"object"` (`SyntaxParser.Parse` produced correct trees,
but `StringValueSyntax.Value` and token texts were wrong):

1. **Span MethodTables had no vtable slots**
   (`AssemblyLoader.CreateSpanTypeMethodTable`): a `constrained.`
   `callvirt Object::ToString` on a `ReadOnlySpan<char>` (Roslyn's
   shape for `span.ToString()`) looks up slot 0 on the constraint
   type's MethodTable; with zero slots the lookup fell through to the
   Object placeholder (`ObjectHelpers.ToString` -> the literal
   `"object"`). Span MTs now carry the Object-shaped slots
   (ToString/Equals/GetHashCode) with a real
   `SpanHelpers.ToStringUtf16` bridge in slot 0.
2. **Span TypeSpecs resolved through the generic-instantiation factory**
   (`AssemblyLoader.ResolveTypeSpec`): `ReadOnlySpan<T>` has no TypeDef
   anywhere, so the factory built a minimal MT with the placeholder in
   slot 0 - the constrained call then direct-called it. Span TypeSpecs
   now route to the single well-known span MT.
3. **`Span<T>.ToString` had no AOT entry**: added the
   `SpanHelpers.ToStringUtf16` bridge plus `ToString` registrations for
   `System.Span`1`/`System.ReadOnlySpan`1`. The string-range
   `AsSpan(string, int, int)` overload also got its signature-hashed
   registration so string calls stop binding to the char[]-range
   bridge (which had shifted span data two characters into the
   buffer).

`src/apps/thirdprobe` gained S2d/S2e (span-string/BCL isolation), S3x
(typed value getters) and S3y (BCL mimics) sections pinning these
paths.

### Remaining boundaries

- **Tomlyn deeper model access**: string, key, boolean and date/time
  syntax nodes now return correct values on device (`S3: stringvalue=`
  prints the parsed string). Still open: `IntegerValueSyntax.Value` /
  `FloatValueSyntax.Value` read 0 (the lexer's digit path fails inside
  `ReadNumberOrDate` - a crowded-frame codegen issue; the identical
  algorithm works in isolated device tests), negative numbers and
  floats throw during parse, and `SyntaxNode.ToString()`
  (the `WriteTo`/`GetChild` walk) is not re-tested yet.
- **Humanizer.Core 3.0.10** (net10.0 asset exists) still needs `Regex`
  (11 member refs), `Immutable`, and culture data - beyond the current
  surface; `src/apps/humprobe` tracks the boundary (its `Main` currently
  fails JIT compilation on the missing surface).
- Packages with assets only for net8.0 or below (Sqids, Pidgin, Cronos,
  ZString, Semver, ...) are excluded by policy.

## Cold-start behavior

The device JIT compiles methods on first call. The first HTTP request
against an endpoint therefore spends seconds-to-minutes compiling the
Kestrel request pipeline parts it needs (under QEMU TCG; hardware is far
faster). The probe script warms `/health` first and then uses generous
per-request timeouts; subsequent requests are served from compiled code.
