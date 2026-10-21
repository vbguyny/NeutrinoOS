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

### M8.3: number-path fixes (Tomlyn ints, negatives, span args)

`S3x` sweeps for integers and negative numbers are now green
(`int=3231`, `intneg=-5`, both with zero diagnostics), and the block
above shrinks to one korlib boundary. Root causes fixed, in order of
discovery:

1. **Branch table overflow (the big one)** - `ILCompiler.MaxBranches`
   was 64, and `RecordBranch` silently dropped every branch past the
   cap. Tomlyn's `Lexer.ReadNumberOrDate` (0x928 IL bytes, 67 branches)
   had its last three forward branches - the exact skip-jumps over the
   `Unable to parse integer` / `Unexpected leading zero` error blocks -
   emitted with an unpatched `rel32` of 0, i.e. unconditional fall
   through. Every integer parse therefore produced BOTH contradictory
   diagnostics and stored 0. The cap is now 512 (labels 2048) and
   overflow prints a loud `[JIT] ERROR: branch table full`.
2. **`Nullable<char32>` argument ABI mismatch** - `CompileNewobj`'s
   `Nullable<T>` sizing heuristic shrunk every MT whose raw size read
   16 down to 8 (correct for `Nullable<int>`, wrong for
   `Nullable<char32>`: this runtime pads `char32` to 8 bytes, so the
   ctor stores `_value` at +8 and the true struct is 16 bytes). The
   caller then allocated an 8-byte temp, the ctor's +8 write overflowed
   it, and the truncated value reached `ReadNumberOrDate` where `ldarga`
   dereferenced it as a pointer - the `-5` parse page faulted in
   `Nullable`1::get_HasValue`. The heuristic now only shrinks when the
   ctor argument's actual stack size is <= 4 bytes.
3. **Span argument sizing** - the open `Span`1`/`ReadOnlySpan`1`
   generic definitions report a `BaseSize` of 32 via the signature
   parser; span-typed arguments were therefore classified as 32-byte
   structs. Spans are always 16 bytes (byref data + length); the
   signature sizer now detects the synthetic span MethodTables by
   pointer and pins them to 16.
4. **`callvirt` 4-arg marshaling truncated large arg2/arg3** -
   `CompileCallvirt`'s `totalArgs == 4` path popped `arg2` straight
   into R8, so `StringBuilder.CopyTo(int, Span<char>, int)` received
   the span's first qword as a pointer and a stale value in RCX as
   `this` (fault reading `[null+0x10]`). The branch now uses the same
   no-pop staging as `CompileCall`: small args load by offset, large
   struct bytes stay on the eval stack and get their addresses via
   deferred LEAs in the shadow-allocation block.

`src/apps/thirdprobe` sweeps: `int`, `int42`, `intneg`, `bool`, `date`,
`float` and `float-e` all print correct values with `diag=0`; the older
`S3:`/`S3x` typed getter checks stay green. The `S3x-fmt-*` probes build
fresh `FloatValueSyntax` nodes from values, so they exercise the device
float formatter directly (`3.25`, `1000.0`, `0.1`, `1.5`), and the
`S3z-*` probes cover the three `ToString` shapes (no-arg, `"R"` +
provider from a local, and Tomlyn's static helper).

### Resolved: float formatting

The float boundary recorded earlier (parsed floats reaching
`double.TryParse` but the parse tree construction page-faulting with
cr2=0x278) turned out to be two separate defects, both closed:

1. **`call` 4-arg marshaling with a large arg0.** `CompileCall`'s
   `totalArgs == 4` path only special-cased a large *arg1*, so
   `double.TryParse(ReadOnlySpan<char>, NumberStyles, IFormatProvider,
   out double)` popped the span descriptor's first qword (its `char*`
   data pointer) into RCX and left eight bytes unpopped. The bridge then
   read `*(char**)text` and faulted - the cr2=0x278 halt was the
   secondary exception-machinery fault. The branch now has a dedicated
   arg0-large case and the no-pop staging condition covers all four
   argument positions.
2. **No device double/single formatter.** korlib's `Double.ToString()`
   and `Single.ToString()` return the placeholder literals `"Double"`/
   `"Single"`, so `TomlFormatHelper.ToString(double)` ->
   `double.ToString("R", InvariantCulture)` had nothing to call.
   `src/kernel/Runtime/FloatFormatting.cs` now does the conversion: it
   expands the IEEE-754 value exactly with a small fixed-size bignum
   (64 x 32-bit limbs), divides out decimal digits, and accepts the
   shortest digit string whose value still rounds back to the original
   double (17 significant digits) or float (9) - i.e. `"R"`/`"G17"`/
   `"G9"` semantics. Rounding is nearest with ties-to-even, the rounding
   interval accounts for the half-width gap below a binade boundary, and
   both neighbouring candidates are tried per digit count because the
   asymmetric interval can admit the farther neighbour. Notation matches
   .NET: fixed point for `-4 <= exp < 17` (double) or `< 9` (single),
   scientific outside. `AotMethodRegistry` wires it into
   `Double/Single.ToString()`, `ToString(string, IFormatProvider)` and
   the `ArgCount == 0` byref variants.
3. **`newobj` dropped float constructor arguments.** A JIT'd prologue
   homes a float parameter from `XMM<slot>` (see
   `X64Emitter.HomeArgumentsWithFloats`), but `CompileNewobj` only
   copied constructor arguments into the integer registers, so
   `new FloatValueSyntax(3.25)` formatted stale `XMM` garbage (`6E+08`)
   for every input. The constructor call path now emits the matching
   `movd`/`movq` into `XMM(i+1)`.

`FloatFormatting` was validated off-device against .NET's own
`ToString("R")` over 15.8 million values (random bit patterns across
both ranges, every power of two and ten with neighbours, binade
boundaries, subnormals, and human-scale decimals) with zero mismatches.

### Remaining boundaries

- **Humanizer.Core 3.0.10** (net10.0 asset exists). The BCL surface it
  needs is now largely in place - see "Humanizer status" below - but its
  `Main` still fails JIT compilation, so `src/apps/humprobe` remains the
  tracker for this boundary.
- Packages with assets only for net8.0 or below (Sqids, Pidgin, Cronos,
  ZString, Semver, ...) are excluded by policy.

### Humanizer status

Humanizer 3.0.10 gets all of its regexes from the `[GeneratedRegex]`
source generator, which emits into the *consumer* assembly subclasses of
`Regex` that poke five of its fields (`pattern`, `roptions`,
`internalMatchTimeout`, `factory`, `capsize`), call `Regex()` and
`Regex.ValidateMatchTimeout`, and construct a `RegexRunnerFactory`
subclass. That shape is the ABI korlib's implementation is built to.

Landed for this boundary:

- **`System.Text.RegularExpressions`** (`src/korlib/System/Text/RegularExpressions/`).
  `Regex` declares those five fields as public (the generated code must
  resolve them by name), and matching is done by `RegexEngine` - a
  backtracking matcher with continuation passing over a parsed node tree -
  rather than by the generated `RegexRunner`, which is why `factory` is
  stored and ignored. Supports literals, `.`, classes with ranges and
  negation, `\d \D \w \W \s \S \b \B`, `\p{Lu} \p{Ll} \p{Lo} \p{L} \p{N}`,
  the common escapes, `* + ? {n,m}` with lazy forms, capturing and `(?:)`
  groups, `(?=)`/`(?!)` lookahead, alternation and `^ $`. Lookbehind,
  backreferences, atomic groups and the RightToLeft/NonBacktracking modes
  throw `NotSupportedException` naming the construct. `Match`, `Group`,
  `Capture`, the collections, `MatchEvaluator`, `Replace` (with `$1`/`$&`
  expansion) and `Split` are implemented; `RegexRunner`/`RegexRunnerFactory`
  exist so generated subclasses load.
  Validated off-device against .NET's `Regex` for all nine Humanizer
  patterns across ~70 inputs (`IsMatch`, `Matches`, group captures,
  `Replace` with a string and with a `MatchEvaluator`): zero mismatches.
- **`System.Collections.Frozen`** (`FrozenCollections.cs`). Humanizer's
  `LocaliserRegistry<T>` materialises its converters with
  `ToFrozenDictionary(...)` and `EnumCache<T>` with `ToFrozenSet()`; these
  are immutable views over `Dictionary<TKey,TValue>`/`HashSet<T>`.
- **Globalization data.** `NumberFormatInfo` (invariant separators/signs),
  `CompareInfo`/`CompareOptions`, `TextInfo`, and the `CultureInfo` members
  Humanizer reads: `TwoLetterISOLanguageName`, `Parent`, `NumberFormat`,
  `CompareInfo`, `TextInfo`, `EnglishName`/`DisplayName`/`NativeName`,
  `IsNeutralCulture`.
- **`DateOnly` / `TimeOnly`**, referenced at static init by Humanizer's
  date/time converter registries.
- **`System.Buffers.SearchValues`**, used by the generated regex runners.
- **Assembly redirects** (`AssemblyLoader.IsVirtualAssembly`) for
  `System.Collections.Immutable` (where `Frozen*` lives in the BCL),
  `System.Text.RegularExpressions` and `System.ComponentModel.Annotations`
  (`DisplayAttribute`), so their TypeRefs resolve into korlib.
- **`AotMethodRegistry`** entries for `Char.IsUpper/IsLower/IsLetter/
  IsLetterOrDigit/IsWhiteSpace/ToUpper/ToLower`. `Humanize()` starts with
  `Enumerable.All(input, Char.IsUpper)`, which compiles to
  `ldftn Char::IsUpper` - that needs a registry entry to yield a code
  pointer, otherwise the whole method fails to compile.

What is left, in the order the failures appear on device:

1. A handful of member refs that still do not resolve, all reached from
   Humanizer's `Configurator..cctor` registry chain: an unnamed member on a
   generic instantiation (`0x0A0001ED`, `0x0A0001F0`), a field
   (`0x0A000056`), `op_Implicit` (`0x0A0000EE`) and `Regex.IsMatch`
   (`0x0A00005C`). Because `Configurator..cctor` constructs every converter
   registry up front, these block `NumberToWordsExtension.ToWords` before
   any converter runs.
2. Humanizer's own method bodies that follow (`0x0600044C`/`0x0600048B`/
   `0x060004C4` and friends) - they fail only because of (1); each attempt
   surfaces the next unresolved reference.

The efficient way to continue is to keep using the device log as the
driver: every failure prints the exact token, `build/iltool` (or any
metadata dumper) resolves it to a name, and the fix is either a korlib type
member or an `AotMethodRegistry` entry.

## Cold-start behavior

The device JIT compiles methods on first call. The first HTTP request
against an endpoint therefore spends seconds-to-minutes compiling the
Kestrel request pipeline parts it needs (under QEMU TCG; hardware is far
faster). The probe script warms `/health` first and then uses generous
per-request timeouts; subsequent requests are served from compiled code.
