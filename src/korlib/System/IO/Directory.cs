// NeutrinoOS korlib - System.IO.Directory
//
// Phase 4: directory operations for .NET 10 console applications.
//
// SEMANTICS
// ---------
// Paths address the NeutrinoOS boot (FAT32) volume exactly like
// System.IO.File: '/' separates directories, a leading '/' is optional,
// and "/" is the root. The kernel bridge exports mirror the file ones
// (DirBootExists/DirBootCreate/DirBootDelete/DirBootEntry); enumeration
// walks the FAT directory by index.
//
// Deviations from the official BCL (per member where relevant):
//  * Return types are reduced: CreateDirectory returns void (not
//    DirectoryInfo), GetFiles/GetDirectories return string[] (not
//    DirectoryInfo/FileInfo objects).
//  * No search patterns (wildcards), no SearchOption.Recursive, no
//    enumeration options (EnumerationOptions), no security/ACL APIs,
//    no timestamps.
//  * Delete never recurses; a non-empty directory fails, like the BCL
//    when recursive is false.

using System.Runtime.InteropServices;

namespace System.IO;

/// <summary>
/// Exposes static methods for creating, moving, and enumerating through
/// directories and subdirectories (NeutrinoOS subset - see the file header).
/// </summary>
public static class Directory
{
    // ==================== Kernel bridge ====================
#if KORLIB_IL
    // IL metadata stubs; the AOT kernel provides the implementations
    // (kernel exports DirBootExists/DirBootCreate/DirBootDelete/
    // DirBootEntry, bound by token registry).
    private static unsafe int DirBootExists(char* path, int pathLen)
        => throw new PlatformNotSupportedException();
    private static unsafe int DirBootCreate(char* path, int pathLen)
        => throw new PlatformNotSupportedException();
    private static unsafe int DirBootDelete(char* path, int pathLen)
        => throw new PlatformNotSupportedException();
    private static unsafe int DirBootEntry(char* path, int pathLen, int index,
        char* nameBuf, int nameCapacity, int* isDir)
        => throw new PlatformNotSupportedException();
#else
    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int DirBootExists(char* path, int pathLen);

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int DirBootCreate(char* path, int pathLen);

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int DirBootDelete(char* path, int pathLen);

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int DirBootEntry(char* path, int pathLen, int index,
        char* nameBuf, int nameCapacity, int* isDir);
#endif

    // ==================== Internal helpers ====================

    private static unsafe bool BootDirExists(string path)
    {
        fixed (char* p = path)
            return DirBootExists(p, path.Length) == 1;
    }

    /// <summary>
    /// Normalizes a path for the bridge: "" and "." mean the root "/";
    /// the root is always reported as existing.
    /// </summary>
    private static string NormalizeDirPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "." || path == "./")
            return "/";
        // Strip a trailing separator (except for the root itself).
        if (path.Length > 1 && path[path.Length - 1] == '/')
            return path.Substring(0, path.Length - 1);
        return path;
    }

    private static bool IsRoot(string path) => path == "/";

    // ==================== Existence ====================

    /// <summary>
    /// Determines whether the given path refers to an existing directory
    /// on the boot volume. Never throws (false for null/empty paths).
    /// </summary>
    public static bool Exists(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        string normalized = NormalizeDirPath(path!);
        if (IsRoot(normalized))
            return true;
        return BootDirExists(normalized);
    }

    // ==================== Creation / deletion ====================

    /// <summary>
    /// Creates the directory at the given path. A missing parent
    /// directory is reported as an error (NeutrinoOS creates a single
    /// level; the official BCL creates intermediate directories - noted
    /// deviation). A no-op when the directory already exists.
    /// </summary>
    public static unsafe void CreateDirectory(string path)
    {
        if (path == null)
            throw new ArgumentNullException("path");
        string normalized = NormalizeDirPath(path);
        if (IsRoot(normalized) || BootDirExists(normalized))
            return;
        int result;
        fixed (char* p = normalized)
            result = DirBootCreate(p, normalized.Length);
        if (result != 0)
            throw new IOException("Could not create directory '" + path + "'");
    }

    /// <summary>
    /// Deletes an empty directory; throws DirectoryNotFoundException
    /// when it does not exist and IOException when it is not empty.
    /// </summary>
    public static void Delete(string path) => Delete(path, false);

    /// <summary>
    /// Deletes a directory. Recursion is not supported in Phase 4: a
    /// non-empty directory fails even when recursive is true (documented
    /// limitation; delete the contents first).
    /// </summary>
    public static unsafe void Delete(string path, bool recursive)
    {
        if (path == null)
            throw new ArgumentNullException("path");
        string normalized = NormalizeDirPath(path);
        if (IsRoot(normalized))
            throw new IOException("Cannot delete the root directory");
        if (!BootDirExists(normalized))
            throw new DirectoryNotFoundException("Could not find directory '" + path + "'");
        int result;
        fixed (char* p = normalized)
            result = DirBootDelete(p, normalized.Length);
        if (result != 0)
            throw new IOException("Could not delete directory '" + path + "' (not empty?)");
    }

    // ==================== Enumeration ====================

    /// <summary>Returns the names of files in the specified directory.</summary>
    public static string[] GetFiles(string path) => GetEntries(path, 1);

    /// <summary>Returns the names of subdirectories in the specified directory.</summary>
    public static string[] GetDirectories(string path) => GetEntries(path, 2);

    /// <summary>Returns the names of files and subdirectories in the specified directory.</summary>
    public static string[] GetFileSystemEntries(string path) => GetEntries(path, 0);

    private static string[] GetEntries(string path, int kindFilter)
    {
        if (path == null)
            throw new ArgumentNullException("path");
        string normalized = NormalizeDirPath(path);
        if (!IsRoot(normalized) && !BootDirExists(normalized))
            throw new DirectoryNotFoundException("Could not find directory '" + path + "'");

        Collections.Generic.List<string> entries = new Collections.Generic.List<string>();
        string prefix = IsRoot(normalized) ? "/" : normalized + "/";

        char[] nameBuffer = new char[256];
        int index = 0;
        for (;;)
        {
            int length;
            int isDir;
            unsafe
            {
                fixed (char* p = normalized)
                fixed (char* n = nameBuffer)
                    length = DirBootEntry(p, normalized.Length, index, n, nameBuffer.Length, &isDir);
            }
            if (length == -1)
                break;
            if (length < 0)
                throw new IOException("Could not enumerate directory '" + path + "'");
            if (kindFilter == 0 || (kindFilter == 2) == (isDir == 1))
                entries.Add(prefix + new string(nameBuffer, 0, length));
            index++;
        }

        string[] result = new string[entries.Count];
        for (int i = 0; i < entries.Count; i++)
            result[i] = entries[i];
        return result;
    }

    // ==================== Current directory ====================

    /// <summary>
    /// Returns the current directory. NeutrinoOS keeps no process
    /// working directory; the root "/" is reported (all paths used by
    /// the shell and applications are root-relative).
    /// </summary>
    public static string GetCurrentDirectory() => "/";

    /// <summary>
    /// Sets the current directory. NeutrinoOS keeps no process working
    /// directory; only "/" (or an empty string) is accepted, any other
    /// value throws IOException.
    /// </summary>
    public static void SetCurrentDirectory(string path)
    {
        if (path == null)
            throw new ArgumentNullException("path");
        string normalized = NormalizeDirPath(path);
        if (!IsRoot(normalized) && !BootDirExists(normalized))
            throw new DirectoryNotFoundException("Could not find directory '" + path + "'");
        if (!IsRoot(normalized))
            throw new IOException("NeutrinoOS has no process working directory; only '/' is supported");
    }
}
