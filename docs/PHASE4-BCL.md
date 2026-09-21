# PHASE4-BCL.md - korlib additions and semantics (Phase 4)

korlib is the NeutrinoOS IL BCL: the same sources compile into the kernel
(AOT, authoritative semantics) and into `korlib.dll` (loaded by the JIT).
JIT-declared bridge primitives (`#if KORLIB_IL` stubs) are bound by method
token to kernel `[UnmanagedCallersOnly]` exports.

This document covers the Phase 4 additions (System.Linq, System.IO) and
summarizes the previously implemented surface the test applications rely
on. Deviations from the official .NET BCL are noted per type.

## New in Phase 4: `System.Linq`

`System.Linq.Enumerable` - LINQ to Objects subset in
`src/korlib/System/Linq/Enumerable.cs`. Deferred execution is implemented
with hand-written enumerator classes (see PHASE4-JIT-COMPAT.md for why
`yield return` is not usable in this codebase).

| Operator | Notes |
|----------|-------|
| `Where`, `Select`, `SelectMany` | deferred; predicate/selector delegates called per element |
| `First`, `FirstOrDefault`, `Last`, `LastOrDefault`, `Single`, `SingleOrDefault`, `ElementAt`, `ElementAtOrDefault` | eager, standard exceptions |
| `Any`, `All`, `Count` (+ predicate), `Contains` | standard |
| `Sum`, `Min`, `Max`, `Average` | `int`/`long`/`double` (Sum: int/long/double; Min/Max: int, double and generic with `IComparer<T>`; Average: int, double) |
| `ToList`, `ToArray`, `ToDictionary` | eager |
| `OrderBy`, `OrderByDescending`, `ThenBy`, `ThenByDescending` | **stable** insertion sort over a buffered copy; multi-key chaining via `IOrderedEnumerable<T>`; O(n^2) - intended for console-scale sequences |
| `GroupBy` | buffers into a `Dictionary` + first-seen key order; `IGrouping<TKey,TElement>` and `IOrderedEnumerable<TElement>` interfaces provided. **Deviation:** `TKey : notnull` constraint (matches korlib's `Dictionary`). |
| `Join` | inner join via hash lookup on the built inner side. **Deviation:** `TKey : notnull`. |
| `Distinct`, `Take`, `Skip`, `Concat`, `Reverse`, `Cast`, `OfType`, `Empty`, `Range`, `Repeat` | deferred (Reverse/Cast/OfType buffer or adapt) |

Not implemented (deferred): indexed overloads (`Func<T,int,...>`),
`IQueryable`/expression trees, parallel LINQ, most `Enumerable.*OrDefault`
predicated variants beyond the listed set, `System.Linq.AsyncEnumerable`
(pending - see PHASE4-REPORT.md).

## New in Phase 4: `System.IO`

All paths address the boot (FAT32) volume through the kernel file bridge
(`FileBootRead`/`FileBootWrite`/`FileBootSize`/`FileBootExists`/
`FileBootDelete` + `DirBoot*`, backed by the JIT-loaded AHCI/FAT driver;
see PHASE4-DESIGN.md). `/` separates directories; a leading `/` is
optional. File names must be FAT 8.3-compatible for now.

| Type | Members | Deviations |
|------|---------|------------|
| `File` | `Exists`, `Delete`, `ReadAllBytes`, `ReadAllText` (+encoding), `ReadAllLines` (+encoding), `WriteAllBytes`, `WriteAllText` (+encoding), `WriteAllLines`, `AppendAllText` (+encoding), `Copy` (+overwrite), `Move`, `Open`, `OpenRead`, `OpenWrite`, `Create` | `Move` = copy + delete (no rename across the bridge yet); no attributes/ACLs/timestamps/locking; UTF-8 (no BOM) default |
| `Directory` | `Exists`, `CreateDirectory` (returns void, single level), `Delete` (non-recursive), `GetFiles`, `GetDirectories`, `GetFileSystemEntries`, `GetCurrentDirectory` (always `/`), `SetCurrentDirectory` (only `/`) | reduced return types; no wildcards/recursion/EnumerationOptions; enumeration returns FAT short (8.3, upper-case) names; no process working directory |
| `Path` | `Combine`, `GetFileName`, `GetDirectoryName`, `GetExtension`, `GetFileNameWithoutExtension`, `ChangeExtension`, `HasExtension`, `IsPathRooted`, `GetFullPath` (identity), `GetPathRoot`, separator constants | no drive letters; no normalization of `.`/`..` |
| `FileStream` | `FileMode`/`FileAccess`/`FileShare` enums; open/read/write/seek/setLength/flush | **whole-file buffering**: contents are read into memory on open and written back as a single truncated rewrite on Flush/Close (the bridge transfers whole files). Correct for console-scale use; no concurrent-writer semantics. |
| `MemoryStream` | standard growable buffer (`Capacity`, `ToArray`, `WriteTo`, ...) | no `GetBuffer`/span APIs |
| `StreamReader` / `StreamWriter` | `ReadLine`/`ReadToEnd`/`Peek`/`Read`; `Write`/`WriteLine` (+`AutoFlush`), `StreamWriter(string [,append])` | decode-on-first-read; no async/detect-encoding |
| `IOException`, `FileNotFoundException`, `DirectoryNotFoundException`, `EndOfStreamException` | message-carrying constructors | reduced hierarchy surface |
| `Stream`, `TextReader`, `TextWriter` (extended) | added `Stream.Close`, `TextReader.Read(char[],int,int)` | - |

## Previously implemented surface (Phases 2-3 + JIT work, summarized)

- `System.Console` (buffered writer, line discipline input, colors), `ConsoleColor`, `ConsoleKey*`,
  `System.Text.Encoding`/`UTF8Encoding`/`StringBuilder`,
  `System.String` (IndexOf/Replace/Split/Join/Format/Trim/...), `Math`, `DateTime`, `TimeSpan`,
  `Guid`, `BitConverter`, `Half`, `Version`, `Nullable<T>`, `ValueTuple`, `Index`/`Range`,
  `Environment`, `GC`, all core interfaces.
- `System.Collections.Generic` (List, Dictionary, HashSet, Queue, Stack, LinkedList, SortedList, comparers),
  `System.Collections` interfaces.
- `System.Threading` (Thread, Monitor, Interlocked, CancellationToken[Source]),
  `System.Threading.Tasks` (Task/Task<T>, Task.Run/Delay/Wait/Result/WhenAll, TaskCompletionSource, ValueTask) -
  **synchronous implementation** (runs continuations inline on the calling thread).
- `System.Runtime.CompilerServices` (DefaultInterpolatedStringHandler, AsyncMethodBuilder, TaskAwaiter, ...).
- `System.Reflection` core, `System.Globalization.CultureInfo` (invariant).

## Pending BCL items (not implemented in this drop)

`System.Random`, `System.Convert`, `System.Diagnostics` (Stopwatch/Debug),
`System.Text.RegularExpressions`, `System.Net` (`IPAddress`, `HttpClient`
facade - the `ProtonOS.Net` library provides the real client today),
`System` `Tuple<T1..>` (non-ValueTuple), `System.Linq.AsyncEnumerable`
(+ `IAsyncEnumerable<T>`/`IAsyncEnumerator<T>`), `Span<T>`/`ReadOnlySpan<T>`
in the IL BCL. Tracked in PHASE4-REPORT.md.
