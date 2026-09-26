// NeutrinoOS kernel - Managed kernel entry point
// EfiEntry (native.asm) saves UEFI params, then calls korlib's EfiMain, which calls Main()

using System.Runtime.InteropServices;
using ProtonOS.Arch;
using ProtonOS.PAL;
using ProtonOS.Memory;
using ProtonOS.Threading;
using ProtonOS.Platform;
using ProtonOS.Runtime;
using ProtonOS.Runtime.JIT;

namespace ProtonOS;

public static unsafe class Kernel
{
    // Loaded assembly binaries (persist after ExitBootServices)
    // Note: PE bytes are loaded via UEFI before ExitBootServices,
    // then registered with AssemblyLoader after HeapAllocator.Init
    private static byte* _testAssemblyBytes;
    private static ulong _testAssemblySize;
    private static byte* _testSupportBytes;
    private static ulong _testSupportSize;
    private static byte* _ddkBytes;
    private static ulong _ddkSize;

    // Driver assembly data
    private static byte* _virtioDriverBytes;
    private static ulong _virtioDriverSize;
    private static byte* _virtioBlkDriverBytes;
    private static ulong _virtioBlkDriverSize;
    private static byte* _fatDriverBytes;
    private static ulong _fatDriverSize;
    private static byte* _ext2DriverBytes;
    private static ulong _ext2DriverSize;
    private static byte* _ahciDriverBytes;
    private static ulong _ahciDriverSize;
    private static byte* _virtioNetDriverBytes;
    private static ulong _virtioNetDriverSize;

    // korlib IL assembly (for JIT generic instantiation and token-based AOT lookup)
    private static byte* _korlibBytes;
    private static ulong _korlibSize;

    // ProtonOS.Net library (application-level networking)
    private static byte* _protonOsNetBytes;
    private static ulong _protonOsNetSize;

    // AppTest assembly (application-level tests)
    private static byte* _appTestBytes;
    private static ulong _appTestSize;

    // console_io_test.dll (Phase 2 interactive console I/O acceptance test)
    private static byte* _consoleIoTestBytes;
    private static ulong _consoleIoTestSize;

    // vga_test.dll (Phase 3 VGA text console acceptance test)
    private static byte* _vgaTestBytes;
    private static ulong _vgaTestSize;

    // keyboard_test.dll (Phase 3 PS/2 keyboard verification test)
    private static byte* _keyboardTestBytes;
    private static ulong _keyboardTestSize;

    // JITTest assembly (comprehensive IL opcode testing)
    private static byte* _jitTestBytes;
    private static ulong _jitTestSize;

    // Assembly IDs from AssemblyLoader (assigned after registration)
    private static uint _testAssemblyId;
    private static uint _testSupportId;
    private static uint _ddkId;

    /// <summary>The loaded ProtonOS.DDK assembly id (Phase 6 services).</summary>
    internal static uint DdkAssemblyId => _ddkId;
    private static uint _virtioDriverId;
    private static uint _virtioBlkDriverId;
    private static uint _fatDriverId;
    private static uint _ext2DriverId;
    private static uint _ahciDriverId;
    private static uint _virtioNetDriverId;
    private static uint _korlibId;
    private static uint _protonOsNetId;

    /// <summary>
    /// Assembly ID of the AHCI driver, used by Platform.AssemblyRunner to
    /// read files (e.g. /apps/*.dll) from the boot volume at runtime.
    /// </summary>
    public static uint AhciDriverAssemblyId => _ahciDriverId;

    /// <summary>
    /// Assembly ID of TestSupport, used by Platform.AssemblyRunner for
    /// the JIT-side ShellRunSupport.InvokeMain helper (managed argument
    /// materialization and entry-point invocation).
    /// </summary>
    public static uint TestSupportAssemblyId => _testSupportId;
    private static uint _appTestId;
    private static uint _jitTestId;
    private static uint _consoleIoTestId;
    private static uint _vgaTestId;
    private static uint _keyboardTestId;

    // Cached MetadataRoot for the test assembly (for string resolution)
    // TODO: Migrate to use LoadedAssembly.Metadata instead
    private static Runtime.MetadataRoot _testMetadataRoot;

    // Cached TablesHeader and TableSizes for the test assembly (for token resolution)
    // TODO: Migrate to use LoadedAssembly.Tables/Sizes instead
    private static Runtime.TablesHeader _testTablesHeader;
    private static Runtime.TableSizes _testTableSizes;

    /// <summary>
    /// Get a pointer to the test assembly's MetadataRoot.
    /// Returns null if no assembly has been loaded/parsed.
    /// </summary>
    public static Runtime.MetadataRoot* GetTestMetadataRoot()
    {
        // Static fields have fixed addresses in the managed heap
        // This is safe because _testMetadataRoot is a static field
        fixed (Runtime.MetadataRoot* ptr = &_testMetadataRoot)
        {
            return ptr;
        }
    }

    /// <summary>
    /// Get a pointer to the test assembly's TablesHeader.
    /// </summary>
    public static Runtime.TablesHeader* GetTestTablesHeader()
    {
        fixed (Runtime.TablesHeader* ptr = &_testTablesHeader)
        {
            return ptr;
        }
    }

    /// <summary>
    /// Get a pointer to the test assembly's TableSizes.
    /// </summary>
    public static Runtime.TableSizes* GetTestTableSizes()
    {
        fixed (Runtime.TableSizes* ptr = &_testTableSizes)
        {
            return ptr;
        }
    }

    public static void Main()
    {
        DebugConsole.Init();
        DebugConsole.WriteLine();
        DebugConsole.WriteLine("==============================");
        DebugConsole.WriteLine("  " + Exports.DDK.SystemInfoExports.VersionBanner);
        DebugConsole.WriteLine("==============================");
        DebugConsole.WriteLine("[CONSOLE] Serial console initialized (ttyS0 @ 115200 8N1)");
        DebugConsole.WriteLine();

#if ARCH_ARM64
        // ARM64 boots single-stage as its own UEFI application: build the
        // BootInfo the x64 loader would otherwise provide (memory map,
        // kernel image range, RSDP) from the live UEFI services.
        Arm64BootSetup.EnsureBootInfo();
#endif

        // Verify BootInfo from bootloader is available and valid
        var bootInfo = BootInfoAccess.Get();
        if (bootInfo == null || !bootInfo->IsValid)
        {
            DebugConsole.WriteLine("[BootInfo] ERROR: BootInfo not available or invalid!");
            for (;;) { }  // Halt
        }
        DebugConsole.Write("[BootInfo] Valid BootInfo at 0x");
        DebugConsole.WriteHex((ulong)bootInfo);
        DebugConsole.Write(" files=");
        DebugConsole.WriteDecimal(bootInfo->LoadedFilesCount);
        DebugConsole.WriteLine();

        // Verbose JIT tracing emits megabytes of serial output, which is
        // very slow on hypervisors that trap every serial byte (e.g.
        // VirtualBox under NEM). Enable it only via the verbose-jit marker.
        JitDiag.VerboseJit = BootInfoAccess.FindFile("verbose-jit", out ulong _verboseJitSize) != null;
        if (JitDiag.VerboseJit)
        {
            DebugConsole.WriteLine("[Kernel] Verbose JIT tracing enabled (verbose-jit marker)");
        }

        // Dump memory map to analyze fragmentation
        DumpMemoryMap(bootInfo);

        // Initialize ReadyToRun info (must be before anything needing runtime metadata)
        ReadyToRunInfo.Init();
        // ReadyToRunInfo.DumpSections();

        // Test GCDesc parsing with frozen objects
        // GCDescHelper.TestWithFrozenObjects();

        // Get pointers to pre-loaded assemblies from BootInfo (bootloader loaded them)
        LoadTestAssembly();

        // Initialize page allocator (uses BootInfo memory map)
        PageAllocator.Init();

        // Initialize ACPI (uses BootInfo RSDP)
        ACPI.Init();

        // Initialize architecture-specific code (GDT, IDT, virtual memory)
        CurrentArch.InitStage1();

        // Initialize kernel heap
        HeapAllocator.Init();

        // Initialize GC heap (managed object heap with proper object headers)
        GCHeap.Init();

        // Initialize static GC fields (must be after GC heap, before using any static object fields)
        InitializeStatics.Init();

        // Initialize garbage collector (must be after GCHeap and PageAllocator)
        GC.Init();

        // Initialize code heap for JIT (must be after VirtualMemory)
        CodeHeap.Init();

        // Test GCDesc with heap-allocated object that has references
        // GCDescHelper.TestWithHeapObject();

        // Initialize scheduler (creates boot thread)
        Scheduler.Init();

        // Initialize PAL subsystems
        TLS.Init();
        PAL.Memory.Init();

        // Initialize syscall infrastructure
        Syscall.SyscallDispatch.Init();
        Syscall.SyscallHandler.Init();

        // Initialize process subsystem
        Memory.CopyOnWrite.Init();
        Process.ProcessTable.Init();

        // Initialize VFS
        IO.VFS.Init();

        // Run Ring 3 test to verify user mode works (HALTS CPU after)
        // Syscall.Ring3Test.Run();

        // Second-stage arch init (timers, enable interrupts)
        CurrentArch.InitStage2();

        // Early VGA text console: bring the screen up before the long boot
        // and mirror the boot log to it, so a manual QEMU/VirtualBox boot
        // is visible in the VM window. Skipped when the console-vga-off
        // marker is present (automated serial-only test runs).
#if !ARCH_ARM64
        ConsoleAbstractionLayer.EarlyInitVgaConsole();
#endif

        // Boot progress timing starts here (the HPET is available now)
        BootLog.Status("Arch initialized (timers + interrupts)");

        // Initialize String MethodTable for JIT ldstr support
        Runtime.MetadataReader.InitStringMethodTable();

        // Set up the MetadataRoot for string resolution (ldstr)
        Runtime.MetadataReader.SetMetadataRoot(GetTestMetadataRoot());

        // Initialize runtime helpers for JIT (allocation, MD array, etc.)
        Runtime.RuntimeHelpers.Init();

        // Initialize AOT method registry for well-known types (String, etc.)
        Runtime.AotMethodRegistry.Init();

        // Initialize AOT static field registry for excluded types (Boolean, IntPtr, etc.)
        Runtime.AotStaticFieldRegistry.Initialize();
        Runtime.AotStaticFieldRegistry.RegisterKnownFields();

        // Initialize JIT stubs for lazy method compilation
        Runtime.JIT.JitStubs.Init();

        // Initialize string pool for interning and ldstr caching (requires HeapAllocator)
        Runtime.StringPool.Init();

        // Initialize assembly loader (requires HeapAllocator)
        AssemblyLoader.Initialize();

        // Initialize ReflectionRuntime for type info reverse lookups (MT* -> assembly/token)
        Runtime.Reflection.ReflectionRuntime.Init();

        // Force bflat to keep virtual method vtable entries for reflection types.
        // Without this, DCE may remove vtable slots that JIT code needs.
        System.RuntimeType.ForceKeepVtableMethods();
        System.Reflection.ReflectionVtableKeeper.ForceKeepVtableMethods();

        // Register korlib.dll as CoreLib (provides IL for generic instantiation)
        if (_korlibBytes != null)
        {
            _korlibId = AssemblyLoader.Load(_korlibBytes, _korlibSize, AssemblyFlags.CoreLib);
            if (_korlibId != AssemblyLoader.InvalidAssemblyId)
            {
                // Build korlib type name cache for fast lookups
                AssemblyLoader.BuildKorlibTypeCache(_korlibId);

                // Build token-based AOT method registry from korlib metadata
                BuildKorlibTokenRegistry();

                // Build DDK token registry (maps korlib DDK methods to kernel exports)
                BuildDDKTokenRegistry();

                // Build JIT console bridge (maps korlib System.Console /
                // System.Environment IL stubs to the kernel console exports)
                BuildConsoleTokenRegistry();

                // Build JIT file bridge (maps korlib System.IO.File /
                // System.IO.Directory IL stubs to the kernel FileExports,
                // which drive the JIT-loaded FAT driver)
                BuildFileTokenRegistry();

                // Initialize critical interface types (IDisposable) from korlib
                AssemblyLoader.InitializeKorlibInterfaces(_korlibId);

                // Test AOT→JIT: compile a method from korlib.dll
                TestAotToJitCompilation();
            }
        }

        // Register TestSupport.dll (dependency for test assembly)
        if (_testSupportBytes != null)
        {
            _testSupportId = AssemblyLoader.Load(_testSupportBytes, _testSupportSize);
        }

        // Register ProtonOS.DDK.dll
        if (_ddkBytes != null)
        {
            _ddkId = AssemblyLoader.Load(_ddkBytes, _ddkSize);
        }

        // Register driver assemblies
        if (_virtioDriverBytes != null)
        {
            _virtioDriverId = AssemblyLoader.Load(_virtioDriverBytes, _virtioDriverSize);
        }

        if (_virtioBlkDriverBytes != null)
        {
            _virtioBlkDriverId = AssemblyLoader.Load(_virtioBlkDriverBytes, _virtioBlkDriverSize);
        }

        if (_fatDriverBytes != null)
        {
            _fatDriverId = AssemblyLoader.Load(_fatDriverBytes, _fatDriverSize);
        }

        if (_ext2DriverBytes != null)
        {
            _ext2DriverId = AssemblyLoader.Load(_ext2DriverBytes, _ext2DriverSize);
        }

        if (_ahciDriverBytes != null)
        {
            _ahciDriverId = AssemblyLoader.Load(_ahciDriverBytes, _ahciDriverSize);
        }

        if (_virtioNetDriverBytes != null)
        {
            _virtioNetDriverId = AssemblyLoader.Load(_virtioNetDriverBytes, _virtioNetDriverSize);
        }

        // Register ProtonOS.Net library (depends on DDK)
        if (_protonOsNetBytes != null)
        {
            _protonOsNetId = AssemblyLoader.Load(_protonOsNetBytes, _protonOsNetSize);
        }

        // Register the test assembly with AssemblyLoader (depends on TestSupport and DDK)
        if (_testAssemblyBytes != null)
        {
            _testAssemblyId = AssemblyLoader.Load(_testAssemblyBytes, _testAssemblySize);
        }

        // Register AppTest assembly (depends on DDK and ProtonOS.Net)
        if (_appTestBytes != null)
        {
            _appTestId = AssemblyLoader.Load(_appTestBytes, _appTestSize);
        }

        // Register console_io_test assembly (Phase 2 console I/O tests)
        if (_consoleIoTestBytes != null)
        {
            _consoleIoTestId = AssemblyLoader.Load(_consoleIoTestBytes, _consoleIoTestSize);
        }

        // Register vga_test assembly (Phase 3 VGA console tests)
        if (_vgaTestBytes != null)
        {
            _vgaTestId = AssemblyLoader.Load(_vgaTestBytes, _vgaTestSize);
        }

        // Register keyboard_test assembly (Phase 3 PS/2 keyboard test)
        if (_keyboardTestBytes != null)
        {
            _keyboardTestId = AssemblyLoader.Load(_keyboardTestBytes, _keyboardTestSize);
        }

        if (_jitTestBytes != null)
        {
            _jitTestId = AssemblyLoader.Load(_jitTestBytes, _jitTestSize);
        }

        BootLog.Status("Assemblies loaded");

        // Initialize metadata integration layer (requires HeapAllocator)
        MetadataIntegration.Initialize();

        // Wire up metadata context for token resolution (backward compatibility)
        // TODO: Migrate to use AssemblyLoader.GetAssembly(_testAssemblyId) instead
        MetadataIntegration.SetMetadataContext(
            GetTestMetadataRoot(),
            GetTestTablesHeader(),
            GetTestTableSizes());

        // Register well-known AOT types (System.String, etc.)
        MetadataIntegration.RegisterWellKnownTypes();

        // Initialize kernel exports for PInvoke resolution (must be before DDK)
        Runtime.KernelExportInit.Initialize();
        BootLog.Status("Kernel exports ready");

        // Initialize DDK (Driver Development Kit)
        RunDDKInit();
        BootLog.Status("DDK initialized");

        // Initialize PCI subsystem and enumerate devices
        Platform.PCI.Initialize();
        Platform.PCI.EnumerateAndPrint();
        BootLog.Status("PCI enumerated");

        // Driver framework: build the device tree (PCI/VirtIO/platform)
        // and run the ABI-gated driver match pass.
        ProtonOS.Drivers.DriverFramework.Initialize();
        BootLog.Status("Driver framework initialized");

        // Bind drivers to detected PCI devices
        BindDrivers();
        BootLog.Status("Drivers bound");

        // Phase 8: load driver packages installed by npkg (catalog ->
        // manifest -> assembly -> Create() factory -> thunk adapter ->
        // DriverManager). The loader reads the boot volume through the
        // AHCI bridge, which is available once BindDrivers bound AHCI.
        ProtonOS.Drivers.DriverFramework.LoadPackagedDrivers();

        // Run the FullTest assembly to exercise JIT functionality
        // (skipped when the skip-boot-tests marker file is present on the
        //  boot volume - useful for fast console-only development cycles)
        bool skipBootTests = BootInfoAccess.FindFile("skip-boot-tests", out ulong _skipMarkerSize) != null;
        if (!skipBootTests)
        {
            RunFullTestAssembly();

            // Run the JITTest assembly (comprehensive IL opcode testing)
            RunJITTestAssembly();

            // Run the AppTest assembly (application-level tests after drivers loaded)
            RunAppTestAssembly();

            // Run syscall tests in Ring 3 (comprehensive syscall validation)
            Process.UserModeTests.RunSyscallTests();

            BootLog.Status("Boot tests complete");
        }
        else
        {
            DebugConsole.WriteLine("[Kernel] Boot tests skipped (skip-boot-tests marker present)");
            BootLog.Status("Boot tests skipped");
        }

        // Phase 7: verify the privilege boundary (user/kernel isolation,
        // W^X) by walking a fresh user address space. Runs on every boot
        // (marker-independent) and prints stable [SEC] lines for the
        // acceptance scripts.
        Process.SecuritySelfTest.Run();

        // Phase 7 (SMP fix): start the APs only now. Starting them in early
        // Stage 2 deadlocked 2-vCPU boots - a trampoline-era AP fault hit
        // the exception machinery before JIT-registration / EH-table /
        // console locks had an established order (ExceptionHandling._lock
        // left held, both CPUs spinning; reproduced on QEMU -smp 2 and
        // VirtualBox). APs then park in SMP.ApEntry until ReleaseAps().
        if (Platform.CPUTopology.CpuCount > 1)
        {
            Arch.SMP.Init();
            Scheduler.EnableSmp();
        }
        Arch.SMP.ReleaseAps();

        // Note: execve tests are available via:
        // - Process.NetExecutable.TestExecHelloApp() - Main() returns 42
        // - Process.NetExecutable.TestExecArgsApp()  - Main(string[] args) returns args.Length

        // Note: If execve succeeds, we never get here
        // The syscall tests can be run instead by uncommenting:
        // Process.UserModeTests.RunSyscallTests();

        // Enable preemptive scheduling (diagnostic: skip-preempt marker
        // disables it to isolate context-switch issues)
        if (BootInfoAccess.FindFile("skip-preempt", out ulong _skipPreemptSize) != null)
        {
            DebugConsole.WriteLine("[Kernel] Preemptive scheduling disabled (skip-preempt marker)");
            BootLog.Status("Scheduling disabled (marker)");
        }
        else
        {
            Scheduler.EnableScheduling();
            BootLog.Status("Scheduling enabled");
        }

        // Phase 2: bring up the console abstraction layer. The serial
        // console (/dev/ttyS0) was initialized for polled output during
        // early boot; from here on all console I/O - kernel logging,
        // System.Console and the shell - flows through the CAL and the
        // UART RX interrupt feeds the line discipline.
        ConsoleAbstractionLayer.Initialize();
        BootLog.Status("Console layer ready");

        DebugConsole.WriteLine();
        DebugConsole.WriteLine("[OK] Kernel initialization complete");
        BootLog.Status("Boot complete");

        // Interactive console I/O acceptance test (console_io_test.dll).
        // Only runs when a "run-console-test" marker file is present on
        // the boot volume, so normal boots stay non-interactive.
        MaybeRunConsoleIoTestAssembly();

        // Phase 3 VGA console acceptance test (vga_test.dll) under the
        // "run-vga-test" marker; PS/2 keyboard inspector
        // (keyboard_test.dll) under the "run-keyboard-test" marker.
        MaybeRunVgaTestAssembly();
        MaybeRunKeyboardTestAssembly();

        // Host the interactive serial shell (System.Console.ReadLine
        // through the line discipline: echo, editing, history, Ctrl+C/D).
        BootLog.Status("Starting interactive shell");
        ConsoleSession.Run();
    }

    /// <summary>
    /// Load the test assembly from BootInfo's loaded files table.
    /// Files were loaded by the bootloader before ExitBootServices.
    /// </summary>
    private static void LoadTestAssembly()
    {
        // Load TestSupport.dll (dependency for test assembly)
        _testSupportBytes = BootInfoAccess.FindFile("TestSupport.dll", out _testSupportSize);

        // Load ProtonOS.DDK.dll (Driver Development Kit)
        _ddkBytes = BootInfoAccess.FindFile("ProtonOS.DDK.dll", out _ddkSize);

        // Load driver assemblies
        _virtioDriverBytes = BootInfoAccess.FindFile("ProtonOS.Drivers.Virtio.dll", out _virtioDriverSize);
        _virtioBlkDriverBytes = BootInfoAccess.FindFile("ProtonOS.Drivers.VirtioBlk.dll", out _virtioBlkDriverSize);
        _fatDriverBytes = BootInfoAccess.FindFile("ProtonOS.Drivers.Fat.dll", out _fatDriverSize);
        _ext2DriverBytes = BootInfoAccess.FindFile("ProtonOS.Drivers.Ext2.dll", out _ext2DriverSize);
        _ahciDriverBytes = BootInfoAccess.FindFile("ProtonOS.Drivers.Ahci.dll", out _ahciDriverSize);
        _virtioNetDriverBytes = BootInfoAccess.FindFile("ProtonOS.Drivers.VirtioNet.dll", out _virtioNetDriverSize);

        // Load korlib.dll (IL assembly for JIT generic instantiation)
        _korlibBytes = BootInfoAccess.FindFile("korlib.dll", out _korlibSize);

        // Load ProtonOS.Net.dll (application-level networking library)
        _protonOsNetBytes = BootInfoAccess.FindFile("ProtonOS.Net.dll", out _protonOsNetSize);

        // Load FullTest.dll
        _testAssemblyBytes = BootInfoAccess.FindFile("FullTest.dll", out _testAssemblySize);

        // Load AppTest.dll (application-level tests)
        _appTestBytes = BootInfoAccess.FindFile("AppTest.dll", out _appTestSize);

        // Load console_io_test.dll (Phase 2 console I/O acceptance test)
        _consoleIoTestBytes = BootInfoAccess.FindFile("console_io_test.dll", out _consoleIoTestSize);

        // Load vga_test.dll (Phase 3 VGA console acceptance test)
        _vgaTestBytes = BootInfoAccess.FindFile("vga_test.dll", out _vgaTestSize);

        // Load keyboard_test.dll (Phase 3 PS/2 keyboard verification test)
        _keyboardTestBytes = BootInfoAccess.FindFile("keyboard_test.dll", out _keyboardTestSize);

        // Load JITTest.dll (comprehensive IL opcode testing)
        _jitTestBytes = BootInfoAccess.FindFile("JITTest.dll", out _jitTestSize);

        if (_testAssemblyBytes == null)
        {
            DebugConsole.WriteLine("[Kernel] Failed to load test assembly from BootInfo");
            return;
        }

        // Verify it looks like a PE file (MZ header)
        if (_testAssemblySize >= 2 && _testAssemblyBytes[0] == 'M' && _testAssemblyBytes[1] == 'Z')
        {
            // Parse CLI header and metadata (still needed before ExitBootServices)
            ParseAssemblyMetadata();
        }
        else
        {
            DebugConsole.WriteLine("[Kernel] WARNING: Not a valid PE file");
        }
    }

    /// <summary>
    /// Parse the CLI header and metadata from the loaded test assembly.
    /// The assembly is loaded as raw file bytes, not memory-mapped.
    /// </summary>
    private static void ParseAssemblyMetadata()
    {
        // Get CLI header (using file-based RVA translation)
        var corHeader = PEHelper.GetCorHeaderFromFile(_testAssemblyBytes);
        if (corHeader == null)
        {
            DebugConsole.WriteLine("[Kernel] No CLI header found");
            return;
        }

        // Get metadata root (using file-based RVA translation)
        var metadataRoot = (byte*)PEHelper.GetMetadataRootFromFile(_testAssemblyBytes);
        if (metadataRoot == null)
        {
            DebugConsole.WriteLine("[Kernel] Failed to locate metadata");
            return;
        }

        // Verify BSJB signature
        uint signature = *(uint*)metadataRoot;
        if (signature != PEConstants.METADATA_SIGNATURE)
        {
            DebugConsole.Write("[Kernel] Invalid metadata signature: 0x");
            DebugConsole.WriteHex(signature);
            DebugConsole.WriteLine();
            return;
        }

        // Parse metadata streams
        if (MetadataReader.Init(metadataRoot, corHeader->MetaData.Size, out var mdRoot))
        {
            // Save the MetadataRoot for later use (e.g., string resolution)
            _testMetadataRoot = mdRoot;

            // MetadataReader.Dump(ref mdRoot);

            // Parse #~ (tables) stream header
            if (MetadataReader.ParseTablesHeader(ref mdRoot, out var tablesHeader))
            {
                // Save for later use (token resolution)
                _testTablesHeader = tablesHeader;
                _testTableSizes = TableSizes.Calculate(ref tablesHeader);

                // MetadataReader.DumpTablesHeader(ref tablesHeader);

                // Test heap access by reading Module and TypeDef tables
                // MetadataReader.DumpModuleTable(ref mdRoot, ref tablesHeader);
                // MetadataReader.DumpTypeDefTable(ref mdRoot, ref tablesHeader);

                // Test new table accessors with TypeRef, MethodDef, MemberRef, AssemblyRef
                // MetadataReader.DumpTypeRefTable(ref mdRoot, ref tablesHeader);
                // MetadataReader.DumpMethodDefTable(ref mdRoot, ref tablesHeader);
                // MetadataReader.DumpMemberRefTable(ref mdRoot, ref tablesHeader);
                // MetadataReader.DumpAssemblyRefTable(ref mdRoot, ref tablesHeader);

                // Test IL method body parsing
                // DumpMethodBodies(ref mdRoot, ref tablesHeader);

                // Test type resolution (Phase 5.9)
                // MetadataReader.TestTypeResolution(ref mdRoot, ref tablesHeader, ref _testTableSizes);

                // Test assembly identity (Phase 5.10)
                // MetadataReader.TestAssemblyIdentity(ref mdRoot, ref tablesHeader, ref _testTableSizes);
            }
            else
            {
                DebugConsole.WriteLine("[Kernel] Failed to parse #~ header");
            }
        }
        else
        {
            DebugConsole.WriteLine("[Kernel] Failed to parse metadata");
        }
    }

    /// <summary>
    /// Parse and dump IL method bodies for all methods in the assembly
    /// </summary>
    private static void DumpMethodBodies(ref MetadataRoot mdRoot, ref TablesHeader tablesHeader)
    {
        uint methodCount = tablesHeader.RowCounts[(int)MetadataTableId.MethodDef];
        if (methodCount == 0)
            return;

        var sizes = TableSizes.Calculate(ref tablesHeader);

        DebugConsole.WriteLine();
        DebugConsole.WriteLine("[Kernel] Parsing method bodies and signatures:");

        for (uint i = 1; i <= methodCount; i++)
        {
            uint rva = MetadataReader.GetMethodDefRva(ref tablesHeader, ref sizes, i);
            uint nameIdx = MetadataReader.GetMethodDefName(ref tablesHeader, ref sizes, i);
            uint sigIdx = MetadataReader.GetMethodDefSignature(ref tablesHeader, ref sizes, i);

            DebugConsole.Write("[Kernel]   Method ");
            MetadataReader.PrintString(ref mdRoot, nameIdx);
            DebugConsole.WriteLine(":");

            // Parse and display method signature
            byte* sigBlob = MetadataReader.GetBlob(ref mdRoot, sigIdx, out uint sigLen);
            if (sigBlob != null && sigLen > 0)
            {
                DebugConsole.Write("[Kernel]     Sig: ");
                if (SignatureReader.ReadMethodSignature(sigBlob, sigLen, out var methodSig))
                {
                    SignatureReader.PrintMethodSignature(ref methodSig);
                    DebugConsole.WriteLine();
                }
                else
                {
                    DebugConsole.WriteLine("failed to parse");
                }
            }

            if (rva == 0)
            {
                // Abstract, extern, or runtime-implemented method (no IL body)
                DebugConsole.WriteLine("[Kernel]     no IL body");
                continue;
            }

            // Convert RVA to file pointer
            byte* methodBodyPtr = (byte*)PEHelper.RvaToFilePointer(_testAssemblyBytes, rva);
            if (methodBodyPtr == null)
            {
                DebugConsole.WriteLine("[Kernel]     failed to resolve RVA");
                continue;
            }

            // Parse the method body
            if (MetadataReader.ReadMethodBody(methodBodyPtr, out var body))
            {
                MetadataReader.DumpMethodBody(ref body);
            }
            else
            {
                DebugConsole.WriteLine("[Kernel]     failed to parse body");
            }
        }
    }

    /// <summary>
    /// Run the FullTest assembly via JIT compilation.
    /// Finds TestRunner.RunAllTests() and executes it.
    /// </summary>
    private static void RunFullTestAssembly()
    {
        DebugConsole.WriteLine();
        DebugConsole.WriteLine("==============================");
        DebugConsole.WriteLine("  Running FullTest Assembly");
        DebugConsole.WriteLine("==============================");
        DebugConsole.WriteLine();

        // Debug: Check korlib blob heap integrity before running tests
        var korlibAsm = AssemblyLoader.GetAssembly(_korlibId);
        if (korlibAsm != null)
        {
            DebugConsole.Write("[FullTest] Korlib blob heap check: [0x44]=0x");
            DebugConsole.WriteHex(korlibAsm->Metadata.BlobHeap[0x44]);
            DebugConsole.Write(" [0x4D]=0x");
            DebugConsole.WriteHex(korlibAsm->Metadata.BlobHeap[0x4D]);
            DebugConsole.WriteLine();
        }

        if (_testAssemblyId == AssemblyLoader.InvalidAssemblyId)
        {
            DebugConsole.WriteLine("[FullTest] ERROR: No test assembly loaded");
            return;
        }

        DebugConsole.WriteLine(string.Format("[FullTest] Assembly ID: {0}", _testAssemblyId));

        // Find TestRunner type using AssemblyLoader
        uint testRunnerToken = AssemblyLoader.FindTypeDefByFullName(_testAssemblyId, "FullTest", "TestRunner");
        if (testRunnerToken == 0)
        {
            DebugConsole.WriteLine("[FullTest] ERROR: Could not find FullTest.TestRunner type");
            return;
        }

        DebugConsole.WriteLine(string.Format("[FullTest] Found TestRunner type, token: 0x{0}", testRunnerToken.ToString("X8", null)));

        // Find RunAllTests method using AssemblyLoader
        uint runAllTestsToken = AssemblyLoader.FindMethodDefByName(_testAssemblyId, testRunnerToken, "RunAllTests");
        if (runAllTestsToken == 0)
        {
            DebugConsole.WriteLine("[FullTest] ERROR: Could not find RunAllTests method");
            return;
        }

        DebugConsole.WriteLine(string.Format("[FullTest] Found RunAllTests method, token: 0x{0}", runAllTestsToken.ToString("X8", null)));

        // JIT compile the method
        DebugConsole.WriteLine("[FullTest] JIT compiling RunAllTests...");

        var jitResult = Runtime.JIT.Tier0JIT.CompileMethod(_testAssemblyId, runAllTestsToken);
        if (jitResult.Success)
        {
            DebugConsole.WriteLine(string.Format("[FullTest] JIT compilation successful, code at 0x{0}", ((ulong)jitResult.CodeAddress).ToString("X", null)));

            // Execute the compiled method
            DebugConsole.WriteLine("[FullTest] Executing RunAllTests...");
            DebugConsole.WriteLine();

            // Call the compiled method (returns int)
            var funcPtr = (delegate* unmanaged<int>)jitResult.CodeAddress;
            int result = funcPtr();

            // Parse result: (passCount << 16) | failCount
            int passCount = (result >> 16) & 0xFFFF;
            int failCount = result & 0xFFFF;

            DebugConsole.WriteLine();
            DebugConsole.WriteLine("==============================");
            DebugConsole.WriteLine("  FullTest Results");
            DebugConsole.WriteLine("==============================");
            DebugConsole.WriteLine(string.Format("[FullTest] Passed: {0}", passCount));
            DebugConsole.WriteLine(string.Format("[FullTest] Failed: {0}", failCount));

            if (failCount == 0)
            {
                DebugConsole.WriteLine("[FullTest] ALL TESTS PASSED!");
            }
            else
            {
                DebugConsole.WriteLine("[FullTest] SOME TESTS FAILED");
            }

            // Report JIT debug statistics
            int jitRegistered = Runtime.JIT.GdbJitDebug.GetRegisteredCount();
            int jitListCount = Runtime.JIT.GdbJitDebug.CountLinkedListEntries();
            DebugConsole.WriteLine(string.Format("[GdbJit] Registered: {0}  LinkedList: {1}", jitRegistered, jitListCount));

            // Dump linked list sample to verify integrity
            Runtime.JIT.GdbJitDebug.DumpLinkedListSample();
        }
        else
        {
            DebugConsole.WriteLine("[FullTest] ERROR: JIT compilation failed");
        }
    }

    /// <summary>
    /// Phase 2: run console_io_test.dll when the "run-console-test" marker
    /// file is present on the boot volume (so normal boots never block on
    /// interactive input). The test exercises System.Console end-to-end
    /// through the CAL, line discipline and JIT pipeline.
    /// </summary>
    private static void MaybeRunConsoleIoTestAssembly()
    {
        if (_consoleIoTestId == AssemblyLoader.InvalidAssemblyId)
            return;

        byte* marker = BootInfoAccess.FindFile("run-console-test", out ulong markerSize);
        if (marker == null)
            return;

        DebugConsole.WriteLine();
        DebugConsole.WriteLine("==============================");
        DebugConsole.WriteLine("  Running Console I/O Test");
        DebugConsole.WriteLine("==============================");

        uint runnerToken = AssemblyLoader.FindTypeDefByFullName(_consoleIoTestId, "ConsoleIoTest", "TestRunner");
        if (runnerToken == 0)
        {
            DebugConsole.WriteLine("[ConIO] ERROR: ConsoleIoTest.TestRunner type not found");
            return;
        }

        uint runToken = AssemblyLoader.FindMethodDefByName(_consoleIoTestId, runnerToken, "RunAllTests");
        if (runToken == 0)
        {
            DebugConsole.WriteLine("[ConIO] ERROR: RunAllTests method not found");
            return;
        }

        var jitResult = Runtime.JIT.Tier0JIT.CompileMethod(_consoleIoTestId, runToken);
        if (!jitResult.Success)
        {
            DebugConsole.WriteLine("[ConIO] ERROR: JIT compilation failed");
            return;
        }

        var testMethod = (delegate*<int>)jitResult.CodeAddress;
        int result = testMethod();

        int passCount = (result >> 16) & 0xFFFF;
        int failCount = result & 0xFFFF;

        DebugConsole.Write("[ConIO] Passed: ");
        DebugConsole.WriteDecimal(passCount);
        DebugConsole.Write("  Failed: ");
        DebugConsole.WriteDecimal(failCount);
        DebugConsole.WriteLine();
        DebugConsole.WriteLine(failCount == 0 ? "[ConIO] ALL TESTS PASSED!" : "[ConIO] SOME TESTS FAILED");
        DebugConsole.WriteLine("==============================");
    }

    /// <summary>
    /// Phase 3: run vga_test.dll when the "run-vga-test" marker file is
    /// present on the boot volume. Exercises the VGA text console through
    /// System.Console: colored output, Clear, cursor positioning/read-back
    /// and CP437 extended characters.
    /// </summary>
    private static void MaybeRunVgaTestAssembly()
    {
        if (_vgaTestId == AssemblyLoader.InvalidAssemblyId)
            return;

        byte* marker = BootInfoAccess.FindFile("run-vga-test", out ulong markerSize);
        if (marker == null)
            return;

        DebugConsole.WriteLine();
        DebugConsole.WriteLine("==============================");
        DebugConsole.WriteLine("  Running VGA Console Test");
        DebugConsole.WriteLine("==============================");

        uint runnerToken = AssemblyLoader.FindTypeDefByFullName(_vgaTestId, "VgaTest", "TestRunner");
        if (runnerToken == 0)
        {
            DebugConsole.WriteLine("[VgaTest] ERROR: VgaTest.TestRunner type not found");
            return;
        }

        uint runToken = AssemblyLoader.FindMethodDefByName(_vgaTestId, runnerToken, "RunAllTests");
        if (runToken == 0)
        {
            DebugConsole.WriteLine("[VgaTest] ERROR: RunAllTests method not found");
            return;
        }

        var jitResult = Runtime.JIT.Tier0JIT.CompileMethod(_vgaTestId, runToken);
        if (!jitResult.Success)
        {
            DebugConsole.WriteLine("[VgaTest] ERROR: JIT compilation failed");
            return;
        }

        var testMethod = (delegate*<int>)jitResult.CodeAddress;
        int result = testMethod();

        int passCount = (result >> 16) & 0xFFFF;
        int failCount = result & 0xFFFF;

        DebugConsole.Write("[VgaTest] Passed: ");
        DebugConsole.WriteDecimal(passCount);
        DebugConsole.Write("  Failed: ");
        DebugConsole.WriteDecimal(failCount);
        DebugConsole.WriteLine();
        DebugConsole.WriteLine(failCount == 0 ? "[VgaTest] ALL TESTS PASSED!" : "[VgaTest] SOME TESTS FAILED");
        DebugConsole.WriteLine("==============================");
    }

    /// <summary>
    /// Phase 3: run keyboard_test.dll when the "run-keyboard-test" marker
    /// file is present. The test is interactive: it prints the
    /// ConsoleKeyInfo of every key pressed until Escape, verifying the
    /// PS/2 decoder against a manual keypress matrix.
    /// </summary>
    private static void MaybeRunKeyboardTestAssembly()
    {
        if (_keyboardTestId == AssemblyLoader.InvalidAssemblyId)
            return;

        byte* marker = BootInfoAccess.FindFile("run-keyboard-test", out ulong markerSize);
        if (marker == null)
            return;

        DebugConsole.WriteLine();
        DebugConsole.WriteLine("==============================");
        DebugConsole.WriteLine("  Running PS/2 Keyboard Test");
        DebugConsole.WriteLine("==============================");

        uint runnerToken = AssemblyLoader.FindTypeDefByFullName(_keyboardTestId, "KeyboardTest", "TestRunner");
        if (runnerToken == 0)
        {
            DebugConsole.WriteLine("[KbdTest] ERROR: KeyboardTest.TestRunner type not found");
            return;
        }

        uint runToken = AssemblyLoader.FindMethodDefByName(_keyboardTestId, runnerToken, "Run");
        if (runToken == 0)
        {
            DebugConsole.WriteLine("[KbdTest] ERROR: Run method not found");
            return;
        }

        var jitResult = Runtime.JIT.Tier0JIT.CompileMethod(_keyboardTestId, runToken);
        if (!jitResult.Success)
        {
            DebugConsole.WriteLine("[KbdTest] ERROR: JIT compilation failed");
            return;
        }

        var runMethod = (delegate*<void>)jitResult.CodeAddress;
        runMethod();

        DebugConsole.WriteLine("[KbdTest] Keyboard test finished");
        DebugConsole.WriteLine("==============================");
    }

    /// <summary>
    /// Run the JITTest assembly via JIT compilation.
    /// Comprehensive IL opcode testing.
    /// </summary>
    private static void RunJITTestAssembly()
    {
        if (_jitTestId == AssemblyLoader.InvalidAssemblyId)
        {
            DebugConsole.WriteLine("[JITTest] No JITTest assembly loaded, skipping");
            return;
        }

        DebugConsole.WriteLine();
        DebugConsole.WriteLine("==============================");
        DebugConsole.WriteLine("  Running JITTest Assembly");
        DebugConsole.WriteLine("==============================");
        DebugConsole.WriteLine();

        // Find TestRunner type
        uint testRunnerToken = AssemblyLoader.FindTypeDefByFullName(_jitTestId, "JITTest", "TestRunner");
        if (testRunnerToken == 0)
        {
            DebugConsole.WriteLine("[JITTest] ERROR: Could not find JITTest.TestRunner type");
            return;
        }

        // Find RunAllTests method
        uint runAllTestsToken = AssemblyLoader.FindMethodDefByName(_jitTestId, testRunnerToken, "RunAllTests");
        if (runAllTestsToken == 0)
        {
            DebugConsole.WriteLine("[JITTest] ERROR: Could not find RunAllTests method");
            return;
        }

        // JIT compile the method
        var jitResult = Runtime.JIT.Tier0JIT.CompileMethod(_jitTestId, runAllTestsToken);
        if (jitResult.Success)
        {
            // Execute the compiled method
            var testMethod = (delegate*<int>)jitResult.CodeAddress;
            int result = testMethod();

            // Decode result
            int passCount = (result >> 16) & 0xFFFF;
            int failCount = result & 0xFFFF;

            DebugConsole.WriteLine();
            DebugConsole.WriteLine("==============================");
            DebugConsole.WriteLine("  JITTest Results");
            DebugConsole.WriteLine("==============================");
            DebugConsole.WriteLine(string.Format("[JITTest] Passed: {0}", passCount));
            DebugConsole.WriteLine(string.Format("[JITTest] Failed: {0}", failCount));

            if (failCount == 0)
            {
                DebugConsole.WriteLine("[JITTest] ALL TESTS PASSED!");
            }
            else
            {
                DebugConsole.WriteLine("[JITTest] SOME TESTS FAILED");
            }
        }
        else
        {
            DebugConsole.WriteLine("[JITTest] ERROR: JIT compilation failed");
        }
    }

    /// <summary>
    /// Run the AppTest assembly via JIT compilation.
    /// Tests application-level functionality like HTTP.
    /// </summary>
    private static void RunAppTestAssembly()
    {
        if (_appTestId == AssemblyLoader.InvalidAssemblyId)
        {
            DebugConsole.WriteLine("[AppTest] No AppTest assembly loaded, skipping");
            return;
        }

        DebugConsole.WriteLine();
        DebugConsole.WriteLine("==============================");
        DebugConsole.WriteLine("  Running AppTest Assembly");
        DebugConsole.WriteLine("==============================");
        DebugConsole.WriteLine();

        // Find TestRunner type
        uint testRunnerToken = AssemblyLoader.FindTypeDefByFullName(_appTestId, "AppTest", "TestRunner");
        if (testRunnerToken == 0)
        {
            DebugConsole.WriteLine("[AppTest] ERROR: Could not find AppTest.TestRunner type");
            return;
        }

        // Find RunAllTests method
        uint runAllTestsToken = AssemblyLoader.FindMethodDefByName(_appTestId, testRunnerToken, "RunAllTests");
        if (runAllTestsToken == 0)
        {
            DebugConsole.WriteLine("[AppTest] ERROR: Could not find RunAllTests method");
            return;
        }

        // JIT compile the method
        var jitResult = Runtime.JIT.Tier0JIT.CompileMethod(_appTestId, runAllTestsToken);
        if (jitResult.Success)
        {
            // Execute the compiled method
            var testMethod = (delegate*<int>)jitResult.CodeAddress;
            int result = testMethod();

            // Decode result
            int passCount = (result >> 16) & 0xFFFF;
            int failCount = result & 0xFFFF;

            DebugConsole.WriteLine();
            DebugConsole.WriteLine("==============================");
            DebugConsole.WriteLine("  AppTest Results");
            DebugConsole.WriteLine("==============================");
            DebugConsole.WriteLine(string.Format("[AppTest] Passed: {0}", passCount));
            DebugConsole.WriteLine(string.Format("[AppTest] Failed: {0}", failCount));

            if (failCount == 0)
            {
                DebugConsole.WriteLine("[AppTest] ALL TESTS PASSED!");
            }
            else
            {
                DebugConsole.WriteLine("[AppTest] SOME TESTS FAILED");
            }
        }
        else
        {
            DebugConsole.WriteLine("[AppTest] ERROR: JIT compilation failed");
        }
    }

    /// <summary>
    /// Build the token-based AOT method registry from korlib.dll metadata.
    /// Maps korlib method tokens to native code addresses.
    /// </summary>
    private static void BuildKorlibTokenRegistry()
    {
        DebugConsole.WriteLine("[Kernel] Building korlib token registry...");

        // Get korlib assembly
        LoadedAssembly* korlib = AssemblyLoader.GetAssembly(_korlibId);
        if (korlib == null)
        {
            DebugConsole.WriteLine("[Kernel] ERROR: korlib assembly not found");
            return;
        }

        // Initialize the token registry
        AotMethodRegistry.InitTokenRegistry();

        // Get MethodDef table row count
        uint methodDefCount = korlib->Tables.RowCounts[(int)MetadataTableId.MethodDef];

        int registered = 0;
        int matched = 0;

        // Iterate through all MethodDef entries
        for (uint row = 1; row <= methodDefCount; row++)
        {
            // Get method name
            uint nameIdx = MetadataReader.GetMethodDefName(ref korlib->Tables, ref korlib->Sizes, row);
            byte* methodName = MetadataReader.GetString(ref korlib->Metadata, nameIdx);

            // Get declaring type for this method
            uint typeDefRow = FindOwningTypeDef(korlib, row);
            if (typeDefRow == 0)
                continue;

            // Get type name and namespace
            uint typeNameIdx = MetadataReader.GetTypeDefName(ref korlib->Tables, ref korlib->Sizes, typeDefRow);
            uint typeNsIdx = MetadataReader.GetTypeDefNamespace(ref korlib->Tables, ref korlib->Sizes, typeDefRow);
            byte* typeName = MetadataReader.GetString(ref korlib->Metadata, typeNameIdx);
            byte* typeNs = MetadataReader.GetString(ref korlib->Metadata, typeNsIdx);

            // Build full type name (namespace.typename)
            byte* fullTypeName = stackalloc byte[256];
            int pos = 0;

            // Copy namespace
            if (typeNs != null && typeNs[0] != 0)
            {
                for (int i = 0; typeNs[i] != 0 && pos < 254; i++)
                    fullTypeName[pos++] = typeNs[i];
                fullTypeName[pos++] = (byte)'.';
            }

            // Copy type name
            if (typeName != null)
            {
                for (int i = 0; typeName[i] != 0 && pos < 255; i++)
                    fullTypeName[pos++] = typeName[i];
            }
            fullTypeName[pos] = 0;

            // Try to find this method in the hash-based AOT registry
            // Get method flags to determine if static
            ushort methodFlags = MetadataReader.GetMethodDefFlags(ref korlib->Tables, ref korlib->Sizes, row);
            bool isStatic = (methodFlags & 0x0010) != 0; // Static flag

            // Try lookup in hash-based registry
            AotMethodEntry hashEntry;
            if (AotMethodRegistry.TryLookup(fullTypeName, methodName, 0, out hashEntry, false))
            {
                // Found! Register in token registry
                uint methodToken = 0x06000000 | row;
                AotMethodFlags flags = hashEntry.Flags;

                AotMethodRegistry.RegisterByToken(_korlibId, methodToken, hashEntry.NativeCode, flags);
                registered++;
                matched++;
            }
        }

        DebugConsole.Write("[Kernel] Scanned ");
        DebugConsole.WriteDecimal((int)methodDefCount);
        DebugConsole.Write(" methods, registered ");
        DebugConsole.WriteDecimal(registered);
        DebugConsole.WriteLine(" in token registry");
    }

    /// <summary>
    /// Find the TypeDef row that owns a MethodDef.
    /// Uses the MethodList field to determine ownership.
    /// </summary>
    private static uint FindOwningTypeDef(LoadedAssembly* asm, uint methodRid)
    {
        uint typeDefCount = asm->Tables.RowCounts[(int)MetadataTableId.TypeDef];

        for (uint row = 1; row <= typeDefCount; row++)
        {
            uint methodListStart = MetadataReader.GetTypeDefMethodList(ref asm->Tables, ref asm->Sizes, row);
            uint methodListEnd;

            if (row < typeDefCount)
                methodListEnd = MetadataReader.GetTypeDefMethodList(ref asm->Tables, ref asm->Sizes, row + 1);
            else
                methodListEnd = asm->Tables.RowCounts[(int)MetadataTableId.MethodDef] + 1;

            if (methodRid >= methodListStart && methodRid < methodListEnd)
                return row;
        }

        return 0;
    }

    /// <summary>
    /// Build token registry entries for korlib DDK types.
    /// Maps korlib DDK method tokens to kernel export addresses.
    /// This enables JIT code to call korlib DDK methods directly.
    /// </summary>
    private static void BuildDDKTokenRegistry()
    {
        if (_korlibId == AssemblyLoader.InvalidAssemblyId)
            return;

        LoadedAssembly* korlib = AssemblyLoader.GetAssembly(_korlibId);
        if (korlib == null)
            return;

        int registered = 0;

        // Register Memory API
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Memory", "AllocatePage",
            (void*)(delegate* unmanaged<ulong>)&Exports.DDK.MemoryExports.AllocatePage);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Memory", "AllocatePages",
            (void*)(delegate* unmanaged<ulong, ulong>)&Exports.DDK.MemoryExports.AllocatePages);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Memory", "FreePage",
            (void*)(delegate* unmanaged<ulong, void>)&Exports.DDK.MemoryExports.FreePage);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Memory", "FreePages",
            (void*)(delegate* unmanaged<ulong, ulong, void>)&Exports.DDK.MemoryExports.FreePages);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Memory", "PhysToVirt",
            (void*)(delegate* unmanaged<ulong, ulong>)&Exports.DDK.MemoryExports.PhysToVirt);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Memory", "VirtToPhys",
            (void*)(delegate* unmanaged<ulong, ulong>)&Exports.DDK.MemoryExports.VirtToPhys);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Memory", "MapMMIO",
            (void*)(delegate* unmanaged<ulong, ulong, ulong>)&Exports.DDK.MemoryExports.MapMMIO);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Memory", "UnmapMMIO",
            (void*)(delegate* unmanaged<ulong, ulong, void>)&Exports.DDK.MemoryExports.UnmapMMIO);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Memory", "GetTotalMemory",
            (void*)(delegate* unmanaged<ulong>)&Exports.DDK.MemoryExports.GetTotalMemory);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Memory", "GetFreeMemory",
            (void*)(delegate* unmanaged<ulong>)&Exports.DDK.MemoryExports.GetFreeMemory);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Memory", "GetPageSize",
            (void*)(delegate* unmanaged<ulong>)&Exports.DDK.MemoryExports.GetPageSize);

        // Register Debug API
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Debug", "Kernel_DebugWrite",
            (void*)(delegate* unmanaged<char*, int, void>)&Exports.DDK.DebugExports.DebugWrite);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Debug", "Kernel_DebugWriteLine",
            (void*)(delegate* unmanaged<char*, int, void>)&Exports.DDK.DebugExports.DebugWriteLine);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Debug", "Kernel_DebugWriteHex64",
            (void*)(delegate* unmanaged<ulong, void>)&Exports.DDK.DebugExports.DebugWriteHex64);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Debug", "Kernel_DebugWriteHex32",
            (void*)(delegate* unmanaged<uint, void>)&Exports.DDK.DebugExports.DebugWriteHex32);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Debug", "Kernel_DebugWriteHex16",
            (void*)(delegate* unmanaged<ushort, void>)&Exports.DDK.DebugExports.DebugWriteHex16);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Debug", "Kernel_DebugWriteHex8",
            (void*)(delegate* unmanaged<byte, void>)&Exports.DDK.DebugExports.DebugWriteHex8);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Debug", "Kernel_DebugWriteDecimal",
            (void*)(delegate* unmanaged<int, void>)&Exports.DDK.DebugExports.DebugWriteDecimal);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Debug", "Kernel_DebugWriteDecimalU",
            (void*)(delegate* unmanaged<uint, void>)&Exports.DDK.DebugExports.DebugWriteDecimalU);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Debug", "Kernel_DebugWriteDecimal64",
            (void*)(delegate* unmanaged<ulong, void>)&Exports.DDK.DebugExports.DebugWriteDecimal64);

        // Register PortIO API
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "PortIO", "InByte",
            (void*)(delegate* unmanaged<ushort, byte>)&Exports.DDK.PortIOExports.InByte);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "PortIO", "OutByte",
            (void*)(delegate* unmanaged<ushort, byte, void>)&Exports.DDK.PortIOExports.OutByte);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "PortIO", "InWord",
            (void*)(delegate* unmanaged<ushort, ushort>)&Exports.DDK.PortIOExports.InWord);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "PortIO", "OutWord",
            (void*)(delegate* unmanaged<ushort, ushort, void>)&Exports.DDK.PortIOExports.OutWord);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "PortIO", "InDword",
            (void*)(delegate* unmanaged<ushort, uint>)&Exports.DDK.PortIOExports.InDword);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "PortIO", "OutDword",
            (void*)(delegate* unmanaged<ushort, uint, void>)&Exports.DDK.PortIOExports.OutDword);

        // Register CPU API
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "CPU", "GetCpuCount",
            (void*)(delegate* unmanaged<int>)&Exports.DDK.CPUExports.GetCpuCount);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "CPU", "GetCurrentCpu",
            (void*)(delegate* unmanaged<int>)&Exports.DDK.CPUExports.GetCurrentCpu);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "CPU", "GetCpuInfo",
            (void*)(delegate* unmanaged<int, Platform.CpuInfo*, bool>)&Exports.DDK.CPUExports.GetCpuInfo);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "CPU", "SetThreadAffinity",
            (void*)(delegate* unmanaged<ulong, ulong>)&Exports.DDK.CPUExports.SetThreadAffinity);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "CPU", "GetThreadAffinity",
            (void*)(delegate* unmanaged<ulong>)&Exports.DDK.CPUExports.GetThreadAffinity);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "CPU", "IsCpuOnline",
            (void*)(delegate* unmanaged<int, bool>)&Exports.DDK.CPUExports.IsCpuOnline);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "CPU", "GetBspIndex",
            (void*)(delegate* unmanaged<int>)&Exports.DDK.CPUExports.GetBspIndex);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "CPU", "GetSystemAffinityMask",
            (void*)(delegate* unmanaged<ulong>)&Exports.DDK.CPUExports.GetSystemAffinityMask);

        // Register PCI API
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "PCI", "ReadConfig32",
            (void*)(delegate* unmanaged<byte, byte, byte, byte, uint>)&Exports.DDK.PCIExports.ReadConfig32);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "PCI", "ReadConfig16",
            (void*)(delegate* unmanaged<byte, byte, byte, byte, ushort>)&Exports.DDK.PCIExports.ReadConfig16);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "PCI", "ReadConfig8",
            (void*)(delegate* unmanaged<byte, byte, byte, byte, byte>)&Exports.DDK.PCIExports.ReadConfig8);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "PCI", "WriteConfig32",
            (void*)(delegate* unmanaged<byte, byte, byte, byte, uint, void>)&Exports.DDK.PCIExports.WriteConfig32);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "PCI", "WriteConfig16",
            (void*)(delegate* unmanaged<byte, byte, byte, byte, ushort, void>)&Exports.DDK.PCIExports.WriteConfig16);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "PCI", "WriteConfig8",
            (void*)(delegate* unmanaged<byte, byte, byte, byte, byte, void>)&Exports.DDK.PCIExports.WriteConfig8);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "PCI", "GetBar",
            (void*)(delegate* unmanaged<byte, byte, byte, int, uint>)&Exports.DDK.PCIExports.GetBar);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "PCI", "GetBarSize",
            (void*)(delegate* unmanaged<byte, byte, byte, int, uint>)&Exports.DDK.PCIExports.GetBarSize);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "PCI", "EnableMemorySpace",
            (void*)(delegate* unmanaged<byte, byte, byte, void>)&Exports.DDK.PCIExports.EnableMemorySpace);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "PCI", "EnableBusMaster",
            (void*)(delegate* unmanaged<byte, byte, byte, void>)&Exports.DDK.PCIExports.EnableBusMaster);

        // Register Thread API
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Thread", "CreateThread",
            (void*)(delegate* unmanaged<delegate* unmanaged<void*, uint>, void*, nuint, uint, uint*, Threading.Thread*>)&Exports.DDK.ThreadExports.CreateThread);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Thread", "ExitThread",
            (void*)(delegate* unmanaged<uint, void>)&Exports.DDK.ThreadExports.ExitThread);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Thread", "GetCurrentThreadId",
            (void*)(delegate* unmanaged<uint>)&Exports.DDK.ThreadExports.GetCurrentThreadId);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Thread", "GetCurrentThread",
            (void*)(delegate* unmanaged<Threading.Thread*>)&Exports.DDK.ThreadExports.GetCurrentThread);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Thread", "Sleep",
            (void*)(delegate* unmanaged<uint, void>)&Exports.DDK.ThreadExports.Sleep);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Thread", "Yield",
            (void*)(delegate* unmanaged<void>)&Exports.DDK.ThreadExports.Yield);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Thread", "GetExitCodeThread",
            (void*)(delegate* unmanaged<Threading.Thread*, uint*, bool>)&Exports.DDK.ThreadExports.GetExitCodeThread);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Thread", "GetThreadState",
            (void*)(delegate* unmanaged<Threading.Thread*, int>)&Exports.DDK.ThreadExports.GetThreadState);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Thread", "SuspendThread",
            (void*)(delegate* unmanaged<Threading.Thread*, int>)&Exports.DDK.ThreadExports.SuspendThread);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Thread", "ResumeThread",
            (void*)(delegate* unmanaged<Threading.Thread*, int>)&Exports.DDK.ThreadExports.ResumeThread);
        registered += RegisterDDKMethod(korlib, "ProtonOS.Kernel", "Thread", "GetThreadCount",
            (void*)(delegate* unmanaged<int>)&Exports.DDK.ThreadExports.GetThreadCount);

        if (registered > 0)
        {
            DebugConsole.Write("[Kernel] Registered ");
            DebugConsole.WriteDecimal(registered);
            DebugConsole.WriteLine(" DDK methods in token registry");
        }
    }

    /// <summary>
    /// Build token registry entries for the System.Console and
    /// System.Environment kernel exports (the JIT console bridge).
    ///
    /// In the korlib IL assembly (the copy the JIT loads from disk) these
    /// methods are stubs that throw PlatformNotSupportedException; the real
    /// implementations live in the kernel's [UnmanagedCallersOnly] exports
    /// (ConsoleExports / EnvironmentExports). Mapping the korlib stub method
    /// tokens to the export addresses lets JIT-compiled applications bind
    /// Console I/O to the real implementations - the same mechanism used for
    /// the DDK exports above.
    /// </summary>
    private static void BuildConsoleTokenRegistry()
    {
        if (_korlibId == AssemblyLoader.InvalidAssemblyId)
            return;

        LoadedAssembly* korlib = AssemblyLoader.GetAssembly(_korlibId);
        if (korlib == null)
            return;

        int registered = 0;

        // System.Console -> ConsoleExports
        registered += RegisterDDKMethod(korlib, "System", "Console", "ConsoleWriteChars",
            (void*)(delegate* unmanaged<char*, int, void>)&ConsoleExports.ConsoleWriteChars);
        registered += RegisterDDKMethod(korlib, "System", "Console", "ConsoleReadLine",
            (void*)(delegate* unmanaged<char*, int, int>)&ConsoleExports.ConsoleReadLine);
        registered += RegisterDDKMethod(korlib, "System", "Console", "ConsoleReadKey",
            (void*)(delegate* unmanaged<int, int, char*, int*, int*, int>)&ConsoleExports.ConsoleReadKey);
        registered += RegisterDDKMethod(korlib, "System", "Console", "ConsoleKeyAvailable",
            (void*)(delegate* unmanaged<int>)&ConsoleExports.ConsoleKeyAvailable);
        registered += RegisterDDKMethod(korlib, "System", "Console", "ConsoleReadChar",
            (void*)(delegate* unmanaged<int>)&ConsoleExports.ConsoleReadChar);
        registered += RegisterDDKMethod(korlib, "System", "Console", "ConsoleClear",
            (void*)(delegate* unmanaged<void>)&ConsoleExports.ConsoleClear);
        registered += RegisterDDKMethod(korlib, "System", "Console", "ConsoleSetCursor",
            (void*)(delegate* unmanaged<int, int, void>)&ConsoleExports.ConsoleSetCursor);
        registered += RegisterDDKMethod(korlib, "System", "Console", "ConsoleGetCursor",
            (void*)(delegate* unmanaged<int*, int*, void>)&ConsoleExports.ConsoleGetCursor);
        registered += RegisterDDKMethod(korlib, "System", "Console", "ConsoleGetSize",
            (void*)(delegate* unmanaged<int*, int*, void>)&ConsoleExports.ConsoleGetSize);
        registered += RegisterDDKMethod(korlib, "System", "Console", "ConsoleFlush",
            (void*)(delegate* unmanaged<void>)&ConsoleExports.ConsoleFlush);
        registered += RegisterDDKMethod(korlib, "System", "Console", "ConsoleSetColors",
            (void*)(delegate* unmanaged<int, int, void>)&ConsoleExports.ConsoleSetColors);
        registered += RegisterDDKMethod(korlib, "System", "Console", "ConsoleSetLineMode",
            (void*)(delegate* unmanaged<int, void>)&ConsoleExports.ConsoleSetLineMode);
        registered += RegisterDDKMethod(korlib, "System", "Console", "ConsoleSetCtrlCAsInput",
            (void*)(delegate* unmanaged<int, void>)&ConsoleExports.ConsoleSetCtrlCAsInput);
        registered += RegisterDDKMethod(korlib, "System", "Console", "ConsoleIsRedirected",
            (void*)(delegate* unmanaged<int*, int*, void>)&ConsoleExports.ConsoleIsRedirected);
        registered += RegisterDDKMethod(korlib, "System", "Console", "ConsoleGetTickMs",
            (void*)(delegate* unmanaged<uint>)&ConsoleExports.ConsoleGetTickMs);

        // System.Environment -> EnvironmentExports
        registered += RegisterDDKMethod(korlib, "System", "Environment", "EnvExit",
            (void*)(delegate* unmanaged<int, void>)&EnvironmentExports.EnvExit);
        registered += RegisterDDKMethod(korlib, "System", "Environment", "EnvGetCurrentDirectory",
            (void*)(delegate* unmanaged<char*, int, int>)&EnvironmentExports.EnvGetCurrentDirectory);

        DebugConsole.Write("[Kernel] Registered ");
        DebugConsole.WriteDecimal(registered);
        DebugConsole.WriteLine(" console methods in token registry");
    }

    /// <summary>
    /// Build token registry entries for the System.IO.File and
    /// System.IO.Directory kernel exports (the JIT file bridge).
    ///
    /// In the korlib IL assembly the bridge primitives are stubs that
    /// throw PlatformNotSupportedException; the real implementations are
    /// the [UnmanagedCallersOnly] exports in Platform.FileExports, which
    /// drive the JIT-loaded FAT driver. Mapping the korlib stub method
    /// tokens to the export addresses lets JIT-compiled applications
    /// perform file I/O against the boot volume.
    /// </summary>
    private static void BuildFileTokenRegistry()
    {
        if (_korlibId == AssemblyLoader.InvalidAssemblyId)
            return;

        LoadedAssembly* korlib = AssemblyLoader.GetAssembly(_korlibId);
        if (korlib == null)
            return;

        int registered = 0;

        // System.IO.File -> FileExports
        registered += RegisterDDKMethod(korlib, "System.IO", "File", "FileBootRead",
            (void*)(delegate* unmanaged<char*, int, byte*, int, int>)&Platform.FileExports.FileBootRead);
        registered += RegisterDDKMethod(korlib, "System.IO", "File", "FileBootWrite",
            (void*)(delegate* unmanaged<char*, int, byte*, int, int, int>)&Platform.FileExports.FileBootWrite);
        registered += RegisterDDKMethod(korlib, "System.IO", "File", "FileBootSize",
            (void*)(delegate* unmanaged<char*, int, int>)&Platform.FileExports.FileBootSize);
        registered += RegisterDDKMethod(korlib, "System.IO", "File", "FileBootExists",
            (void*)(delegate* unmanaged<char*, int, int>)&Platform.FileExports.FileBootExists);
        registered += RegisterDDKMethod(korlib, "System.IO", "File", "FileBootDelete",
            (void*)(delegate* unmanaged<char*, int, int>)&Platform.FileExports.FileBootDelete);

        // System.Diagnostics.Stopwatch -> TimeExports (Phase 7)
        registered += RegisterDDKMethod(korlib, "System.Diagnostics", "Stopwatch", "StopwatchBootNs",
            (void*)(delegate* unmanaged<ulong>)&Platform.TimeExports.StopwatchBootNs);

        // System.IO.Directory -> FileExports
        registered += RegisterDDKMethod(korlib, "System.IO", "Directory", "DirBootExists",
            (void*)(delegate* unmanaged<char*, int, int>)&Platform.FileExports.DirBootExists);
        registered += RegisterDDKMethod(korlib, "System.IO", "Directory", "DirBootCreate",
            (void*)(delegate* unmanaged<char*, int, int>)&Platform.FileExports.DirBootCreate);
        registered += RegisterDDKMethod(korlib, "System.IO", "Directory", "DirBootDelete",
            (void*)(delegate* unmanaged<char*, int, int>)&Platform.FileExports.DirBootDelete);
        registered += RegisterDDKMethod(korlib, "System.IO", "Directory", "DirBootEntry",
            (void*)(delegate* unmanaged<char*, int, int, char*, int, int*, int>)&Platform.FileExports.DirBootEntry);

        DebugConsole.Write("[Kernel] Registered ");
        DebugConsole.WriteDecimal(registered);
        DebugConsole.WriteLine(" file methods in token registry");
    }

    /// <summary>
    /// Helper to register a single korlib method (DDK or console) in the
    /// token registry, resolving it by (namespace, type, method name).
    /// </summary>
    private static int RegisterDDKMethod(LoadedAssembly* korlib, string ns, string typeName, string methodName, void* nativeAddr)
    {
        // Find type in korlib
        uint typeToken = AssemblyLoader.FindTypeDefByFullName(_korlibId, ns, typeName);
        if (typeToken == 0)
            return 0;

        // Find method in type
        uint methodToken = AssemblyLoader.FindMethodDefByName(_korlibId, typeToken, methodName);
        if (methodToken == 0)
            return 0;

        // Register in token registry
        AotMethodRegistry.RegisterByToken(_korlibId, methodToken, (nint)nativeAddr, AotMethodFlags.None);
        return 1;
    }

    /// <summary>
    /// Initialize the DDK via JIT compilation.
    /// Finds DDKInit.Initialize() and executes it.
    /// </summary>
    private static void RunDDKInit()
    {
        DebugConsole.WriteLine();
        DebugConsole.WriteLine("==============================");
        DebugConsole.WriteLine("  Initializing DDK");
        DebugConsole.WriteLine("==============================");
        DebugConsole.WriteLine();

        if (_ddkId == AssemblyLoader.InvalidAssemblyId)
        {
            DebugConsole.WriteLine("[DDK] No DDK assembly loaded, skipping initialization");
            return;
        }

        DebugConsole.WriteLine(string.Format("[DDK] Assembly ID: {0}", _ddkId));

        // Find DDKInit type
        uint ddkInitToken = AssemblyLoader.FindTypeDefByFullName(_ddkId, "ProtonOS.DDK", "DDKInit");
        if (ddkInitToken == 0)
        {
            DebugConsole.WriteLine("[DDK] ERROR: Could not find ProtonOS.DDK.DDKInit type");
            return;
        }

        DebugConsole.WriteLine(string.Format("[DDK] Found DDKInit type, token: 0x{0}", ddkInitToken.ToString("X8", null)));

        // Find Initialize method
        uint initializeToken = AssemblyLoader.FindMethodDefByName(_ddkId, ddkInitToken, "Initialize");
        if (initializeToken == 0)
        {
            DebugConsole.WriteLine("[DDK] ERROR: Could not find Initialize method");
            return;
        }

        DebugConsole.WriteLine(string.Format("[DDK] Found Initialize method, token: 0x{0}", initializeToken.ToString("X8", null)));

        // JIT compile the method
        DebugConsole.WriteLine("[DDK] JIT compiling DDKInit.Initialize...");

        var jitResult = Runtime.JIT.Tier0JIT.CompileMethod(_ddkId, initializeToken);
        if (jitResult.Success)
        {
            DebugConsole.WriteLine(string.Format("[DDK] JIT compilation successful, code at 0x{0}", ((ulong)jitResult.CodeAddress).ToString("X", null)));

            // Execute the compiled method (returns bool)
            DebugConsole.WriteLine("[DDK] Executing DDKInit.Initialize()...");

            var funcPtr = (delegate* unmanaged<bool>)jitResult.CodeAddress;
            bool success = funcPtr();

            if (success)
            {
                DebugConsole.WriteLine("[DDK] Initialization successful");
            }
            else
            {
                DebugConsole.WriteLine("[DDK] Initialization failed");
            }
        }
        else
        {
            DebugConsole.WriteLine("[DDK] ERROR: JIT compilation failed");
        }
    }

    /// <summary>
    /// Bind drivers to detected PCI devices.
    /// Iterates through PCI devices and tries to match them with loaded drivers.
    /// </summary>
    private static void BindDrivers()
    {
        DebugConsole.WriteLine();
        DebugConsole.WriteLine("[Drivers] Binding drivers to PCI devices...");

        if (_virtioBlkDriverId == AssemblyLoader.InvalidAssemblyId)
        {
            DebugConsole.WriteLine("[Drivers] No VirtioBlk driver loaded, skipping binding");
            return;
        }

        // Find VirtioBlkEntry type
        uint virtioBlkEntryToken = AssemblyLoader.FindTypeDefByFullName(
            _virtioBlkDriverId, "ProtonOS.Drivers.Storage.VirtioBlk", "VirtioBlkEntry");

        if (virtioBlkEntryToken == 0)
        {
            DebugConsole.WriteLine("[Drivers] ERROR: Could not find VirtioBlkEntry type");
            return;
        }

        // Find Probe method
        uint probeToken = AssemblyLoader.FindMethodDefByName(_virtioBlkDriverId, virtioBlkEntryToken, "Probe");
        if (probeToken == 0)
        {
            DebugConsole.WriteLine("[Drivers] ERROR: Could not find Probe method");
            return;
        }

        // Find Bind method
        uint bindToken = AssemblyLoader.FindMethodDefByName(_virtioBlkDriverId, virtioBlkEntryToken, "Bind");
        if (bindToken == 0)
        {
            DebugConsole.WriteLine("[Drivers] ERROR: Could not find Bind method");
            return;
        }

        DebugConsole.WriteLine(string.Format("[Drivers] Found VirtioBlkEntry (0x{0}) Probe (0x{1}) Bind (0x{2})",
            virtioBlkEntryToken.ToString("X8", null), probeToken.ToString("X8", null), bindToken.ToString("X8", null)));

        // JIT compile Probe method
        DebugConsole.WriteLine("[Drivers] JIT compiling VirtioBlkEntry.Probe...");
        var probeResult = Runtime.JIT.Tier0JIT.CompileMethod(_virtioBlkDriverId, probeToken);
        if (!probeResult.Success)
        {
            DebugConsole.WriteLine("[Drivers] ERROR: Failed to JIT compile Probe");
            return;
        }

        // JIT compile Bind method
        DebugConsole.WriteLine("[Drivers] JIT compiling VirtioBlkEntry.Bind...");
        var bindResult = Runtime.JIT.Tier0JIT.CompileMethod(_virtioBlkDriverId, bindToken);
        if (!bindResult.Success)
        {
            DebugConsole.WriteLine("[Drivers] ERROR: Failed to JIT compile Bind");
            return;
        }

        DebugConsole.WriteLine(string.Format("[Drivers] Probe at 0x{0} Bind at 0x{1}",
            ((ulong)probeResult.CodeAddress).ToString("X", null), ((ulong)bindResult.CodeAddress).ToString("X", null)));

        // Create function pointers
        var probeFunc = (delegate* unmanaged<ushort, ushort, bool>)probeResult.CodeAddress;
        var bindFunc = (delegate* unmanaged<byte, byte, byte, bool>)bindResult.CodeAddress;

        // Iterate through detected PCI devices
        int deviceCount = Platform.PCI.DeviceCount;
        int boundCount = 0;

        DebugConsole.WriteLine(string.Format("[Drivers] Checking {0} PCI device(s)...", deviceCount));

        for (int i = 0; i < deviceCount; i++)
        {
            var device = Platform.PCI.GetDevice(i);
            if (device == null)
                continue;

            // Try VirtioBlk driver
            bool probeSuccess = probeFunc(device->VendorId, device->DeviceId);

            if (probeSuccess)
            {
                // Test string.Format with byte (Bus/Device/Function) and ushort (VendorId/DeviceId)
                DebugConsole.WriteLine(string.Format("[Drivers] VirtioBlk matched {0}:{1}.{2} (Vendor:{3} Device:{4})",
                    device->Bus.ToString("X2", null), device->Device.ToString("X2", null), device->Function.ToString("X2", null),
                    device->VendorId.ToString("X4", null), device->DeviceId.ToString("X4", null)));

                // Bind the driver
                bool bindSuccess = bindFunc(device->Bus, device->Device, device->Function);
                if (bindSuccess)
                {
                    DebugConsole.WriteLine("[Drivers]   Bind successful");
                    boundCount++;
                }
                else
                {
                    DebugConsole.WriteLine("[Drivers]   Bind failed");
                }
            }
        }

        DebugConsole.WriteLine(string.Format("[Drivers] Bound {0} VirtioBlk driver(s)", boundCount));

        // Test virtio I/O if a driver was bound
        // TEMPORARILY DISABLED - VirtioBlk has a crash issue
        // if (boundCount > 0)
        // {
        //     TestVirtioIO(virtioBlkEntryToken);
        // }

        // Now try AHCI driver
        BindAhciDriver();

        // Now try VirtioNet driver
        BindVirtioNetDriver();
    }

    /// <summary>
    /// Bind AHCI driver to detected AHCI controllers.
    /// </summary>
    private static void BindAhciDriver()
    {
        if (_ahciDriverId == AssemblyLoader.InvalidAssemblyId)
        {
            DebugConsole.WriteLine("[Drivers] No AHCI driver loaded, skipping AHCI binding");
            return;
        }

        // Find AhciEntry type
        uint ahciEntryToken = AssemblyLoader.FindTypeDefByFullName(
            _ahciDriverId, "ProtonOS.Drivers.Storage.Ahci", "AhciEntry");

        if (ahciEntryToken == 0)
        {
            DebugConsole.WriteLine("[Drivers] ERROR: Could not find AhciEntry type");
            return;
        }

        // Find Probe method
        uint probeToken = AssemblyLoader.FindMethodDefByName(_ahciDriverId, ahciEntryToken, "Probe");
        if (probeToken == 0)
        {
            DebugConsole.WriteLine("[Drivers] ERROR: Could not find AHCI Probe method");
            return;
        }

        // Find Bind method
        uint bindToken = AssemblyLoader.FindMethodDefByName(_ahciDriverId, ahciEntryToken, "Bind");
        if (bindToken == 0)
        {
            DebugConsole.WriteLine("[Drivers] ERROR: Could not find AHCI Bind method");
            return;
        }

        DebugConsole.WriteLine(string.Format("[Drivers] Found AhciEntry (0x{0}) Probe (0x{1}) Bind (0x{2})",
            ahciEntryToken.ToString("X8", null), probeToken.ToString("X8", null), bindToken.ToString("X8", null)));

        // JIT compile Probe method
        DebugConsole.WriteLine("[Drivers] JIT compiling AhciEntry.Probe...");
        var probeResult = Runtime.JIT.Tier0JIT.CompileMethod(_ahciDriverId, probeToken);
        if (!probeResult.Success)
        {
            DebugConsole.WriteLine("[Drivers] ERROR: Failed to JIT compile AHCI Probe");
            return;
        }

        // JIT compile Bind method
        DebugConsole.WriteLine("[Drivers] JIT compiling AhciEntry.Bind...");
        var bindResult = Runtime.JIT.Tier0JIT.CompileMethod(_ahciDriverId, bindToken);
        if (!bindResult.Success)
        {
            DebugConsole.WriteLine("[Drivers] ERROR: Failed to JIT compile AHCI Bind");
            return;
        }

        DebugConsole.WriteLine(string.Format("[Drivers] AHCI Probe at 0x{0} Bind at 0x{1}",
            ((ulong)probeResult.CodeAddress).ToString("X", null), ((ulong)bindResult.CodeAddress).ToString("X", null)));

        // Create function pointers - AHCI Probe takes (vendorId, deviceId, classCode, subclassCode, progIf)
        var probeFunc = (delegate* unmanaged<ushort, ushort, byte, byte, byte, bool>)probeResult.CodeAddress;
        var bindFunc = (delegate* unmanaged<byte, byte, byte, bool>)bindResult.CodeAddress;

        // Iterate through detected PCI devices
        int deviceCount = Platform.PCI.DeviceCount;
        int boundCount = 0;

        DebugConsole.WriteLine(string.Format("[Drivers] Checking {0} PCI device(s) for AHCI...", deviceCount));

        for (int i = 0; i < deviceCount; i++)
        {
            var device = Platform.PCI.GetDevice(i);
            if (device == null)
                continue;

            // Try AHCI driver - pass class code info
            bool probeSuccess = probeFunc(device->VendorId, device->DeviceId,
                device->BaseClass, device->SubClass, device->ProgIF);

            if (probeSuccess)
            {
                DebugConsole.WriteLine(string.Format("[Drivers] AHCI matched {0}:{1}.{2} (Class:{3}/{4}/{5})",
                    device->Bus.ToString("X2", null), device->Device.ToString("X2", null), device->Function.ToString("X2", null),
                    device->BaseClass.ToString("X2", null), device->SubClass.ToString("X2", null), device->ProgIF.ToString("X2", null)));

                // Bind the driver
                bool bindSuccess = bindFunc(device->Bus, device->Device, device->Function);
                if (bindSuccess)
                {
                    DebugConsole.WriteLine("[Drivers]   AHCI Bind successful");
                    boundCount++;
                }
                else
                {
                    DebugConsole.WriteLine("[Drivers]   AHCI Bind failed");
                }
            }
        }

        DebugConsole.WriteLine(string.Format("[Drivers] Bound {0} AHCI driver(s)", boundCount));

        // Test AHCI I/O if a driver was bound
        if (boundCount > 0)
        {
            TestAhciIO(ahciEntryToken);
        }
    }

    /// <summary>
    /// Bind VirtioNet driver to detected virtio network devices.
    /// </summary>
    private static void BindVirtioNetDriver()
    {
        if (_virtioNetDriverId == AssemblyLoader.InvalidAssemblyId)
        {
            DebugConsole.WriteLine("[Drivers] No VirtioNet driver loaded, skipping VirtioNet binding");
            return;
        }

        // Find VirtioNetEntry type
        uint virtioNetEntryToken = AssemblyLoader.FindTypeDefByFullName(
            _virtioNetDriverId, "ProtonOS.Drivers.Network.VirtioNet", "VirtioNetEntry");

        if (virtioNetEntryToken == 0)
        {
            DebugConsole.WriteLine("[Drivers] ERROR: Could not find VirtioNetEntry type");
            return;
        }

        // Find Probe method
        uint probeToken = AssemblyLoader.FindMethodDefByName(_virtioNetDriverId, virtioNetEntryToken, "Probe");
        if (probeToken == 0)
        {
            DebugConsole.WriteLine("[Drivers] ERROR: Could not find VirtioNet Probe method");
            return;
        }

        // Find Bind method
        uint bindToken = AssemblyLoader.FindMethodDefByName(_virtioNetDriverId, virtioNetEntryToken, "Bind");
        if (bindToken == 0)
        {
            DebugConsole.WriteLine("[Drivers] ERROR: Could not find VirtioNet Bind method");
            return;
        }

        DebugConsole.WriteLine(string.Format("[Drivers] Found VirtioNetEntry (0x{0}) Probe (0x{1}) Bind (0x{2})",
            virtioNetEntryToken.ToString("X8", null), probeToken.ToString("X8", null), bindToken.ToString("X8", null)));

        // JIT compile Probe method
        DebugConsole.WriteLine("[Drivers] JIT compiling VirtioNetEntry.Probe...");
        var probeResult = Runtime.JIT.Tier0JIT.CompileMethod(_virtioNetDriverId, probeToken);
        if (!probeResult.Success)
        {
            DebugConsole.WriteLine("[Drivers] ERROR: Failed to JIT compile VirtioNet Probe");
            return;
        }

        // JIT compile Bind method
        DebugConsole.WriteLine("[Drivers] JIT compiling VirtioNetEntry.Bind...");
        var bindResult = Runtime.JIT.Tier0JIT.CompileMethod(_virtioNetDriverId, bindToken);
        if (!bindResult.Success)
        {
            DebugConsole.WriteLine("[Drivers] ERROR: Failed to JIT compile VirtioNet Bind");
            return;
        }

        DebugConsole.WriteLine(string.Format("[Drivers] VirtioNet Probe at 0x{0} Bind at 0x{1}",
            ((ulong)probeResult.CodeAddress).ToString("X", null), ((ulong)bindResult.CodeAddress).ToString("X", null)));

        // Create function pointers
        var probeFunc = (delegate* unmanaged<ushort, ushort, bool>)probeResult.CodeAddress;
        var bindFunc = (delegate* unmanaged<byte, byte, byte, bool>)bindResult.CodeAddress;

        // Iterate through detected PCI devices
        int deviceCount = Platform.PCI.DeviceCount;
        int boundCount = 0;

        DebugConsole.WriteLine(string.Format("[Drivers] Checking {0} PCI device(s) for VirtioNet...", deviceCount));

        for (int i = 0; i < deviceCount; i++)
        {
            var device = Platform.PCI.GetDevice(i);
            if (device == null)
                continue;

            // Try VirtioNet driver
            bool probeSuccess = probeFunc(device->VendorId, device->DeviceId);

            if (probeSuccess)
            {
                DebugConsole.WriteLine(string.Format("[Drivers] VirtioNet matched {0}:{1}.{2} (Vendor:{3} Device:{4})",
                    device->Bus.ToString("X2", null), device->Device.ToString("X2", null), device->Function.ToString("X2", null),
                    device->VendorId.ToString("X4", null), device->DeviceId.ToString("X4", null)));

                // Bind the driver
                bool bindSuccess = bindFunc(device->Bus, device->Device, device->Function);
                if (bindSuccess)
                {
                    DebugConsole.WriteLine("[Drivers]   VirtioNet Bind successful");

                    // Disable PCI INTx (command register bit 10). The
                    // virtio-net driver polls its queues and never reads
                    // the device ISR to deassert the interrupt line, so a
                    // level-triggered INTx keeps re-firing as an
                    // "Unhandled interrupt" - measured to slow every
                    // later operation (and JIT compile) down ~20x.
                    ushort pciCmd = Platform.PCI.ReadConfig16(
                        device->Bus, device->Device, device->Function, 0x04);
                    Platform.PCI.WriteConfig16(
                        device->Bus, device->Device, device->Function, 0x04,
                        (ushort)(pciCmd | 0x0400));

                    boundCount++;
                }
                else
                {
                    DebugConsole.WriteLine("[Drivers]   VirtioNet Bind failed");
                }
            }
        }

        DebugConsole.WriteLine(string.Format("[Drivers] Bound {0} VirtioNet driver(s)", boundCount));

        // Capture the driver's frame pump for the shell's network
        // utilities and materialize the network stack + interface (see
        // Platform.NetworkBridge).
        if (boundCount > 0)
            Platform.NetworkBridge.Register(_virtioNetDriverId, virtioNetEntryToken);

        // Test network I/O if a driver was bound. The in-kernel network
        // self-tests are gated with the skip-boot-tests marker: the
        // Phase 5 NIC session validates the stack through the shell
        // utilities, and the legacy tests JIT-saturate the runtime
        // (thousands of methods) which slows later compiles down a lot.
        if (boundCount > 0
            && BootInfoAccess.FindFile("skip-boot-tests", out ulong _skipNetTestSize) == null)
        {
            TestVirtioNetIO(virtioNetEntryToken);
        }
    }

    /// <summary>
    /// Test virtio network device I/O.
    /// </summary>
    private static void TestVirtioNetIO(uint virtioNetEntryToken)
    {
        DebugConsole.WriteLine();
        DebugConsole.WriteLine("[VirtioNet] Testing virtio network device...");

        // Run unit tests first
        uint unitTestToken = AssemblyLoader.FindMethodDefByName(_virtioNetDriverId, virtioNetEntryToken, "RunUnitTests");
        if (unitTestToken != 0)
        {
            DebugConsole.WriteLine("[VirtioNet] JIT compiling VirtioNetEntry.RunUnitTests...");
            var unitTestResult = Runtime.JIT.Tier0JIT.CompileMethod(_virtioNetDriverId, unitTestToken);
            if (unitTestResult.Success)
            {
                var unitTestFunc = (delegate* unmanaged<int>)unitTestResult.CodeAddress;
                int unitResult = unitTestFunc();
                if (unitResult != 1)
                {
                    DebugConsole.WriteLine("[VirtioNet] ERROR: Unit tests failed!");
                    return;
                }
                DebugConsole.WriteLine("[VirtioNet] Unit tests passed");
            }
        }

        // Find TestSend method
        uint testSendToken = AssemblyLoader.FindMethodDefByName(_virtioNetDriverId, virtioNetEntryToken, "TestSend");
        if (testSendToken == 0)
        {
            DebugConsole.WriteLine("[VirtioNet] ERROR: Could not find TestSend method");
            return;
        }

        // JIT compile TestSend method
        DebugConsole.WriteLine("[VirtioNet] JIT compiling VirtioNetEntry.TestSend...");
        var testSendResult = Runtime.JIT.Tier0JIT.CompileMethod(_virtioNetDriverId, testSendToken);
        if (!testSendResult.Success)
        {
            DebugConsole.WriteLine("[VirtioNet] ERROR: Failed to JIT compile TestSend");
            return;
        }

        // Call TestSend
        var testSendFunc = (delegate* unmanaged<int>)testSendResult.CodeAddress;
        int sendResult = testSendFunc();
        DebugConsole.WriteLine(string.Format("[VirtioNet] TestSend returned {0}", sendResult));

        // Find TestReceive method
        uint testReceiveToken = AssemblyLoader.FindMethodDefByName(_virtioNetDriverId, virtioNetEntryToken, "TestReceive");
        if (testReceiveToken == 0)
        {
            DebugConsole.WriteLine("[VirtioNet] ERROR: Could not find TestReceive method");
            return;
        }

        // JIT compile TestReceive method
        DebugConsole.WriteLine("[VirtioNet] JIT compiling VirtioNetEntry.TestReceive...");
        var testReceiveResult = Runtime.JIT.Tier0JIT.CompileMethod(_virtioNetDriverId, testReceiveToken);
        if (!testReceiveResult.Success)
        {
            DebugConsole.WriteLine("[VirtioNet] ERROR: Failed to JIT compile TestReceive");
            return;
        }

        // Call TestReceive
        var testReceiveFunc = (delegate* unmanaged<int>)testReceiveResult.CodeAddress;
        int receiveResult = testReceiveFunc();
        DebugConsole.WriteLine(string.Format("[VirtioNet] TestReceive returned {0}", receiveResult));

        // Find TestNetworkStack method
        uint testNetStackToken = AssemblyLoader.FindMethodDefByName(_virtioNetDriverId, virtioNetEntryToken, "TestNetworkStack");
        if (testNetStackToken == 0)
        {
            DebugConsole.WriteLine("[VirtioNet] ERROR: Could not find TestNetworkStack method");
            return;
        }

        // JIT compile TestNetworkStack method
        DebugConsole.WriteLine("[VirtioNet] JIT compiling VirtioNetEntry.TestNetworkStack...");
        var testNetStackResult = Runtime.JIT.Tier0JIT.CompileMethod(_virtioNetDriverId, testNetStackToken);
        if (!testNetStackResult.Success)
        {
            DebugConsole.WriteLine("[VirtioNet] ERROR: Failed to JIT compile TestNetworkStack");
            return;
        }

        // Call TestNetworkStack
        var testNetStackFunc = (delegate* unmanaged<int>)testNetStackResult.CodeAddress;
        int netStackResult = testNetStackFunc();
        DebugConsole.WriteLine(string.Format("[VirtioNet] TestNetworkStack returned {0}", netStackResult));

        // Find TestPing method
        uint testPingToken = AssemblyLoader.FindMethodDefByName(_virtioNetDriverId, virtioNetEntryToken, "TestPing");
        if (testPingToken == 0)
        {
            DebugConsole.WriteLine("[VirtioNet] ERROR: Could not find TestPing method");
            return;
        }

        // JIT compile TestPing method
        DebugConsole.WriteLine("[VirtioNet] JIT compiling VirtioNetEntry.TestPing...");
        var testPingResult = Runtime.JIT.Tier0JIT.CompileMethod(_virtioNetDriverId, testPingToken);
        if (!testPingResult.Success)
        {
            DebugConsole.WriteLine("[VirtioNet] ERROR: Failed to JIT compile TestPing");
            return;
        }

        // Call TestPing
        var testPingFunc = (delegate* unmanaged<int>)testPingResult.CodeAddress;
        int pingResult = testPingFunc();
        DebugConsole.WriteLine(string.Format("[VirtioNet] TestPing returned {0}", pingResult));

        // Find TestUdp method
        uint testUdpToken = AssemblyLoader.FindMethodDefByName(_virtioNetDriverId, virtioNetEntryToken, "TestUdp");
        if (testUdpToken == 0)
        {
            DebugConsole.WriteLine("[VirtioNet] ERROR: Could not find TestUdp method");
            return;
        }

        // JIT compile TestUdp method
        DebugConsole.WriteLine("[VirtioNet] JIT compiling VirtioNetEntry.TestUdp...");
        var testUdpResult = Runtime.JIT.Tier0JIT.CompileMethod(_virtioNetDriverId, testUdpToken);
        if (!testUdpResult.Success)
        {
            DebugConsole.WriteLine("[VirtioNet] ERROR: Failed to JIT compile TestUdp");
            return;
        }

        // Call TestUdp
        var testUdpFunc = (delegate* unmanaged<int>)testUdpResult.CodeAddress;
        int udpResult = testUdpFunc();
        DebugConsole.WriteLine(string.Format("[VirtioNet] TestUdp returned {0}", udpResult));

        // Find TestTcp method
        uint testTcpToken = AssemblyLoader.FindMethodDefByName(_virtioNetDriverId, virtioNetEntryToken, "TestTcp");
        if (testTcpToken == 0)
        {
            DebugConsole.WriteLine("[VirtioNet] ERROR: Could not find TestTcp method");
            return;
        }

        // JIT compile TestTcp method
        DebugConsole.WriteLine("[VirtioNet] JIT compiling VirtioNetEntry.TestTcp...");
        var testTcpResult = Runtime.JIT.Tier0JIT.CompileMethod(_virtioNetDriverId, testTcpToken);
        if (!testTcpResult.Success)
        {
            DebugConsole.WriteLine("[VirtioNet] ERROR: Failed to JIT compile TestTcp");
            return;
        }

        // Call TestTcp
        var testTcpFunc = (delegate* unmanaged<int>)testTcpResult.CodeAddress;
        int tcpResult = testTcpFunc();
        DebugConsole.WriteLine(string.Format("[VirtioNet] TestTcp returned {0}", tcpResult));
    }

    /// <summary>
    /// Test AOT→JIT compilation capability by compiling a method from korlib.dll.
    /// </summary>
    private static void TestAotToJitCompilation()
    {
        DebugConsole.WriteLine();
        DebugConsole.WriteLine("[AOT→JIT] Testing AOT to JIT compilation...");

        // Try to compile Environment.get_NewLine from korlib.dll
        // This is a simple method that just returns a string constant
        void* code = ProtonOS.Runtime.JIT.Tier0JIT.CompileKorlibMethod("System", "Environment", "get_NewLine");
        if (code != null)
        {
            DebugConsole.Write("[AOT→JIT] SUCCESS: Compiled Environment.get_NewLine at 0x");
            DebugConsole.WriteHex((ulong)code);
            DebugConsole.WriteLine();

            // Try to call it! The signature is: string get_NewLine()
            // We'll use a function pointer to call the JIT-compiled code
            var getNewLine = (delegate*<string>)code;
            string newLine = getNewLine();
            if (newLine != null)
            {
                DebugConsole.Write("[AOT→JIT] Called get_NewLine, got string of length ");
                DebugConsole.WriteDecimal((uint)newLine.Length);
                DebugConsole.WriteLine();
            }
            else
            {
                DebugConsole.WriteLine("[AOT→JIT] WARNING: get_NewLine returned null");
            }
        }
        else
        {
            DebugConsole.WriteLine("[AOT→JIT] FAILED: Could not compile Environment.get_NewLine");
        }

        DebugConsole.WriteLine("[AOT→JIT] Test complete");
        DebugConsole.WriteLine();
    }

    /// <summary>
    /// Test AHCI block device I/O.
    /// </summary>
    private static void TestAhciIO(uint ahciEntryToken)
    {
        DebugConsole.WriteLine();
        DebugConsole.WriteLine("[AhciIO] Testing AHCI block device...");

        // Find TestRead method
        uint testReadToken = AssemblyLoader.FindMethodDefByName(_ahciDriverId, ahciEntryToken, "TestRead");
        if (testReadToken == 0)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Could not find TestRead method");
            return;
        }

        // JIT compile TestRead method
        DebugConsole.WriteLine("[AhciIO] JIT compiling AhciEntry.TestRead...");
        var testReadResult = Runtime.JIT.Tier0JIT.CompileMethod(_ahciDriverId, testReadToken);
        if (!testReadResult.Success)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Failed to JIT compile TestRead");
            return;
        }

        // Call TestRead
        var testReadFunc = (delegate* unmanaged<int>)testReadResult.CodeAddress;
        int readResult = testReadFunc();
        DebugConsole.WriteLine(string.Format("[AhciIO] TestRead returned {0}", readResult));

        // Find TestWrite method
        uint testWriteToken = AssemblyLoader.FindMethodDefByName(_ahciDriverId, ahciEntryToken, "TestWrite");
        if (testWriteToken == 0)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Could not find TestWrite method");
            return;
        }

        // JIT compile TestWrite method
        DebugConsole.WriteLine("[AhciIO] JIT compiling AhciEntry.TestWrite...");
        var testWriteResult = Runtime.JIT.Tier0JIT.CompileMethod(_ahciDriverId, testWriteToken);
        if (!testWriteResult.Success)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Failed to JIT compile TestWrite");
            return;
        }

        // Call TestWrite
        var testWriteFunc = (delegate* unmanaged<int>)testWriteResult.CodeAddress;
        int writeResult = testWriteFunc();
        DebugConsole.WriteLine(string.Format("[AhciIO] TestWrite returned {0}", writeResult));

        // Test FAT mount on SATA device
        uint testFatMountToken = AssemblyLoader.FindMethodDefByName(_ahciDriverId, ahciEntryToken, "TestFatMount");
        if (testFatMountToken == 0)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Could not find TestFatMount method");
            return;
        }

        DebugConsole.WriteLine("[AhciIO] JIT compiling AhciEntry.TestFatMount...");
        var testFatMountResult = Runtime.JIT.Tier0JIT.CompileMethod(_ahciDriverId, testFatMountToken);
        if (!testFatMountResult.Success)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Failed to JIT compile TestFatMount");
            return;
        }

        var testFatMountFunc = (delegate* unmanaged<int>)testFatMountResult.CodeAddress;
        int fatMountResult = testFatMountFunc();
        DebugConsole.WriteLine(string.Format("[AhciIO] TestFatMount returned {0}", fatMountResult));

        // Test FAT file read on SATA device
        uint testFatReadToken = AssemblyLoader.FindMethodDefByName(_ahciDriverId, ahciEntryToken, "TestFatReadFile");
        if (testFatReadToken == 0)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Could not find TestFatReadFile method");
            return;
        }

        DebugConsole.WriteLine("[AhciIO] JIT compiling AhciEntry.TestFatReadFile...");
        var testFatReadResult = Runtime.JIT.Tier0JIT.CompileMethod(_ahciDriverId, testFatReadToken);
        if (!testFatReadResult.Success)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Failed to JIT compile TestFatReadFile");
            return;
        }

        var testFatReadFunc = (delegate* unmanaged<int>)testFatReadResult.CodeAddress;
        int fatReadResult = testFatReadFunc();
        DebugConsole.WriteLine(string.Format("[AhciIO] TestFatReadFile returned {0}", fatReadResult));

        // Test EXT2 mount on SATA device
        uint testExt2MountToken = AssemblyLoader.FindMethodDefByName(_ahciDriverId, ahciEntryToken, "TestExt2Mount");
        if (testExt2MountToken == 0)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Could not find TestExt2Mount method");
            return;
        }

        DebugConsole.WriteLine("[AhciIO] JIT compiling AhciEntry.TestExt2Mount...");
        var testExt2MountResult = Runtime.JIT.Tier0JIT.CompileMethod(_ahciDriverId, testExt2MountToken);
        if (!testExt2MountResult.Success)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Failed to JIT compile TestExt2Mount");
            return;
        }

        var testExt2MountFunc = (delegate* unmanaged<int>)testExt2MountResult.CodeAddress;
        int ext2MountResult = testExt2MountFunc();
        DebugConsole.WriteLine(string.Format("[AhciIO] TestExt2Mount returned {0}", ext2MountResult));

        // Test EXT2 file read on SATA device
        uint testExt2ReadToken = AssemblyLoader.FindMethodDefByName(_ahciDriverId, ahciEntryToken, "TestExt2ReadFile");
        if (testExt2ReadToken == 0)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Could not find TestExt2ReadFile method");
            return;
        }

        DebugConsole.WriteLine("[AhciIO] JIT compiling AhciEntry.TestExt2ReadFile...");
        var testExt2ReadResult = Runtime.JIT.Tier0JIT.CompileMethod(_ahciDriverId, testExt2ReadToken);
        if (!testExt2ReadResult.Success)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Failed to JIT compile TestExt2ReadFile");
            return;
        }

        var testExt2ReadFunc = (delegate* unmanaged<int>)testExt2ReadResult.CodeAddress;
        int ext2ReadResult = testExt2ReadFunc();
        DebugConsole.WriteLine(string.Format("[AhciIO] TestExt2ReadFile returned {0}", ext2ReadResult));

        // Test EXT2 file write on SATA device
        uint testExt2WriteToken = AssemblyLoader.FindMethodDefByName(_ahciDriverId, ahciEntryToken, "TestExt2WriteFile");
        if (testExt2WriteToken == 0)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Could not find TestExt2WriteFile method");
            return;
        }

        DebugConsole.WriteLine("[AhciIO] JIT compiling AhciEntry.TestExt2WriteFile...");
        var testExt2WriteResult = Runtime.JIT.Tier0JIT.CompileMethod(_ahciDriverId, testExt2WriteToken);
        if (!testExt2WriteResult.Success)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Failed to JIT compile TestExt2WriteFile");
            return;
        }

        var testExt2WriteFunc = (delegate* unmanaged<int>)testExt2WriteResult.CodeAddress;
        int ext2WriteResult = testExt2WriteFunc();
        DebugConsole.WriteLine(string.Format("[AhciIO] TestExt2WriteFile returned {0}", ext2WriteResult));

        // Test FAT CreateDirectory
        uint testFatCreateDirToken = AssemblyLoader.FindMethodDefByName(_ahciDriverId, ahciEntryToken, "TestFatCreateDirectory");
        if (testFatCreateDirToken == 0)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Could not find TestFatCreateDirectory method");
            return;
        }

        DebugConsole.WriteLine("[AhciIO] JIT compiling AhciEntry.TestFatCreateDirectory...");
        var testFatCreateDirResult = Runtime.JIT.Tier0JIT.CompileMethod(_ahciDriverId, testFatCreateDirToken);
        if (!testFatCreateDirResult.Success)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Failed to JIT compile TestFatCreateDirectory");
            return;
        }

        var testFatCreateDirFunc = (delegate* unmanaged<int>)testFatCreateDirResult.CodeAddress;
        int fatCreateDirResult = testFatCreateDirFunc();
        DebugConsole.WriteLine(string.Format("[AhciIO] TestFatCreateDirectory returned {0}", fatCreateDirResult));

        // Test FAT CreateFile/DeleteFile
        uint testFatCreateFileToken = AssemblyLoader.FindMethodDefByName(_ahciDriverId, ahciEntryToken, "TestFatCreateDeleteFile");
        if (testFatCreateFileToken == 0)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Could not find TestFatCreateDeleteFile method");
            return;
        }

        DebugConsole.WriteLine("[AhciIO] JIT compiling AhciEntry.TestFatCreateDeleteFile...");
        var testFatCreateFileResult = Runtime.JIT.Tier0JIT.CompileMethod(_ahciDriverId, testFatCreateFileToken);
        if (!testFatCreateFileResult.Success)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Failed to JIT compile TestFatCreateDeleteFile");
            return;
        }

        var testFatCreateFileFunc = (delegate* unmanaged<int>)testFatCreateFileResult.CodeAddress;
        int fatCreateFileResult = testFatCreateFileFunc();
        DebugConsole.WriteLine(string.Format("[AhciIO] TestFatCreateDeleteFile returned {0}", fatCreateFileResult));

        // Test ext2 CreateDirectory
        uint testExt2CreateDirToken = AssemblyLoader.FindMethodDefByName(_ahciDriverId, ahciEntryToken, "TestExt2CreateDirectory");
        if (testExt2CreateDirToken == 0)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Could not find TestExt2CreateDirectory method");
            return;
        }

        DebugConsole.WriteLine("[AhciIO] JIT compiling AhciEntry.TestExt2CreateDirectory...");
        var testExt2CreateDirResult = Runtime.JIT.Tier0JIT.CompileMethod(_ahciDriverId, testExt2CreateDirToken);
        if (!testExt2CreateDirResult.Success)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Failed to JIT compile TestExt2CreateDirectory");
            return;
        }

        var testExt2CreateDirFunc = (delegate* unmanaged<int>)testExt2CreateDirResult.CodeAddress;
        int ext2CreateDirResult = testExt2CreateDirFunc();
        DebugConsole.WriteLine(string.Format("[AhciIO] TestExt2CreateDirectory returned {0}", ext2CreateDirResult));

        // Test ext2 CreateFile/DeleteFile
        uint testExt2CreateFileToken = AssemblyLoader.FindMethodDefByName(_ahciDriverId, ahciEntryToken, "TestExt2CreateDeleteFile");
        if (testExt2CreateFileToken == 0)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Could not find TestExt2CreateDeleteFile method");
            return;
        }

        DebugConsole.WriteLine("[AhciIO] JIT compiling AhciEntry.TestExt2CreateDeleteFile...");
        var testExt2CreateFileResult = Runtime.JIT.Tier0JIT.CompileMethod(_ahciDriverId, testExt2CreateFileToken);
        if (!testExt2CreateFileResult.Success)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Failed to JIT compile TestExt2CreateDeleteFile");
            return;
        }

        var testExt2CreateFileFunc = (delegate* unmanaged<int>)testExt2CreateFileResult.CodeAddress;
        int ext2CreateFileResult = testExt2CreateFileFunc();
        DebugConsole.WriteLine(string.Format("[AhciIO] TestExt2CreateDeleteFile returned {0}", ext2CreateFileResult));

        // Test struct field access (diagnose JIT issues with Pack=1 structs)
        uint testStructToken = AssemblyLoader.FindMethodDefByName(_ahciDriverId, ahciEntryToken, "TestStructFieldAccess");
        if (testStructToken == 0)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Could not find TestStructFieldAccess method");
            return;
        }

        DebugConsole.WriteLine("[AhciIO] JIT compiling AhciEntry.TestStructFieldAccess...");
        var testStructResult = Runtime.JIT.Tier0JIT.CompileMethod(_ahciDriverId, testStructToken);
        if (!testStructResult.Success)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Failed to JIT compile TestStructFieldAccess");
            return;
        }

        var testStructFunc = (delegate* unmanaged<int>)testStructResult.CodeAddress;
        int structTestResult = testStructFunc();
        DebugConsole.WriteLine(string.Format("[AhciIO] TestStructFieldAccess returned {0}", structTestResult));

        // Test AtaIdentifyData field offsets (fixed buffer investigation)
        uint testIdentifyToken = AssemblyLoader.FindMethodDefByName(_ahciDriverId, ahciEntryToken, "TestIdentifyOffsets");
        if (testIdentifyToken == 0)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Could not find TestIdentifyOffsets method");
            return;
        }

        DebugConsole.WriteLine("[AhciIO] JIT compiling AhciEntry.TestIdentifyOffsets...");
        var testIdentifyResult = Runtime.JIT.Tier0JIT.CompileMethod(_ahciDriverId, testIdentifyToken);
        if (!testIdentifyResult.Success)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Failed to JIT compile TestIdentifyOffsets");
            return;
        }

        var testIdentifyFunc = (delegate* unmanaged<int>)testIdentifyResult.CodeAddress;
        int identifyTestResult = testIdentifyFunc();
        DebugConsole.WriteLine(string.Format("[AhciIO] TestIdentifyOffsets returned {0}", identifyTestResult));

        // Test VFS root mount with EXT2
        uint testVfsToken = AssemblyLoader.FindMethodDefByName(_ahciDriverId, ahciEntryToken, "TestVfsRootMount");
        if (testVfsToken == 0)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Could not find TestVfsRootMount method");
            return;
        }

        DebugConsole.WriteLine("[AhciIO] JIT compiling AhciEntry.TestVfsRootMount...");
        var testVfsResult = Runtime.JIT.Tier0JIT.CompileMethod(_ahciDriverId, testVfsToken);
        if (!testVfsResult.Success)
        {
            DebugConsole.WriteLine("[AhciIO] ERROR: Failed to JIT compile TestVfsRootMount");
            return;
        }

        var testVfsFunc = (delegate* unmanaged<int>)testVfsResult.CodeAddress;
        int vfsTestResult = testVfsFunc();
        DebugConsole.WriteLine(string.Format("[AhciIO] TestVfsRootMount returned {0}", vfsTestResult));

        // Mount root filesystem (persistent mount)
        DebugConsole.WriteLine();
        DebugConsole.WriteLine("==============================");
        DebugConsole.WriteLine("  Mounting Root Filesystem");
        DebugConsole.WriteLine("==============================");

        uint mountRootToken = AssemblyLoader.FindMethodDefByName(_ahciDriverId, ahciEntryToken, "MountRootFilesystem");
        if (mountRootToken == 0)
        {
            DebugConsole.WriteLine("[Root] ERROR: Could not find MountRootFilesystem method");
            return;
        }

        DebugConsole.WriteLine("[Root] JIT compiling AhciEntry.MountRootFilesystem...");
        var mountRootResult = Runtime.JIT.Tier0JIT.CompileMethod(_ahciDriverId, mountRootToken);
        if (!mountRootResult.Success)
        {
            DebugConsole.WriteLine("[Root] ERROR: Failed to JIT compile MountRootFilesystem");
            return;
        }

        var mountRootFunc = (delegate* unmanaged<int>)mountRootResult.CodeAddress;
        int mountResult = mountRootFunc();

        if (mountResult != 1)
        {
            DebugConsole.WriteLine("[Root] ERROR: Failed to mount root filesystem");
            return;
        }

        DebugConsole.WriteLine("[Root] Root filesystem ready");

        // Mount boot filesystem (FAT) at /boot
        uint mountBootToken = AssemblyLoader.FindMethodDefByName(_ahciDriverId, ahciEntryToken, "MountBootFilesystem");
        if (mountBootToken == 0)
        {
            DebugConsole.WriteLine("[Boot] WARNING: Could not find MountBootFilesystem method");
            // Continue without /boot - not fatal
        }
        else
        {
            DebugConsole.WriteLine("[Boot] JIT compiling AhciEntry.MountBootFilesystem...");
            var mountBootResult = Runtime.JIT.Tier0JIT.CompileMethod(_ahciDriverId, mountBootToken);
            if (!mountBootResult.Success)
            {
                DebugConsole.WriteLine("[Boot] WARNING: Failed to JIT compile MountBootFilesystem");
            }
            else
            {
                var mountBootFunc = (delegate* unmanaged<int>)mountBootResult.CodeAddress;
                int bootMountResult = mountBootFunc();

                if (bootMountResult != 1)
                {
                    DebugConsole.WriteLine("[Boot] WARNING: Failed to mount boot filesystem");
                }
                else
                {
                    DebugConsole.WriteLine("[Boot] Boot filesystem ready");
                }
            }
        }

        // Load drivers from /drivers on root filesystem (via JIT-compiled driver code)
        LoadDriversFromFilesystem(ahciEntryToken);
    }

    /// <summary>
    /// Load drivers from the /drivers directory on the root filesystem.
    /// This calls into JIT-compiled code that has access to VFS.
    /// </summary>
    private static void LoadDriversFromFilesystem(uint ahciEntryToken)
    {
        DebugConsole.WriteLine();
        DebugConsole.WriteLine("==============================");
        DebugConsole.WriteLine("  Loading Drivers from VFS");
        DebugConsole.WriteLine("==============================");
        DebugConsole.WriteLine();

        // Find LoadDrivers method in AhciEntry (JIT-compiled, has VFS access)
        uint loadDriversToken = AssemblyLoader.FindMethodDefByName(_ahciDriverId, ahciEntryToken, "LoadDrivers");
        if (loadDriversToken == 0)
        {
            DebugConsole.WriteLine("[Drivers] LoadDrivers method not found in AhciEntry");
            return;
        }

        // JIT compile the method
        var jitResult = Runtime.JIT.Tier0JIT.CompileMethod(_ahciDriverId, loadDriversToken);
        if (!jitResult.Success)
        {
            DebugConsole.WriteLine("[Drivers] Failed to JIT compile LoadDrivers");
            return;
        }

        // Call it - returns number of drivers loaded
        var loadDriversFunc = (delegate* unmanaged<int>)jitResult.CodeAddress;
        int loaded = loadDriversFunc();

        DebugConsole.Write("[Drivers] Loaded ");
        DebugConsole.WriteDecimal(loaded);
        DebugConsole.WriteLine(" driver(s) from /drivers");
    }

    /// <summary>
    /// Dump the memory map from BootInfo to analyze fragmentation.
    /// </summary>
    private static void DumpMemoryMap(BootInfo* bootInfo)
    {
        DebugConsole.WriteLine();
        DebugConsole.WriteLine("==============================");
        DebugConsole.WriteLine("  Memory Map from Bootloader");
        DebugConsole.WriteLine("==============================");
        DebugConsole.WriteLine();

        byte* memoryMap = (byte*)bootInfo->MemoryMapAddress;
        int entryCount = (int)bootInfo->MemoryMapEntries;
        ulong descriptorSize = bootInfo->MemoryMapEntrySize;

        if (memoryMap == null || entryCount == 0)
        {
            DebugConsole.WriteLine("[MemMap] No memory map available!");
            return;
        }

        DebugConsole.Write("[MemMap] ");
        DebugConsole.WriteDecimal(entryCount);
        DebugConsole.Write(" entries, descriptor size=");
        DebugConsole.WriteDecimal((int)descriptorSize);
        DebugConsole.WriteLine();
        DebugConsole.WriteLine();

        // Statistics
        ulong totalConventional = 0;
        ulong totalUsed = 0;
        int conventionalRegions = 0;
        ulong smallestConventional = ulong.MaxValue;
        ulong largestConventional = 0;

        // Print header
        DebugConsole.WriteLine("  Start            End              Size        Type");
        DebugConsole.WriteLine("  ---------------  ---------------  ----------  ----------------");

        for (int i = 0; i < entryCount; i++)
        {
            var desc = UEFIBoot.GetDescriptor(memoryMap, descriptorSize, i);
            ulong start = desc->PhysicalStart;
            ulong size = desc->NumberOfPages * 4096;
            ulong end = start + size;

            // Print address range
            DebugConsole.Write("  0x");
            DebugConsole.WriteHex(start);
            DebugConsole.Write("  0x");
            DebugConsole.WriteHex(end);
            DebugConsole.Write("  ");

            // Print size in human-readable format
            if (size >= 1024 * 1024)
            {
                DebugConsole.WriteDecimal((uint)(size / (1024 * 1024)));
                DebugConsole.Write(" MB");
            }
            else if (size >= 1024)
            {
                DebugConsole.WriteDecimal((uint)(size / 1024));
                DebugConsole.Write(" KB");
            }
            else
            {
                DebugConsole.WriteDecimal((uint)size);
                DebugConsole.Write(" B ");
            }

            // Pad to 10 chars
            DebugConsole.Write("     ");

            // Print type name
            string typeName = GetMemoryTypeName(desc->Type);
            DebugConsole.Write(typeName);

            // Mark important types
            if (desc->Type == EFIMemoryType.ConventionalMemory)
            {
                DebugConsole.Write(" [FREE]");
                totalConventional += size;
                conventionalRegions++;
                if (size < smallestConventional) smallestConventional = size;
                if (size > largestConventional) largestConventional = size;
            }
            else if (desc->Type == EFIMemoryType.RuntimeServicesCode ||
                     desc->Type == EFIMemoryType.RuntimeServicesData)
            {
                DebugConsole.Write(" [RUNTIME]");
                totalUsed += size;
            }
            else if (desc->Type == EFIMemoryType.ACPIReclaimMemory ||
                     desc->Type == EFIMemoryType.ACPIMemoryNVS)
            {
                DebugConsole.Write(" [ACPI]");
                totalUsed += size;
            }
            else if (desc->Type == EFIMemoryType.ReservedMemoryType ||
                     desc->Type == EFIMemoryType.UnusableMemory)
            {
                totalUsed += size;
            }
            else if (desc->Type == EFIMemoryType.LoaderCode ||
                     desc->Type == EFIMemoryType.LoaderData ||
                     desc->Type == EFIMemoryType.BootServicesCode ||
                     desc->Type == EFIMemoryType.BootServicesData)
            {
                // After ExitBootServices, these are also free
                DebugConsole.Write(" [reclaimable]");
                totalConventional += size;
            }

            DebugConsole.WriteLine();
        }

        // Print summary
        DebugConsole.WriteLine();
        DebugConsole.WriteLine("  Memory Summary:");
        DebugConsole.Write("    Conventional (free): ");
        DebugConsole.WriteDecimal((uint)(totalConventional / (1024 * 1024)));
        DebugConsole.Write(" MB in ");
        DebugConsole.WriteDecimal(conventionalRegions);
        DebugConsole.WriteLine(" regions");

        if (conventionalRegions > 0 && smallestConventional != ulong.MaxValue)
        {
            DebugConsole.Write("    Smallest free region: ");
            if (smallestConventional >= 1024 * 1024)
            {
                DebugConsole.WriteDecimal((uint)(smallestConventional / (1024 * 1024)));
                DebugConsole.WriteLine(" MB");
            }
            else
            {
                DebugConsole.WriteDecimal((uint)(smallestConventional / 1024));
                DebugConsole.WriteLine(" KB");
            }

            DebugConsole.Write("    Largest free region:  ");
            DebugConsole.WriteDecimal((uint)(largestConventional / (1024 * 1024)));
            DebugConsole.WriteLine(" MB");
        }

        // Show our reserved regions
        DebugConsole.WriteLine();
        DebugConsole.WriteLine("  Our Reserved Regions:");
        DebugConsole.Write("    BootInfo:    0x");
        DebugConsole.WriteHex(0x100000UL);
        DebugConsole.Write(" - 0x");
        DebugConsole.WriteHex(0x200000UL);
        DebugConsole.WriteLine(" (1 MB)");

        DebugConsole.Write("    Files:       0x");
        DebugConsole.WriteHex(bootInfo->LoadedFilesAddress);
        DebugConsole.Write(" (");
        DebugConsole.WriteDecimal(bootInfo->LoadedFilesCount);
        DebugConsole.WriteLine(" files)");

        DebugConsole.Write("    Kernel:      0x");
        DebugConsole.WriteHex(bootInfo->KernelPhysicalBase);
        DebugConsole.Write(" - 0x");
        DebugConsole.WriteHex(bootInfo->KernelPhysicalBase + bootInfo->KernelSize);
        DebugConsole.Write(" (");
        DebugConsole.WriteDecimal((uint)(bootInfo->KernelSize / 1024));
        DebugConsole.WriteLine(" KB)");

        DebugConsole.WriteLine();
    }

    /// <summary>
    /// Get human-readable name for EFI memory type
    /// </summary>
    private static string GetMemoryTypeName(EFIMemoryType type)
    {
        return type switch
        {
            EFIMemoryType.ReservedMemoryType => "Reserved",
            EFIMemoryType.LoaderCode => "LoaderCode",
            EFIMemoryType.LoaderData => "LoaderData",
            EFIMemoryType.BootServicesCode => "BSCode",
            EFIMemoryType.BootServicesData => "BSData",
            EFIMemoryType.RuntimeServicesCode => "RSCode",
            EFIMemoryType.RuntimeServicesData => "RSData",
            EFIMemoryType.ConventionalMemory => "Conventional",
            EFIMemoryType.UnusableMemory => "Unusable",
            EFIMemoryType.ACPIReclaimMemory => "ACPIReclaim",
            EFIMemoryType.ACPIMemoryNVS => "ACPINVS",
            EFIMemoryType.MemoryMappedIO => "MMIO",
            EFIMemoryType.MemoryMappedIOPortSpace => "MMIOPort",
            EFIMemoryType.PalCode => "PalCode",
            EFIMemoryType.PersistentMemory => "Persistent",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Test virtio block device I/O by calling VirtioBlkEntry.TestRead().
    /// </summary>
    private static void TestVirtioIO(uint virtioBlkEntryToken)
    {
        DebugConsole.WriteLine();
        DebugConsole.WriteLine("[VirtioIO] Testing virtio block device...");

        // Find TestRead method
        uint testReadToken = AssemblyLoader.FindMethodDefByName(_virtioBlkDriverId, virtioBlkEntryToken, "TestRead");
        if (testReadToken == 0)
        {
            DebugConsole.WriteLine("[VirtioIO] ERROR: Could not find TestRead method");
            return;
        }

        // JIT compile TestRead method
        DebugConsole.WriteLine("[VirtioIO] JIT compiling VirtioBlkEntry.TestRead...");
        var testReadResult = Runtime.JIT.Tier0JIT.CompileMethod(_virtioBlkDriverId, testReadToken);
        if (!testReadResult.Success)
        {
            DebugConsole.WriteLine("[VirtioIO] ERROR: Failed to JIT compile TestRead");
            return;
        }

        DebugConsole.WriteLine(string.Format("[VirtioIO] TestRead at 0x{0}",
            ((ulong)testReadResult.CodeAddress).ToString("X", null)));

        // Call TestRead
        var testReadFunc = (delegate* unmanaged<int>)testReadResult.CodeAddress;
        int result = testReadFunc();

        if (result == 1)
        {
            DebugConsole.WriteLine("[VirtioIO] TestRead PASSED!");
        }
        else
        {
            DebugConsole.WriteLine("[VirtioIO] TestRead FAILED!");
            return;  // Don't continue if read fails
        }

        // Find TestWrite method
        uint testWriteToken = AssemblyLoader.FindMethodDefByName(_virtioBlkDriverId, virtioBlkEntryToken, "TestWrite");
        if (testWriteToken == 0)
        {
            DebugConsole.WriteLine("[VirtioIO] ERROR: Could not find TestWrite method");
            return;
        }

        // JIT compile TestWrite method
        DebugConsole.WriteLine("[VirtioIO] JIT compiling VirtioBlkEntry.TestWrite...");
        var testWriteResult = Runtime.JIT.Tier0JIT.CompileMethod(_virtioBlkDriverId, testWriteToken);
        if (!testWriteResult.Success)
        {
            DebugConsole.WriteLine("[VirtioIO] ERROR: Failed to JIT compile TestWrite");
            return;
        }

        DebugConsole.WriteLine(string.Format("[VirtioIO] TestWrite at 0x{0}",
            ((ulong)testWriteResult.CodeAddress).ToString("X", null)));

        // Call TestWrite
        var testWriteFunc = (delegate* unmanaged<int>)testWriteResult.CodeAddress;
        int writeResult = testWriteFunc();

        if (writeResult == 1)
        {
            DebugConsole.WriteLine("[VirtioIO] TestWrite PASSED!");
        }
        else
        {
            DebugConsole.WriteLine("[VirtioIO] TestWrite FAILED!");
            return;  // Don't continue if write fails
        }

        // Find TestFatMount method
        uint testFatMountToken = AssemblyLoader.FindMethodDefByName(_virtioBlkDriverId, virtioBlkEntryToken, "TestFatMount");
        if (testFatMountToken == 0)
        {
            DebugConsole.WriteLine("[VirtioIO] ERROR: Could not find TestFatMount method");
            return;
        }

        // JIT compile TestFatMount method
        DebugConsole.WriteLine("[VirtioIO] JIT compiling VirtioBlkEntry.TestFatMount...");
        var testFatMountResult = Runtime.JIT.Tier0JIT.CompileMethod(_virtioBlkDriverId, testFatMountToken);
        if (!testFatMountResult.Success)
        {
            DebugConsole.WriteLine("[VirtioIO] ERROR: Failed to JIT compile TestFatMount");
            return;
        }

        DebugConsole.WriteLine(string.Format("[VirtioIO] TestFatMount at 0x{0}",
            ((ulong)testFatMountResult.CodeAddress).ToString("X", null)));

        // Call TestFatMount
        var testFatMountFunc = (delegate* unmanaged<int>)testFatMountResult.CodeAddress;
        int fatMountResult = testFatMountFunc();

        if (fatMountResult == 1)
        {
            DebugConsole.WriteLine("[VirtioIO] TestFatMount PASSED!");
        }
        else
        {
            DebugConsole.WriteLine("[VirtioIO] TestFatMount FAILED!");
        }
    }
}
