// NeutrinoOS kernel - Phase 4 file-system exports
//
// Implements the System.IO kernel bridge: the [UnmanagedCallersOnly]
// exports that korlib's System.IO.File / System.IO.Directory primitives
// (FileBootRead, FileBootWrite, ..., DirBootEntry) are bound to by the
// korlib token registry (Kernel.BuildFileTokenRegistry).
//
// The actual I/O runs in the JIT-loaded AHCI/FAT driver: each export
// calls the driver's AhciEntry helper of the same shape through a
// function pointer that is JIT-compiled once (lazily, on first use -
// after the driver has been bound) and cached in a static field. This
// mirrors how Platform.AssemblyRunner reads `run` images from the boot
// volume.
//
// Call convention: the exports are called from JIT-compiled frames with
// the platform ABI; arguments are raw pointers (char* UTF-16 paths,
// byte* buffers) into pinned managed memory - no managed references
// cross this boundary (see korlib System.IO.File for the pinning side).

using System.Runtime.InteropServices;
using ProtonOS.Runtime;
using ProtonOS.Runtime.JIT;

namespace ProtonOS.Platform;

/// <summary>Kernel exports backing korlib's System.IO (see file header).</summary>
public static unsafe class FileExports
{
    // ==================== Lazily JIT-compiled driver helpers ====================

    private static void* _fnGetBootFileSize;
    private static void* _fnReadBootFile;
    private static void* _fnWriteBootFile;
    private static void* _fnDeleteBootFile;
    private static void* _fnCreateBootDir;
    private static void* _fnDeleteBootDir;
    private static void* _fnBootPathExists;
    private static void* _fnListBootDirEntry;
    private static bool _ensureInProgress;

    /// <summary>
    /// Find and JIT-compile the AHCI driver file helpers (once per boot;
    /// requires the driver to be bound first). Reentrancy: compiling the
    /// driver helpers can itself trigger assembly resolution, which the
    /// /lib on-demand loader routes back through this class - the guard
    /// makes that inner call fail fast instead of recursing.
    /// </summary>
    private static bool EnsureDriverHelpers()
    {
        if (_fnGetBootFileSize != null && _fnListBootDirEntry != null)
            return true;
        if (_ensureInProgress)
            return false;
        _ensureInProgress = true;
        try
        {
            return EnsureDriverHelpersCore();
        }
        finally
        {
            _ensureInProgress = false;
        }
    }

    private static bool EnsureDriverHelpersCore()
    {
        if (_fnGetBootFileSize != null && _fnListBootDirEntry != null)
            return true;

        uint asmId = Kernel.AhciDriverAssemblyId;
        if (asmId == AssemblyLoader.InvalidAssemblyId)
            return false;

        uint typeToken = AssemblyLoader.FindTypeDefByFullName(
            asmId, "ProtonOS.Drivers.Storage.Ahci", "AhciEntry");
        if (typeToken == 0)
            return false;

        if (_fnGetBootFileSize == null)
        {
            uint t = AssemblyLoader.FindMethodDefByName(asmId, typeToken, "GetBootFileSize");
            if (t == 0)
                return false;
            var r = Tier0JIT.CompileMethod(asmId, t);
            if (!r.Success || r.CodeAddress == null)
                return false;
            _fnGetBootFileSize = r.CodeAddress;
        }

        if (_fnReadBootFile == null)
        {
            uint t = AssemblyLoader.FindMethodDefByName(asmId, typeToken, "ReadBootFile");
            if (t == 0)
                return false;
            var r = Tier0JIT.CompileMethod(asmId, t);
            if (!r.Success || r.CodeAddress == null)
                return false;
            _fnReadBootFile = r.CodeAddress;
        }

        if (_fnWriteBootFile == null)
        {
            uint t = AssemblyLoader.FindMethodDefByName(asmId, typeToken, "WriteBootFile");
            if (t == 0)
                return false;
            var r = Tier0JIT.CompileMethod(asmId, t);
            if (!r.Success || r.CodeAddress == null)
                return false;
            _fnWriteBootFile = r.CodeAddress;
        }

        if (_fnDeleteBootFile == null)
        {
            uint t = AssemblyLoader.FindMethodDefByName(asmId, typeToken, "DeleteBootFile");
            if (t == 0)
                return false;
            var r = Tier0JIT.CompileMethod(asmId, t);
            if (!r.Success || r.CodeAddress == null)
                return false;
            _fnDeleteBootFile = r.CodeAddress;
        }

        if (_fnCreateBootDir == null)
        {
            uint t = AssemblyLoader.FindMethodDefByName(asmId, typeToken, "CreateBootDir");
            if (t == 0)
                return false;
            var r = Tier0JIT.CompileMethod(asmId, t);
            if (!r.Success || r.CodeAddress == null)
                return false;
            _fnCreateBootDir = r.CodeAddress;
        }

        if (_fnDeleteBootDir == null)
        {
            uint t = AssemblyLoader.FindMethodDefByName(asmId, typeToken, "DeleteBootDir");
            if (t == 0)
                return false;
            var r = Tier0JIT.CompileMethod(asmId, t);
            if (!r.Success || r.CodeAddress == null)
                return false;
            _fnDeleteBootDir = r.CodeAddress;
        }

        if (_fnBootPathExists == null)
        {
            uint t = AssemblyLoader.FindMethodDefByName(asmId, typeToken, "BootPathExists");
            if (t == 0)
                return false;
            var r = Tier0JIT.CompileMethod(asmId, t);
            if (!r.Success || r.CodeAddress == null)
                return false;
            _fnBootPathExists = r.CodeAddress;
        }

        if (_fnListBootDirEntry == null)
        {
            uint t = AssemblyLoader.FindMethodDefByName(asmId, typeToken, "ListBootDirEntry");
            if (t == 0)
                return false;
            var r = Tier0JIT.CompileMethod(asmId, t);
            if (!r.Success || r.CodeAddress == null)
                return false;
            _fnListBootDirEntry = r.CodeAddress;
        }

        return true;
    }

    // ==================== Kernel-callable wrappers ====================
    // [UnmanagedCallersOnly] methods cannot be called directly from C#;
    // kernel code (e.g. AssemblyLoader's /lib on-demand loader) calls
    // these plain wrappers, which share the implementation with the
    // exports below.

    /// <summary>Kernel-callable file size probe (see FileBootSize export).</summary>
    public static int KernelBootSize(char* path, int pathLen)
    {
        if (!EnsureDriverHelpers())
            return -1;
        var getSize = (delegate*<char*, int, int>)_fnGetBootFileSize;
        return getSize(path, pathLen);
    }

    /// <summary>Kernel-callable file read (see FileBootRead export).</summary>
    public static int KernelBootRead(char* path, int pathLen, byte* buffer, int capacity)
    {
        if (!EnsureDriverHelpers())
            return -1;

        // Reuse the driver's size probe to bound the read, then read.
        var getSize = (delegate*<char*, int, int>)_fnGetBootFileSize;
        int size = getSize(path, pathLen);
        if (size < 0)
            return -1;
        if (size > capacity)
            return -2;

        var readFile = (delegate*<char*, int, byte*, int, int>)_fnReadBootFile;
        return readFile(path, pathLen, buffer, capacity);
    }

    // ==================== System.IO.File exports ====================

    /// <summary>
    /// Reads a file from the boot volume into the caller's buffer.
    /// Returns the number of bytes read, or a negative error code.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "FileBootRead")]
    public static int FileBootRead(char* path, int pathLen, byte* buffer, int capacity)
        => KernelBootRead(path, pathLen, buffer, capacity);

    /// <summary>
    /// Writes (or appends) a buffer to a file on the boot volume,
    /// creating it when missing. Returns the number of bytes written,
    /// or a negative error code.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "FileBootWrite")]
    public static int FileBootWrite(char* path, int pathLen, byte* data, int length, int append)
    {
        if (!EnsureDriverHelpers())
            return -1;
        var writeFile = (delegate*<char*, int, byte*, int, int, int>)_fnWriteBootFile;
        return writeFile(path, pathLen, data, length, append);
    }

    /// <summary>Returns the size of a boot-volume file, or a negative error code.</summary>
    [UnmanagedCallersOnly(EntryPoint = "FileBootSize")]
    public static int FileBootSize(char* path, int pathLen)
        => KernelBootSize(path, pathLen);

    /// <summary>Returns 1 when the boot-volume file exists, else 0 (negative: no driver).</summary>
    [UnmanagedCallersOnly(EntryPoint = "FileBootExists")]
    public static int FileBootExists(char* path, int pathLen)
    {
        if (!EnsureDriverHelpers())
            return -1;
        var pathExists = (delegate*<char*, int, int, int>)_fnBootPathExists;
        return pathExists(path, pathLen, 0) == 1 ? 1 : 0;
    }

    /// <summary>Deletes a boot-volume file: 0 on success, -1 when missing, -2 on error.</summary>
    [UnmanagedCallersOnly(EntryPoint = "FileBootDelete")]
    public static int FileBootDelete(char* path, int pathLen)
    {
        if (!EnsureDriverHelpers())
            return -2;
        var deleteFile = (delegate*<char*, int, int>)_fnDeleteBootFile;
        return deleteFile(path, pathLen);
    }

    // ==================== System.IO.Directory exports ====================

    /// <summary>Returns 1 when the boot-volume directory exists, else 0 (negative: no driver).</summary>
    [UnmanagedCallersOnly(EntryPoint = "DirBootExists")]
    public static int DirBootExists(char* path, int pathLen)
    {
        if (!EnsureDriverHelpers())
            return -1;
        var pathExists = (delegate*<char*, int, int, int>)_fnBootPathExists;
        return pathExists(path, pathLen, 1) == 1 ? 1 : 0;
    }

    /// <summary>Creates a boot-volume directory: 0 on success (or existing), negative on error.</summary>
    [UnmanagedCallersOnly(EntryPoint = "DirBootCreate")]
    public static int DirBootCreate(char* path, int pathLen)
    {
        if (!EnsureDriverHelpers())
            return -2;
        var createDir = (delegate*<char*, int, int>)_fnCreateBootDir;
        return createDir(path, pathLen);
    }

    /// <summary>Deletes an empty boot-volume directory: 0 on success, -1 when missing, -2 on error.</summary>
    [UnmanagedCallersOnly(EntryPoint = "DirBootDelete")]
    public static int DirBootDelete(char* path, int pathLen)
    {
        if (!EnsureDriverHelpers())
            return -2;
        var deleteDir = (delegate*<char*, int, int>)_fnDeleteBootDir;
        return deleteDir(path, pathLen);
    }

    /// <summary>
    /// Enumerates a boot-volume directory entry by index (skipping "."
    /// and ".."): returns the entry-name length in chars (copied into
    /// nameBuf, clipped to nameCapacity) and writes 1/0 to isDir, or a
    /// negative code (-1 end of listing, -2 error, -3 no driver).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "DirBootEntry")]
    public static int DirBootEntry(char* path, int pathLen, int index, char* nameBuf, int nameCapacity, int* isDir)
    {
        if (!EnsureDriverHelpers())
            return -3;
        var listEntry = (delegate*<char*, int, int, char*, int, int*, int>)_fnListBootDirEntry;
        return listEntry(path, pathLen, index, nameBuf, nameCapacity, isDir);
    }
}
