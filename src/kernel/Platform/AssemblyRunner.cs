// NeutrinoOS kernel - Phase 4 assembly runner
//
// Executes a .NET assembly (compiled with the .NET 10 SDK) by path from
// the interactive shell or from tests:
//
//   1. Locate the file. Paths with a directory component ("/apps/x.dll")
//      are read from the runtime VFS (the mounted FAT32 boot volume), so
//      any .dll copied into the image can be launched. Bare file names
//      are looked up in the boot-image preloaded-files table first
//      (files the bootloader loaded before ExitBootServices).
//   2. Load the PE image with the AssemblyLoader and JIT-compile the
//      entry point (Main) with the Tier-0 JIT.
//   3. Invoke Main in kernel mode on the calling thread. Supported
//      signatures: static void Main(), static int Main(), and
//      static int Main(string[] args) / static void Main(string[] args).
//      Arguments are materialized by a JIT-side helper
//      (TestSupport.ShellRunSupport.InvokeMain): managed objects must be
//      created inside JIT-compiled frames, otherwise the GC's root scan
//      (which only sees kernel stack pointers, not references) cannot
//      keep them alive across allocations.
//   4. Return Main's exit code, or a negative Errno on failure.
//
// The loaded image buffer is intentionally not freed: the AssemblyLoader
// keeps referencing the PE bytes for the lifetime of the process (the
// same contract NetExecutable relies on). Each `run` leaks the file
// buffer plus one assembly slot; acceptable for the Phase 4 shell.
//
// Phase 4 scope note: the runner executes assemblies in kernel mode (the
// same way the boot-time test runners execute JITTest/AppTest). The Ring-3
// execve path (NetExecutable.Exec) is unchanged and remains available for
// process-isolated execution.

using System;
using ProtonOS.IO;
using ProtonOS.Memory;
using ProtonOS.Process;
using ProtonOS.Runtime;
using ProtonOS.Runtime.JIT;

namespace ProtonOS.Platform;

/// <summary>
/// Loads and runs a .NET assembly by path (see file header).
/// </summary>
public static unsafe class AssemblyRunner
{
    /// <summary>
    /// Run the assembly at <paramref name="path"/>, passing
    /// <paramref name="args"/> to Main(string[]) when the entry point
    /// takes an argument array.
    /// </summary>
    /// <returns>Main's exit code, or a negative Errno on failure.</returns>
    public static int Run(string path, string[] args)
    {
        // Everything below - the file load, the JIT compiles it triggers
        // (boot-volume helpers, then Main and its call graph) and Main itself -
        // runs on a private large stack. The shell executes on the firmware
        // boot stack (tens of KB), which these nested compile chains overflow
        // (observed as an RhpStackProbe page fault). See BigStackRunner.
        RunRequest req;
        req.Path = path;
        req.Args = args;
        req.ExitCode = -1;
        BigStackRunner.Run(&RunOuterThunk, &req);
        return req.ExitCode;
    }

    /// <summary>State block for <see cref="RunOuterThunk"/> (caller's stack).</summary>
    private struct RunRequest
    {
        public string Path;
        public string[] Args;
        public int ExitCode;
    }

    /// <summary>Body executed on the private stack (see <see cref="Run"/>).</summary>
    private static int RunOuterThunk(void* arg)
    {
        var req = (RunRequest*)arg;
        // Copy the managed references to locals in this frame (on the private
        // stack) so they remain in the GC root-scan range for the whole call.
        string path = req->Path;
        string[] args = req->Args;
        int rc = RunFull(path, args);
        req->ExitCode = rc;
        return rc;
    }

    private static int RunFull(string path, string[] args)
    {
        DebugConsole.Write("[run] ");
        DebugConsole.Write(path);
        DebugConsole.WriteLine();

        // Phase 5: reuse an already-loaded assembly for the same path.
        // Every AssemblyLoader.Load consumes a fixed table slot and keeps
        // its file buffer for the session's lifetime; external commands
        // resolve through the same paths repeatedly (`ls`, `ls /apps`, a
        // pipe running `wc` twice, ...), so without this cache a long
        // shell session exhausts the 64-slot assembly table. With the
        // cache the slot count is bounded by the number of DISTINCT
        // utilities executed (the Tier-0 JIT's compiled-method cache
        // makes the re-run fast as well).
        uint asmId = FindCachedAssembly(path);
        LoadedAssembly* asm = null;
        if (asmId != AssemblyLoader.InvalidAssemblyId)
            asm = AssemblyLoader.GetAssembly(asmId);

        if (asm == null)
        {
            byte* data = LoadFile(path, out ulong size);
            if (data == null)
            {
                DebugConsole.Write("[run] error: file not found: ");
                DebugConsole.Write(path);
                DebugConsole.WriteLine();
                return -Errno.ENOENT;
            }

            // Sanity check: PE image
            if (size < 2 || data[0] != 'M' || data[1] != 'Z')
            {
                DebugConsole.WriteLine("[run] error: not a PE image");
                return -Errno.ENOEXEC;
            }

            asmId = AssemblyLoader.Load(data, size);
            if (asmId == AssemblyLoader.InvalidAssemblyId)
            {
                DebugConsole.WriteLine("[run] error: assembly load failed");
                return -Errno.ENOEXEC;
            }

            asm = AssemblyLoader.GetAssembly(asmId);
            if (asm == null)
            {
                DebugConsole.WriteLine("[run] error: assembly lookup failed");
                return -Errno.ENOEXEC;
            }

            CacheAssembly(path, asmId);
        }

        uint entryToken = NetExecutable.GetEntryPointToken(asm);
        if (entryToken == 0)
        {
            DebugConsole.WriteLine("[run] error: no entry point (Main) found");
            return -Errno.ENOEXEC;
        }

        var sig = NetExecutable.GetMainSignatureInfo(asm, entryToken);

        int exitCode = 0;
        int argCount = 0;

        char* packed = null;
        int* lengths = null;

        if (sig.TakesStringArrayArg)
        {
            string[] effectiveArgs = args ?? new string[0];
            if (!EnsureShellRunSupport())
            {
                DebugConsole.WriteLine("[run] error: shell run support unavailable");
                return -Errno.ENOEXEC;
            }

            // Pack the arguments as a UTF-16 buffer plus a lengths array.
            // The JIT-side helper materializes the managed strings and
            // the string[] (see file header).
            int packedChars = 0;
            for (int i = 0; i < effectiveArgs.Length; i++)
                packedChars += effectiveArgs[i].Length;

            packed = (char*)HeapAllocator.Alloc((ulong)((packedChars + 1) * 2));
            lengths = (int*)HeapAllocator.Alloc((ulong)((effectiveArgs.Length + 1) * 4));
            if (packed == null || lengths == null)
            {
                DebugConsole.WriteLine("[run] error: out of memory for args");
                return -Errno.ENOMEM;
            }

            int w = 0;
            for (int i = 0; i < effectiveArgs.Length; i++)
            {
                string a = effectiveArgs[i];
                lengths[i] = a.Length;
                for (int j = 0; j < a.Length; j++)
                    packed[w++] = a[j];
            }
            packed[w] = '\0';

            argCount = effectiveArgs.Length;
        }

        // Compile Main and invoke it (running on the private execution stack
        // established by Run - see the wrapper above).
        var jitResult = Tier0JIT.CompileMethod(asmId, entryToken);
        if (!jitResult.Success || jitResult.CodeAddress == null)
        {
            DebugConsole.WriteLine("[run] error: JIT compilation of Main failed");
            return -Errno.ENOEXEC;
        }

        if (sig.TakesStringArrayArg)
        {
            var invokeMain = (delegate*<void*, char*, int*, int, int, int>)_fnInvokeMain;
            exitCode = invokeMain(jitResult.CodeAddress, packed, lengths, argCount, sig.ReturnsInt ? 1 : 0);
        }
        else if (sig.ReturnsInt)
        {
            var main = (delegate*<int>)jitResult.CodeAddress;
            exitCode = main();
        }
        else
        {
            var main = (delegate*<void>)jitResult.CodeAddress;
            main();
        }

        if (sig.ReturnsInt)
        {
            DebugConsole.Write("[run] exited with code ");
            DebugConsole.WriteDecimal(exitCode);
            DebugConsole.WriteLine();
        }
        else
        {
            DebugConsole.WriteLine("[run] process exited");
        }

        return exitCode;
    }

    // JIT-compiled boot-volume helpers (AhciEntry.GetBootFileSize /
    // AhciEntry.ReadBootFile), compiled lazily on first use and cached
    // for the lifetime of the boot.
    private static void* _fnGetBootFileSize;
    private static void* _fnReadBootFile;

    // ==================== Assembly path cache (Phase 5) ====================

    private const int AssemblyCacheSize = 64;
    private static readonly string?[] _cachePaths = new string?[AssemblyCacheSize];
    private static readonly uint[] _cacheIds = new uint[AssemblyCacheSize];
    private static int _cacheCount;

    /// <summary>Returns the cached assembly id for <paramref name="path"/> or InvalidAssemblyId.</summary>
    private static uint FindCachedAssembly(string path)
    {
        for (int i = 0; i < _cacheCount; i++)
        {
            string? cached = _cachePaths[i];
            if (cached != null && cached == path)
                return _cacheIds[i];
        }
        return AssemblyLoader.InvalidAssemblyId;
    }

    /// <summary>Remembers a successfully loaded assembly for future runs.</summary>
    private static void CacheAssembly(string path, uint asmId)
    {
        if (_cacheCount >= AssemblyCacheSize)
            return;
        _cachePaths[_cacheCount] = path;
        _cacheIds[_cacheCount] = asmId;
        _cacheCount++;
    }

    // JIT-compiled entry-point invocation helper
    // (TestSupport.ShellRunSupport.InvokeMain).
    private static void* _fnInvokeMain;

    /// <summary>
    /// Load the raw PE bytes for <paramref name="path"/>.
    ///
    /// Resolution order:
    ///   1. Bare file names are looked up in the boot-image preloaded
    ///      files table (fast path for bootloader-loaded assemblies).
    ///   2. The boot (FAT) volume is read directly through the AHCI
    ///      driver's ReadBootFile helper - this is how /apps/*.dll and
    ///      /lib/*.dll files copied into the image are picked up at
    ///      runtime.
    ///   3. The kernel VFS (used when a root filesystem is mounted).
    /// </summary>
    private static byte* LoadFile(string path, out ulong size)
    {
        size = 0;

        bool hasDirectory = false;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '/')
            {
                hasDirectory = true;
                break;
            }
        }

        // Fast path: a bare name that the bootloader preloaded
        if (!hasDirectory)
        {
            byte* preloaded = BootInfoAccess.FindFile(path, out size);
            if (preloaded != null)
                return preloaded;
        }

        byte* fromVolume = LoadFromBootVolume(path, out size);
        if (fromVolume != null)
            return fromVolume;

        return LoadFromVfs(path, out size);
    }

    /// <summary>
    /// Read a file from the boot (FAT) volume through the AHCI driver.
    /// The bytes are copied into a kernel heap buffer that is
    /// intentionally never freed (see the file header).
    /// </summary>
    private static byte* LoadFromBootVolume(string path, out ulong size)
    {
        size = 0;

        if (!EnsureBootVolumeHelpers())
            return null;

        // NUL-terminated UTF-16 path buffer for the driver helpers.
        // Managed strings must not cross this boundary (see file header):
        // the driver creates the managed string inside JIT-compiled code,
        // using the explicit 3-arg ctor that the JIT bridges.
        char* pathBuf = stackalloc char[path.Length + 1];
        for (int i = 0; i < path.Length; i++)
            pathBuf[i] = path[i];
        pathBuf[path.Length] = '\0';

        var getSize = (delegate*<char*, int, int>)_fnGetBootFileSize;
        int fileSize = getSize(pathBuf, path.Length);
        if (fileSize <= 0)
            return null;

        byte* buffer = (byte*)HeapAllocator.Alloc((ulong)fileSize);
        if (buffer == null)
            return null;

        var readFile = (delegate*<char*, int, byte*, int, int>)_fnReadBootFile;
        int bytesRead = readFile(pathBuf, path.Length, buffer, fileSize);
        if (bytesRead != fileSize)
        {
            HeapAllocator.Free(buffer);
            return null;
        }

        size = (ulong)fileSize;
        return buffer;
    }

    /// <summary>
    /// Find and JIT-compile the AHCI driver's boot-volume read helpers
    /// (once per boot).
    /// </summary>
    private static bool EnsureBootVolumeHelpers()
    {
        if (_fnGetBootFileSize != null && _fnReadBootFile != null)
            return true;

        uint asmId = Kernel.AhciDriverAssemblyId;
        if (asmId == AssemblyLoader.InvalidAssemblyId)
            return false;

        uint typeToken = AssemblyLoader.FindTypeDefByFullName(
            asmId, "ProtonOS.Drivers.Storage.Ahci", "AhciEntry");
        if (typeToken == 0)
            return false;

        uint sizeToken = AssemblyLoader.FindMethodDefByName(asmId, typeToken, "GetBootFileSize");
        uint readToken = AssemblyLoader.FindMethodDefByName(asmId, typeToken, "ReadBootFile");
        if (sizeToken == 0 || readToken == 0)
            return false;

        var jitSize = Tier0JIT.CompileMethod(asmId, sizeToken);
        if (!jitSize.Success || jitSize.CodeAddress == null)
            return false;

        var jitRead = Tier0JIT.CompileMethod(asmId, readToken);
        if (!jitRead.Success || jitRead.CodeAddress == null)
            return false;

        _fnGetBootFileSize = jitSize.CodeAddress;
        _fnReadBootFile = jitRead.CodeAddress;
        return true;
    }

    /// <summary>
    /// Fallback: read through the kernel VFS (used when a root
    /// filesystem is mounted by a future configuration).
    /// </summary>
    private static byte* LoadFromVfs(string path, out ulong size)
    {
        size = 0;

        byte* pathBytes = stackalloc byte[path.Length + 1];
        for (int i = 0; i < path.Length; i++)
            pathBytes[i] = (byte)path[i];
        pathBytes[path.Length] = 0;

        int handle = VFS.Open(pathBytes, 0, 0);
        if (handle < 0)
            return null;

        long fileSize = VFS.Seek(handle, 0, 2 /* SEEK_END */);
        if (fileSize <= 0)
        {
            VFS.Close(handle);
            return null;
        }

        VFS.Seek(handle, 0, 0 /* SEEK_SET */);

        byte* buffer = (byte*)HeapAllocator.Alloc((ulong)fileSize);
        if (buffer == null)
        {
            VFS.Close(handle);
            return null;
        }

        long bytesRead = 0;
        while (bytesRead < fileSize)
        {
            int toRead = (int)(fileSize - bytesRead);
            if (toRead > 65536) toRead = 65536;
            int read = VFS.Read(handle, buffer + bytesRead, toRead);
            if (read <= 0)
                break;
            bytesRead += read;
        }

        VFS.Close(handle);

        if (bytesRead != fileSize)
        {
            HeapAllocator.Free(buffer);
            return null;
        }

        size = (ulong)fileSize;
        return buffer;
    }

    /// <summary>
    /// Find and JIT-compile TestSupport.ShellRunSupport.InvokeMain, the
    /// JIT-side helper that materializes Main(string[]) arguments and
    /// invokes the entry point (once per boot).
    /// </summary>
    private static bool EnsureShellRunSupport()
    {
        if (_fnInvokeMain != null)
            return true;

        uint asmId = Kernel.TestSupportAssemblyId;
        if (asmId == AssemblyLoader.InvalidAssemblyId)
            return false;

        uint typeToken = AssemblyLoader.FindTypeDefByFullName(asmId, "TestSupport", "ShellRunSupport");
        if (typeToken == 0)
            return false;

        uint token = AssemblyLoader.FindMethodDefByName(asmId, typeToken, "InvokeMain");
        if (token == 0)
            return false;

        var jit = Tier0JIT.CompileMethod(asmId, token);
        if (!jit.Success || jit.CodeAddress == null)
            return false;

        _fnInvokeMain = jit.CodeAddress;
        return true;
    }
}
