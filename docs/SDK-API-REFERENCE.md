# SDK — API Reference

This page is derived from the XML doc comments in the source tree
(`GenerateDocumentationFile` is enabled for `NeutrinoOS.Packaging`; the
DDK and driver abstractions carry XML docs as well). It covers the three
surfaces a NeutrinoOS developer touches:

1. **korlib / BCL subset** — what application code compiles against
2. **Driver abstractions** (`NeutrinoOS.Driver.Abstractions.dll`)
3. **Packaging APIs** (`NeutrinoOS.Packaging.dll`) + host tool CLIs

---

## 1. Applications: korlib / BCL subset

Classic console apps compile against the normal .NET 10 BCL; the kernel
redirects `System.*` to korlib at load time. The implemented subset and
IL-pattern support are documented in:

* `docs/PHASE4-BCL.md` — implemented BCL surface
* `docs/PHASE4-JIT-COMPAT.md` — JIT-compatible IL patterns
* `docs/KORLIB_PLAN.md` — roadmap for the remaining surface

Practically available today (higher-level highlights):

| Area | Types |
|---|---|
| Console | `Console.WriteLine/Write/ReadLine`, `Console.Error`, colors |
| Strings | `String` (full), `StringBuilder`, `Encoding.UTF8` |
| Core | `Array`, `List<T>`, `Dictionary<K,V>`, `HashSet<T>`, `Queue/Stack<T>`, `KeyValuePair`, `Nullable`, `Math`, `BitConverter`, `Guid`, `Half` |
| LINQ | `Enumerable` (core operators) |
| Collections | `IEnumerable/IEnumerator`, comparers, `Span/ReadOnlySpan` (partial) |
| IO | `File`, `Directory`, `Path`, `FileStream`, `StreamReader/Writer`, `MemoryStream`, `StringWriter` |
| Threading | `Thread`, `Interlocked`, `Monitor`, `CancellationToken`, `Task`/`ValueTask` (JIT-side) |
| Runtime | `Environment` (`TickCount64`, vars), `GC`, `Version`, `TimeSpan`, `DateTime`, `Stopwatch` |
| Reflection | `Type` (partial), `Assembly` (partial) |

## 2. Driver abstractions

`namespace NeutrinoOS.Drivers` (assembly `NeutrinoOS.Driver.Abstractions.dll`,
provided by the OS at load time).

```csharp
public interface IDriver
{
    string Name { get; }
    string Version { get; }
    int AbiMajor { get; }          // DriverAbi.Major
    int AbiMinor { get; }          // DriverAbi.Minor
    void Initialize(IDriverServices services);
    bool Match(DeviceInfo device);     // cheap, no hardware access
    bool Probe(DeviceInfo device);     // validate variant/resources
    bool Start(DeviceInfo device);     // map resources, bring up
    void Stop(DeviceInfo device);      // tolerate partial Start
}
```

Driver packages are instantiated through a **static parameterless
factory**: `public static IDriver Create()` in the driver assembly.

```csharp
public interface IDriverServices
{
    ulong MapMmio(ulong physicalAddress, ulong size);
    void  UnmapMmio(ulong virtualAddress, ulong size);
    ulong AllocateDma(ulong size, ulong alignment);
    void  FreeDma(ulong physicalAddress, ulong size);
    bool  RegisterInterrupt(int irq, DriverInterruptCallback handler);
    void  UnregisterInterrupt(int irq);
    string CreateDeviceNode(string name, int major, int minor);
    bool  RemoveDeviceNode(string name);
    void  Log(DriverLogLevel level, string message);
}
```

Device model (`DeviceInfo`): `Bus` (`"pci"`, `"virtio"`, `"platform"`),
`Address`, `VendorId`/`DeviceId` (ushort), `Class`/`ClassCode`
(`DeviceClass` enum), `Path`, `TryGetResource(DeviceResourceKind, out
DeviceResource)` (kinds: `Mmio`, `Irq`, `Dma`, ...; resource carries
`Base`/`Length`). `DriverAbi` current version: **1.0**
(`DriverAbi.Major == 1`). `DriverLogLevel`: `Debug/Info/Warn/Error`.

Lifecycle, matching rules and packaged-driver loading:
`docs/PHASE8-DRIVER.md`.

## 3. Packaging APIs (`NeutrinoOS.Packaging.dll`)

Shared byte-for-byte between the host tools, the repo server and the
on-device `npkg` (the in-tree tools compile these sources directly).

```csharp
NpkgPackage.Open(byte[] data)             // parse a .npkg
NpkgPackage.Build(NpkgManifest manifest,
                  KeyValuePair<string,byte[]>[] files,
                  byte[] signingSeed)     // -> serialized .npkg bytes
pkg.PayloadFiles                          // file name -> bytes
pkg.VerifyChecksums(out string badPath)
pkg.VerifySignature(byte[] publicKey)     // Ed25519 over manifest+checksums
NpkgPackage.Sha256Hex(byte[] data)
NpkgPackage.Fingerprint(byte[] publicKey) // 64-hex signer fingerprint
```

```csharp
// Manifest (manifest.json, format "npkg/1")
NpkgManifest {
    Format, Name, SemVersion Version, Architecture,
    Author, Description, License, Homepage, Signer,
    InstallPath, List<string> Provides,
    Dictionary<string,string> Dependencies,
    Dictionary<string,string> EntryPoints,
    Dictionary<string,string> Scripts,
    NpkgDriverInfo Driver   // class, vendorIds, deviceIds, entryPoint
}
NpkgManifest.FromJson/ToJson(...)
```

```csharp
// Repository index (repository.json, format "npkg-repo/1")
RepositoryIndex { Format, Revision, Generated, Name, RepoPackageList Packages }
RepoPackageInfo { Name, SemVersion Version, Architecture, Description,
                  Filename, Size, Sha256, Signer, Dependencies, Provides }
```

Support types: `SemVersion` (`Parse`/`TryParse`, comparison),
`VersionConstraint` (dependency constraints), `Json` (`Parse`/`Write`),
`TextConv` (`HexEncode/HexDecode/LongToString`), `Zip`/`Inflate` (the
container codec). Ed25519 signing lives in the DDK
(`ProtonOS.DDK.Crypto.Ed25519`); container format details are in
`docs/PHASE8-NPKG.md`.

## 4. Host tool CLIs

### `npkg-host`

```
npkg-host keygen [--seed <64-hex>] [--out-dir <dir>] [--force]
npkg-host fingerprint <pubkey-file>
npkg-host pack --manifest <manifest.json> --payload-dir <dir> --out <f.npkg> [--key <k>]
npkg-host pack --name <n> --version <v> [--entry <app.dll>|--files <a,b>]
               [--install-path apps|bin|drivers|lib] [--architecture any|x86-64|arm64]
               [--provides <a,b>] [--description|--license|--author|--homepage <s>]
               --out <f.npkg> [--key <k>]
npkg-host sign   <file.npkg> <private.key>
npkg-host verify <file.npkg> [--key <pubkey-file>]
npkg-host list   <file.npkg>
npkg-host repo-index --dir <dir> [--key <private.key>] [--name <name>]
npkg-host publish    <file.npkg> --repo <dir>
```

### `npkg-repo-server`

```
npkg-repo-server --dir <repo-dir> [--port 8080] [--bind 0.0.0.0]
                 [--key <private.key>] [--name <repo-name>] [--quiet]
```

(`scripts\start-repo-server.ps1` wraps this on Windows.)

### On-device `npkg`

`repo add/remove/list`, `update`, `search`, `info`, `install [--allow-
untrusted]`, `remove`, `upgrade`, `verify`, `list`. Journal:
`/var/lib/npkg/journal.jsonl`.
