// NeutrinoOS korlib - System.IO.File
//
// Phase 4: static file operations for .NET 10 console applications.
//
// SEMANTICS
// ---------
// All paths address the NeutrinoOS boot (FAT32) volume; '/' separates
// directories and a leading '/' is optional ("/apps/out.txt",
// "out.txt", "apps/out.txt" all work). The bytes travel through the
// kernel's boot-volume bridge (see the [UnmanagedCallersOnly] exports in
// the kernel: FileBootRead/FileBootWrite/FileBootSize/FileBootExists/
// FileBootDelete), which mounts the FAT driver on demand and performs
// the actual I/O - the same mechanism the shell's `run` command uses.
//
// Deviations from the official BCL (per member where relevant):
//  * No file locking/sharing (no FileShare), no attributes or ACLs.
//  * Time-stamp accessors and Encrypted/Compressed checks are absent.
//  * Move is implemented as copy + delete (the FAT driver has no rename
//    exposed across the bridge yet); Copy/Move with overwrite supported.
//  * WriteAllText/AppendAllText default to UTF-8 without BOM.
//
// MANAGED/UNMANAGED BOUNDARY
// --------------------------
// The bridge primitives take raw pointers into pinned managed buffers
// (`fixed (...)`), never managed object references: the kernel side is
// AOT code that must not see GC references. The path string and byte
// arrays stay pinned for the duration of each call.

using System.Runtime.InteropServices;

namespace System.IO;

/// <summary>
/// Provides static methods for the creation, copying, deletion, moving,
/// and opening of files (NeutrinoOS subset - see the file header).
/// </summary>
public static class File
{
    // ==================== Kernel bridge ====================
#if KORLIB_IL
    // IL metadata stubs. The AOT kernel provides the real implementations
    // (kernel exports FileBootRead/... registered in the token registry);
    // JIT-compiled code resolves these tokens to the AOT versions.
    private static unsafe int FileBootRead(char* path, int pathLen, byte* buffer, int capacity)
        => throw new PlatformNotSupportedException();
    private static unsafe int FileBootWrite(char* path, int pathLen, byte* data, int length, int append)
        => throw new PlatformNotSupportedException();
    private static unsafe int FileBootSize(char* path, int pathLen)
        => throw new PlatformNotSupportedException();
    private static unsafe int FileBootExists(char* path, int pathLen)
        => throw new PlatformNotSupportedException();
    private static unsafe int FileBootDelete(char* path, int pathLen)
        => throw new PlatformNotSupportedException();
#else
    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int FileBootRead(char* path, int pathLen, byte* buffer, int capacity);

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int FileBootWrite(char* path, int pathLen, byte* data, int length, int append);

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int FileBootSize(char* path, int pathLen);

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int FileBootExists(char* path, int pathLen);

    [DllImport("*", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int FileBootDelete(char* path, int pathLen);
#endif

    // ==================== Internal helpers ====================

    private static unsafe int BootSize(string path)
    {
        fixed (char* p = path)
            return FileBootSize(p, path.Length);
    }

    private static unsafe int BootExistsRaw(string path)
    {
        fixed (char* p = path)
            return FileBootExists(p, path.Length);
    }

    private static unsafe int BootRead(string path, byte[] buffer, int capacity)
    {
        fixed (char* p = path)
        fixed (byte* b = buffer)
            return FileBootRead(p, path.Length, b, capacity);
    }

    private static unsafe int BootWrite(string path, byte[] data, int length, bool append)
    {
        fixed (char* p = path)
        fixed (byte* b = data)
            return FileBootWrite(p, path.Length, b, length, append ? 1 : 0);
    }

    private static unsafe int BootDeleteRaw(string path)
    {
        fixed (char* p = path)
            return FileBootDelete(p, path.Length);
    }

    private static bool IsNullPath(string? path) => string.IsNullOrEmpty(path);

    // ==================== Existence ====================

    /// <summary>
    /// Determines whether the specified file exists (false for null,
    /// empty, missing files, and directories). Never throws.
    /// </summary>
    public static bool Exists(string? path)
    {
        if (IsNullPath(path))
            return false;
        return BootExistsRaw(path!) == 1;
    }

    // ==================== Deletion ====================

    /// <summary>Deletes the specified file; throws FileNotFoundException when it does not exist.</summary>
    public static void Delete(string path)
    {
        if (path == null)
            throw new ArgumentNullException("path");
        int result = BootDeleteRaw(path);
        if (result == -1)
            throw new FileNotFoundException("Could not find file '" + path + "'");
        if (result != 0)
            throw new IOException("Could not delete file '" + path + "'");
    }

    // ==================== Reading ====================

    /// <summary>Opens a binary file, reads its contents, and closes it.</summary>
    public static byte[] ReadAllBytes(string path)
    {
        if (path == null)
            throw new ArgumentNullException("path");
        int size = BootSize(path);
        if (size < 0)
            throw new FileNotFoundException("Could not find file '" + path + "'");
        byte[] buffer = new byte[size];
        if (size == 0)
            return buffer;
        int read = BootRead(path, buffer, size);
        if (read != size)
            throw new IOException("Could not read file '" + path + "' (short read)");
        return buffer;
    }

    /// <summary>Opens a text file, reads all text (UTF-8), and closes it.</summary>
    public static string ReadAllText(string path)
    {
        byte[] bytes = ReadAllBytes(path);
        return Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length);
    }

    /// <summary>Reads all text using the specified encoding.</summary>
    public static string ReadAllText(string path, Text.Encoding encoding)
    {
        if (encoding == null)
            throw new ArgumentNullException("encoding");
        byte[] bytes = ReadAllBytes(path);
        return encoding.GetString(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Reads all lines (terminated by '\n'; a trailing '\r' is stripped).
    /// An empty file yields an empty array; a trailing terminator does
    /// not produce a final empty line.
    /// </summary>
    public static string[] ReadAllLines(string path)
    {
        string text = ReadAllText(path);
        return SplitLines(text);
    }

    /// <summary>Reads all lines using the specified encoding.</summary>
    public static string[] ReadAllLines(string path, Text.Encoding encoding)
    {
        string text = ReadAllText(path, encoding);
        return SplitLines(text);
    }

    private static string[] SplitLines(string text)
    {
        if (text.Length == 0)
            return new string[0];
        Collections.Generic.List<string> lines = new Collections.Generic.List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                int len = i - start;
                if (len > 0 && text[start + len - 1] == '\r')
                    len--;
                lines.Add(text.Substring(start, len));
                start = i + 1;
            }
        }
        if (start < text.Length)
            lines.Add(text.Substring(start));
        return lines.ToArray();
    }

    // ==================== Writing ====================

    /// <summary>Creates or overwrites a file with the given bytes.</summary>
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        if (path == null)
            throw new ArgumentNullException("path");
        if (bytes == null)
            throw new ArgumentNullException("bytes");
        int written = BootWrite(path, bytes, bytes.Length, false);
        if (written != bytes.Length)
            throw new IOException("Could not write file '" + path + "'");
    }

    /// <summary>Creates or overwrites a text file (UTF-8 without BOM).</summary>
    public static void WriteAllText(string path, string? contents)
    {
        if (path == null)
            throw new ArgumentNullException("path");
        byte[] bytes = Text.Encoding.UTF8.GetBytes(contents ?? "");
        WriteAllBytes(path, bytes);
    }

    /// <summary>Creates or overwrites a text file using the specified encoding.</summary>
    public static void WriteAllText(string path, string? contents, Text.Encoding encoding)
    {
        if (encoding == null)
            throw new ArgumentNullException("encoding");
        if (path == null)
            throw new ArgumentNullException("path");
        byte[] bytes = encoding.GetBytes(contents ?? "");
        WriteAllBytes(path, bytes);
    }

    /// <summary>Writes lines to a new or overwritten file, each followed by '\n'.</summary>
    public static void WriteAllLines(string path, string[] contents)
    {
        if (contents == null)
            throw new ArgumentNullException("contents");
        Text.StringBuilder sb = new Text.StringBuilder();
        for (int i = 0; i < contents.Length; i++)
        {
            sb.Append(contents[i]);
            sb.Append('\n');
        }
        WriteAllText(path, sb.ToString());
    }

    /// <summary>Appends text to a file, creating it when missing (UTF-8 without BOM).</summary>
    public static void AppendAllText(string path, string? contents)
    {
        if (path == null)
            throw new ArgumentNullException("path");
        byte[] bytes = Text.Encoding.UTF8.GetBytes(contents ?? "");
        int written = BootWrite(path, bytes, bytes.Length, true);
        if (written != bytes.Length)
            throw new IOException("Could not append to file '" + path + "'");
    }

    /// <summary>Appends text using the specified encoding.</summary>
    public static void AppendAllText(string path, string? contents, Text.Encoding encoding)
    {
        if (encoding == null)
            throw new ArgumentNullException("encoding");
        if (path == null)
            throw new ArgumentNullException("path");
        byte[] bytes = encoding.GetBytes(contents ?? "");
        int written = BootWrite(path, bytes, bytes.Length, true);
        if (written != bytes.Length)
            throw new IOException("Could not append to file '" + path + "'");
    }

    // ==================== Copying and moving ====================

    /// <summary>Copies an existing file to a new file; throws when the destination exists.</summary>
    public static void Copy(string sourceFileName, string destFileName)
        => Copy(sourceFileName, destFileName, false);

    /// <summary>Copies an existing file to a new file, optionally overwriting the destination.</summary>
    public static void Copy(string sourceFileName, string destFileName, bool overwrite)
    {
        if (sourceFileName == null)
            throw new ArgumentNullException("sourceFileName");
        if (destFileName == null)
            throw new ArgumentNullException("destFileName");
        if (!overwrite && Exists(destFileName))
            throw new IOException("The destination file '" + destFileName + "' already exists");
        byte[] bytes = ReadAllBytes(sourceFileName);
        WriteAllBytes(destFileName, bytes);
    }

    /// <summary>
    /// Moves a file (implemented as copy + delete on NeutrinoOS; see the
    /// file header). The source must exist; the destination must not.
    /// </summary>
    public static void Move(string sourceFileName, string destFileName)
    {
        if (sourceFileName == null)
            throw new ArgumentNullException("sourceFileName");
        if (destFileName == null)
            throw new ArgumentNullException("destFileName");
        if (Exists(destFileName))
            throw new IOException("The destination file '" + destFileName + "' already exists");
        byte[] bytes = ReadAllBytes(sourceFileName);
        WriteAllBytes(destFileName, bytes);
        Delete(sourceFileName);
    }

    // ==================== Stream factories ====================

    /// <summary>Opens an existing file for reading.</summary>
    public static FileStream OpenRead(string path)
        => new FileStream(path, FileMode.Open, FileAccess.Read);

    /// <summary>Opens an existing file (or creates it) for writing.</summary>
    public static FileStream OpenWrite(string path)
        => new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write);

    /// <summary>Creates or overwrites a file for read/write access.</summary>
    public static FileStream Create(string path)
        => new FileStream(path, FileMode.Create, FileAccess.ReadWrite);

    /// <summary>Opens a file with the specified mode and read/write access.</summary>
    public static FileStream Open(string path, FileMode mode)
        => new FileStream(path, mode, FileAccess.ReadWrite);

    /// <summary>Opens a file with the specified mode and access.</summary>
    public static FileStream Open(string path, FileMode mode, FileAccess access)
        => new FileStream(path, mode, access);
}
