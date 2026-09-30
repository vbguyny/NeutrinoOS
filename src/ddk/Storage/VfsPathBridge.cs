// NeutrinoOS Phase 10 - System.IO path bridge over the DDK VFS (Task 4).
//
// The korlib System.IO surface reaches the kernel through the
// FileBoot*/DirBoot* exports, which historically served every path from
// the boot FAT volume. Phase 10 mounts real filesystems (exFAT disks)
// into the DDK VFS at runtime, so those exports now consult this bridge
// first: any path under a non-root VFS mount (e.g. /mnt/usb/sda or
// /proc) is routed to the mounted filesystem; paths the VFS does not
// cover return NotHandled and fall back to the boot FAT unchanged.
//
// The kernel JITs the statics below once and calls them from its
// FileExports wrappers (same pattern as the AHCI boot-volume helpers).
// Signatures mirror the kernel bridge:
//
//   Exists   (path, len, wantDir)      -> 1/0,        NotHandled
//   GetSize  (path, len)               -> bytes, -1,  NotHandled
//   Read     (path, len, buf, cap)     -> bytes, <0,  NotHandled
//   Write    (path, len, data, n, app) -> bytes, <0,  NotHandled
//   DirEntry (path, len, index, buf, cap, *isDir)
//                                      -> namelen, -1 end, -2, NotHandled
//   DirCreate/DirDelete/FileDelete     -> 0/-1/-2,    NotHandled

using System;

namespace NeutrinoOS.DDK.Storage;

/// <summary>VFS path bridge for the kernel System.IO exports (see file header).</summary>
public static unsafe class VfsPathBridge
{
    /// <summary>Returned when no non-root VFS mount covers the path.</summary>
    public const int NotHandled = -100;

    /// <summary>Existence probe (1 exists kind-matches, 0 otherwise).</summary>
    public static int Exists(char* path, int pathLen, int wantDirectory)
    {
        if (!Resolve(path, pathLen, out IFileSystem? fs, out string rel))
            return NotHandled;
        var rc = fs!.GetInfo(rel, out FileInfo? info);
        if (rc != FileResult.Success || info == null)
            return 0;
        bool isDir = info.IsDirectory;
        return (wantDirectory != 0) == isDir ? 1 : 0;
    }

    /// <summary>File size in bytes (-1 when missing).</summary>
    public static int GetSize(char* path, int pathLen)
    {
        if (!Resolve(path, pathLen, out IFileSystem? fs, out string rel))
            return NotHandled;
        var rc = fs!.GetInfo(rel, out FileInfo? info);
        if (rc != FileResult.Success || info == null || info.IsDirectory)
            return -1;
        ulong size = info.Size;
        if (size > 0x7FFFFFFF)
            size = 0x7FFFFFFF;
        return (int)size;
    }

    /// <summary>Reads a file into the caller's buffer (bytes read, or negative).</summary>
    public static int Read(char* path, int pathLen, byte* buffer, int capacity)
    {
        if (!Resolve(path, pathLen, out IFileSystem? fs, out string rel))
            return NotHandled;
        if (buffer == null || capacity <= 0)
            return -2;

        IFileHandle? file;
        if (fs!.OpenFile(rel, FileMode.Open, FileAccess.Read, out file) != FileResult.Success || file == null)
            return -1;

        int size = (int)file.Length;
        if (size > capacity)
            size = capacity;

        int total = 0;
        while (total < size)
        {
            int n = file.Read(buffer + total, size - total);
            if (n <= 0)
                break;
            total += n;
        }
        file.Dispose();
        return total;
    }

    /// <summary>Writes (or appends) a file (bytes written, or negative).</summary>
    public static int Write(char* path, int pathLen, byte* data, int count, int append)
    {
        if (!Resolve(path, pathLen, out IFileSystem? fs, out string rel))
            return NotHandled;
        if (data == null && count > 0)
            return -2;

        IFileHandle? file;
        var mode = append != 0 ? FileMode.Append : FileMode.Create;
        if (fs!.OpenFile(rel, mode, FileAccess.Write, out file) != FileResult.Success || file == null)
            return -1;

        int total = 0;
        while (total < count)
        {
            int n = file.Write(data + total, count - total);
            if (n <= 0)
                break;
            total += n;
        }
        file.Dispose();
        return total;
    }

    /// <summary>
    /// Enumerates a directory entry by index (name length + isDir, -1 at
    /// the end of the listing).
    /// </summary>
    public static int DirEntry(char* path, int pathLen, int index, char* nameBuf, int nameCapacity, int* isDir)
    {
        if (!Resolve(path, pathLen, out IFileSystem? fs, out string rel))
            return NotHandled;
        if (nameBuf == null || nameCapacity <= 0 || isDir == null)
            return -2;

        IDirectoryHandle? dir;
        if (fs!.OpenDirectory(rel, out dir) != FileResult.Success || dir == null)
            return -2;

        int result = -2;
        int i = 0;
        for (;;)
        {
            FileInfo? info = dir.ReadNext();
            if (info == null)
            {
                result = -1;   // end of listing
                break;
            }
            if (i == index)
            {
                string name = info.Name;
                int length = name.Length;
                int copy = length > nameCapacity ? nameCapacity : length;
                for (int c = 0; c < copy; c++)
                    nameBuf[c] = name[c];
                *isDir = info.IsDirectory ? 1 : 0;
                result = length;
                break;
            }
            i++;
        }
        dir.Dispose();
        return result;
    }

    /// <summary>Creates a directory (0 ok/existing, -2 error).</summary>
    public static int DirCreate(char* path, int pathLen)
    {
        if (!Resolve(path, pathLen, out IFileSystem? fs, out string rel))
            return NotHandled;
        var rc = fs!.CreateDirectory(rel);
        if (rc == FileResult.Success || rc == FileResult.AlreadyExists)
            return 0;
        return -2;
    }

    /// <summary>Deletes an empty directory (0 ok, -1 missing, -2 error).</summary>
    public static int DirDelete(char* path, int pathLen)
    {
        if (!Resolve(path, pathLen, out IFileSystem? fs, out string rel))
            return NotHandled;
        var rc = fs!.DeleteDirectory(rel);
        if (rc == FileResult.Success)
            return 0;
        if (rc == FileResult.NotFound)
            return -1;
        return -2;
    }

    /// <summary>Deletes a file (0 ok, -1 missing, -2 error).</summary>
    public static int FileDelete(char* path, int pathLen)
    {
        if (!Resolve(path, pathLen, out IFileSystem? fs, out string rel))
            return NotHandled;
        var rc = fs!.DeleteFile(rel);
        if (rc == FileResult.Success)
            return 0;
        if (rc == FileResult.NotFound)
            return -1;
        return -2;
    }

    /// <summary>
    /// Resolves a path against the VFS mount table. Only non-root mounts
    /// are considered ("/" stays with the kernel's boot-volume model);
    /// returns false when no mount covers the path (caller falls back to
    /// the boot FAT).
    /// </summary>
    private static bool Resolve(char* path, int pathLen, out IFileSystem? fs, out string rel)
    {
        fs = null;
        rel = "";
        if (path == null || pathLen <= 0)
            return false;

        var mounts = VFS.MountPoints;
        for (int i = 0; i < mounts.Count; i++)
        {
            var mp = mounts[i];
            string root = mp.Path;
            if (root.Length <= 1)
                continue;   // skip the root mount ("/") and empties

            if (!PrefixMatches(path, pathLen, root))
                continue;
            if (pathLen != root.Length)
            {
                // Must be segment-aligned: "/mnt/test2" must not match "/mnt/test".
                if (pathLen < root.Length + 1 || path[root.Length] != '/')
                    continue;
            }

            fs = mp.FileSystem;
            if (pathLen == root.Length)
                rel = "/";
            else
                rel = new string(path, root.Length, pathLen - root.Length);
            return true;
        }
        return false;
    }

    /// <summary>Ordinal prefix test of a char* against a managed string.</summary>
    private static bool PrefixMatches(char* path, int pathLen, string prefix)
    {
        if (pathLen < prefix.Length)
            return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            if (path[i] != prefix[i])
                return false;
        }
        return true;
    }
}
