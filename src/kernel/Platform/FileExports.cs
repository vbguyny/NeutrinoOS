// NeutrinoOS kernel - Phase 4 file-system exports
//
// Implements the System.IO kernel bridge: the [UnmanagedCallersOnly]
// exports that korlib's System.IO.File / System.IO.Directory primitives
// (FileBootRead, FileBootWrite, ..., DirBootEntry, DirBootGetCwd/
// DirBootSetCwd) are bound to by the korlib token registry
// (Kernel.BuildFileTokenRegistry).
//
// The actual I/O runs in the JIT-loaded boot-volume driver (AHCI/FAT or
// virtio-blk/FAT, selected by Platform.BootStorage): each export calls
// the selected entry's helper of the same shape through a function
// pointer that is JIT-compiled once (lazily, on first use - after the
// driver has been bound) and cached in a static field. This mirrors how
// Platform.AssemblyRunner reads `run` images from the boot volume.
//
// Call convention: the exports are called from JIT-compiled frames with
// the platform ABI; arguments are raw pointers (char* UTF-16 paths,
// byte* buffers) into pinned managed memory - no managed references
// cross this boundary (see korlib System.IO.File for the pinning side).

using System.Runtime.InteropServices;
using NeutrinoOS.Runtime;
using NeutrinoOS.Runtime.JIT;

namespace NeutrinoOS.Platform;

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
    private static void* _fnGetBootVolumeStats;
    private static bool _ensureInProgress;

    // ==================== Phase 10: DDK VFS path bridge ====================
    // Paths under non-root VFS mounts (exFAT disks auto-mounted under
    // /mnt/usb, /proc) are served by the DDK VfsPathBridge before any
    // boot-volume fallback. The statics are JIT-compiled once, lazily
    // (the DDK is loaded later than the first System.IO users, so the
    // lookup simply retries until it succeeds).

    private static void* _fnVfsExists;
    private static void* _fnVfsSize;
    private static void* _fnVfsRead;
    private static void* _fnVfsWrite;
    private static void* _fnVfsDirEntry;
    private static void* _fnVfsDirCreate;
    private static void* _fnVfsDirDelete;
    private static void* _fnVfsFileDelete;

    /// <summary>VfsPathBridge.NotHandled ("no non-root mount covers the path").</summary>
    private const int VfsNotHandled = -100;

    /// <summary>
    /// JIT-compiles the DDK VFS path bridge (once). Returns false while
    /// the DDK is unavailable or any bridge static fails to compile.
    /// </summary>
    private static bool EnsureVfsBridge()
    {
        if (_fnVfsRead != null)
            return true;

        uint ddkId = Kernel.DdkAssemblyId;
        if (ddkId == AssemblyLoader.InvalidAssemblyId)
            return false;

        uint typeToken = AssemblyLoader.FindTypeDefByFullName(
            ddkId, "NeutrinoOS.DDK.Storage", "VfsPathBridge");
        if (typeToken == 0)
            return false;

        if (_fnVfsExists == null)
        {
            uint t = AssemblyLoader.FindMethodDefByName(ddkId, typeToken, "Exists");
            if (t == 0)
                return false;
            var r = Tier0JIT.CompileMethod(ddkId, t);
            if (!r.Success || r.CodeAddress == null)
                return false;
            _fnVfsExists = r.CodeAddress;
        }
        if (_fnVfsSize == null)
        {
            uint t = AssemblyLoader.FindMethodDefByName(ddkId, typeToken, "GetSize");
            if (t == 0)
                return false;
            var r = Tier0JIT.CompileMethod(ddkId, t);
            if (!r.Success || r.CodeAddress == null)
                return false;
            _fnVfsSize = r.CodeAddress;
        }
        if (_fnVfsRead == null)
        {
            uint t = AssemblyLoader.FindMethodDefByName(ddkId, typeToken, "Read");
            if (t == 0)
                return false;
            var r = Tier0JIT.CompileMethod(ddkId, t);
            if (!r.Success || r.CodeAddress == null)
                return false;
            _fnVfsRead = r.CodeAddress;
        }
        if (_fnVfsWrite == null)
        {
            uint t = AssemblyLoader.FindMethodDefByName(ddkId, typeToken, "Write");
            if (t == 0)
                return false;
            var r = Tier0JIT.CompileMethod(ddkId, t);
            if (!r.Success || r.CodeAddress == null)
                return false;
            _fnVfsWrite = r.CodeAddress;
        }
        if (_fnVfsDirEntry == null)
        {
            uint t = AssemblyLoader.FindMethodDefByName(ddkId, typeToken, "DirEntry");
            if (t == 0)
                return false;
            var r = Tier0JIT.CompileMethod(ddkId, t);
            if (!r.Success || r.CodeAddress == null)
                return false;
            _fnVfsDirEntry = r.CodeAddress;
        }
        if (_fnVfsDirCreate == null)
        {
            uint t = AssemblyLoader.FindMethodDefByName(ddkId, typeToken, "DirCreate");
            if (t == 0)
                return false;
            var r = Tier0JIT.CompileMethod(ddkId, t);
            if (!r.Success || r.CodeAddress == null)
                return false;
            _fnVfsDirCreate = r.CodeAddress;
        }
        if (_fnVfsDirDelete == null)
        {
            uint t = AssemblyLoader.FindMethodDefByName(ddkId, typeToken, "DirDelete");
            if (t == 0)
                return false;
            var r = Tier0JIT.CompileMethod(ddkId, t);
            if (!r.Success || r.CodeAddress == null)
                return false;
            _fnVfsDirDelete = r.CodeAddress;
        }
        if (_fnVfsFileDelete == null)
        {
            uint t = AssemblyLoader.FindMethodDefByName(ddkId, typeToken, "FileDelete");
            if (t == 0)
                return false;
            var r = Tier0JIT.CompileMethod(ddkId, t);
            if (!r.Success || r.CodeAddress == null)
                return false;
            _fnVfsFileDelete = r.CodeAddress;
        }

        return true;
    }

    /// <summary>
    /// Find and JIT-compile the boot-volume driver file helpers (once per
    /// boot; requires a driver with a FAT volume to be bound first - see
    /// Platform.BootStorage). Reentrancy: compiling the
    /// driver helpers can itself trigger assembly resolution, which the
    /// /lib on-demand loader routes back through this class - the guard
    /// makes that inner call fail fast instead of recursing.
    /// </summary>
    private static bool EnsureDriverHelpers()
    {
        if (_fnGetBootFileSize != null && _fnListBootDirEntry != null && _fnGetBootVolumeStats != null)
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
        if (_fnGetBootFileSize != null && _fnListBootDirEntry != null && _fnGetBootVolumeStats != null)
            return true;

        // Boot-volume driver selection: AHCI (boot disk on SATA) or
        // virtio-blk (QEMU virtio boots) - see Platform.BootStorage.
        if (!BootStorage.TryResolve(out uint asmId, out uint typeToken))
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

        if (_fnGetBootVolumeStats == null)
        {
            uint t = AssemblyLoader.FindMethodDefByName(asmId, typeToken, "GetBootVolumeStats");
            if (t == 0)
                return false;
            var r = Tier0JIT.CompileMethod(asmId, t);
            if (!r.Success || r.CodeAddress == null)
                return false;
            _fnGetBootVolumeStats = r.CodeAddress;
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
        // Phase 7: virtual /dev files are served before the FAT driver.
        if (VirtualDevices.IsVirtual(path, pathLen))
            return VirtualDevices.Size(path, pathLen);

        // Phase 10: paths under non-root VFS mounts (exFAT disks, /proc)
        // are served by the mounted filesystem.
        if (EnsureVfsBridge())
        {
            var vfsSize = (delegate*<char*, int, int>)_fnVfsSize;
            int vfs = vfsSize(path, pathLen);
            if (vfs != VfsNotHandled)
                return vfs;
        }

        if (!EnsureDriverHelpers())
            return -1;
        var getSize = (delegate*<char*, int, int>)_fnGetBootFileSize;
        return getSize(path, pathLen);
    }

    /// <summary>
    /// Phase 5: boot-volume stats for the df utility (see
    /// AhciEntry.GetBootVolumeStats). Returns the volume label length, or
    /// a negative error code when the driver is not ready.
    /// </summary>
    public static int KernelBootVolumeStats(char* labelBuf, int labelCapacity,
                                            ulong* totalBytes, ulong* freeBytes)
    {
        if (!EnsureDriverHelpers())
            return -3;
        var stats = (delegate*<char*, int, ulong*, ulong*, int>)_fnGetBootVolumeStats;
        return stats(labelBuf, labelCapacity, totalBytes, freeBytes);
    }

    /// <summary>Kernel-callable file read (see FileBootRead export).</summary>
    public static int KernelBootRead(char* path, int pathLen, byte* buffer, int capacity)
    {
        // Phase 7: virtual /dev files are served before the FAT driver.
        if (VirtualDevices.IsVirtual(path, pathLen))
            return VirtualDevices.Read(path, pathLen, buffer, capacity);

        // Phase 10: paths under non-root VFS mounts (exFAT disks, /proc).
        if (EnsureVfsBridge())
        {
            var vfsRead = (delegate*<char*, int, byte*, int, int>)_fnVfsRead;
            int vfs = vfsRead(path, pathLen, buffer, capacity);
            if (vfs != VfsNotHandled)
                return vfs;
        }

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
        // Phase 10: writes under non-root VFS mounts (e.g. an auto-mounted
        // exFAT stick) go to the mounted filesystem.
        if (EnsureVfsBridge())
        {
            var vfsWrite = (delegate*<char*, int, byte*, int, int, int>)_fnVfsWrite;
            int vfs = vfsWrite(path, pathLen, data, length, append);
            if (vfs != VfsNotHandled)
                return vfs;
        }

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
        // Phase 7: virtual /dev files are served before the FAT driver.
        if (VirtualDevices.IsVirtual(path, pathLen))
            return 1;

        // Phase 10: paths under non-root VFS mounts (exFAT disks, /proc).
        if (EnsureVfsBridge())
        {
            var vfsExists = (delegate*<char*, int, int, int>)_fnVfsExists;
            int vfs = vfsExists(path, pathLen, 0);
            if (vfs != VfsNotHandled)
                return vfs;
        }

        if (!EnsureDriverHelpers())
            return -1;
        var pathExists = (delegate*<char*, int, int, int>)_fnBootPathExists;
        return pathExists(path, pathLen, 0) == 1 ? 1 : 0;
    }

    /// <summary>Deletes a boot-volume file: 0 on success, -1 when missing, -2 on error.</summary>
    [UnmanagedCallersOnly(EntryPoint = "FileBootDelete")]
    public static int FileBootDelete(char* path, int pathLen)
    {
        if (EnsureVfsBridge())
        {
            var vfsDelete = (delegate*<char*, int, int>)_fnVfsFileDelete;
            int vfs = vfsDelete(path, pathLen);
            if (vfs != VfsNotHandled)
                return vfs;
        }
        if (!EnsureDriverHelpers())
            return -2;
        var deleteFile = (delegate*<char*, int, int>)_fnDeleteBootFile;
        return deleteFile(path, pathLen);
    }

    // ==================== System.IO.Directory exports ====================

    // Current directory shared by the kernel shell and JIT-compiled
    // utilities (korlib's Directory.Get/SetCurrentDirectory bridge to
    // these exports, so `cd` is visible to ls/cat/... and vice versa).
    // Lazily defaults to "/" - no field initializer (a string initializer
    // would add a static constructor).
    private static string? _cwd;

    /// <summary>
    /// Copies the shared current directory into the caller's buffer and
    /// returns the number of chars copied ("/" before any cd).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "DirBootGetCwd")]
    public static int DirBootGetCwd(char* buffer, int capacity)
    {
        if (buffer == null || capacity <= 0)
            return -1;
        string cwd = _cwd ?? "/";
        int len = cwd.Length;
        if (len > capacity)
            len = capacity;
        for (int i = 0; i < len; i++)
            buffer[i] = cwd[i];
        return len;
    }

    /// <summary>
    /// Stores the shared current directory. Called by korlib's
    /// Directory.SetCurrentDirectory after it validated the target.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "DirBootSetCwd")]
    public static int DirBootSetCwd(char* path, int pathLen)
    {
        if (path == null || pathLen <= 0)
            return -1;
        _cwd = new string(path, 0, pathLen);
        return 0;
    }

    /// <summary>Returns 1 when the boot-volume directory exists, else 0 (negative: no driver).</summary>
    [UnmanagedCallersOnly(EntryPoint = "DirBootExists")]
    public static int DirBootExists(char* path, int pathLen)
    {
        if (EnsureVfsBridge())
        {
            var vfsExists = (delegate*<char*, int, int, int>)_fnVfsExists;
            int vfs = vfsExists(path, pathLen, 1);
            if (vfs != VfsNotHandled)
                return vfs;
        }
        if (!EnsureDriverHelpers())
            return -1;
        var pathExists = (delegate*<char*, int, int, int>)_fnBootPathExists;
        return pathExists(path, pathLen, 1) == 1 ? 1 : 0;
    }

    /// <summary>Creates a boot-volume directory: 0 on success (or existing), negative on error.</summary>
    [UnmanagedCallersOnly(EntryPoint = "DirBootCreate")]
    public static int DirBootCreate(char* path, int pathLen)
    {
        if (EnsureVfsBridge())
        {
            var vfsCreate = (delegate*<char*, int, int>)_fnVfsDirCreate;
            int vfs = vfsCreate(path, pathLen);
            if (vfs != VfsNotHandled)
                return vfs;
        }
        if (!EnsureDriverHelpers())
            return -2;
        var createDir = (delegate*<char*, int, int>)_fnCreateBootDir;
        return createDir(path, pathLen);
    }

    /// <summary>Deletes an empty boot-volume directory: 0 on success, -1 when missing, -2 on error.</summary>
    [UnmanagedCallersOnly(EntryPoint = "DirBootDelete")]
    public static int DirBootDelete(char* path, int pathLen)
    {
        if (EnsureVfsBridge())
        {
            var vfsDelete = (delegate*<char*, int, int>)_fnVfsDirDelete;
            int vfs = vfsDelete(path, pathLen);
            if (vfs != VfsNotHandled)
                return vfs;
        }
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
        if (EnsureVfsBridge())
        {
            var vfsEntry = (delegate*<char*, int, int, char*, int, int*, int>)_fnVfsDirEntry;
            int vfs = vfsEntry(path, pathLen, index, nameBuf, nameCapacity, isDir);
            if (vfs != VfsNotHandled)
                return vfs;
        }
        if (!EnsureDriverHelpers())
            return -3;
        var listEntry = (delegate*<char*, int, int, char*, int, int*, int>)_fnListBootDirEntry;
        return listEntry(path, pathLen, index, nameBuf, nameCapacity, isDir);
    }
}
