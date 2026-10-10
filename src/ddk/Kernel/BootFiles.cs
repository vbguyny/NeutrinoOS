// NeutrinoOS DDK - chunked (offset-based) boot-volume file access.
//
// Wrappers for the Kernel_BootFileReadRange / Kernel_BootFileWriteRange
// exports (src/kernel/Exports/DDK/FileRangeExports.cs), resolved by
// [DllImport("*")] name at JIT time. Services use these to stream files
// without loading them whole into the guest heap (sshd's SFTP subsystem
// transfers files of any size this way - see docs/SSH.md).
//
// Offsets are int: chunked access covers the first 2 GiB of a file, only
// the FAT boot volume (not virtual /dev files or non-root VFS mounts),
// and does not support sparse writes (writing past EOF fails).

using System.Runtime.InteropServices;

namespace NeutrinoOS.DDK.Kernel;

/// <summary>Chunked (offset-based) file I/O on the boot volume.</summary>
public static unsafe class BootFiles
{
    [DllImport("*", EntryPoint = "Kernel_BootFileReadRange")]
    private static extern int BootFileReadRangeRaw(char* path, int pathLen, int offset, byte* buffer, int capacity);

    [DllImport("*", EntryPoint = "Kernel_BootFileWriteRange")]
    private static extern int BootFileWriteRangeRaw(char* path, int pathLen, int offset, byte* data, int count);

    /// <summary>
    /// Reads up to <paramref name="capacity"/> bytes at
    /// <paramref name="offset"/> into <paramref name="buffer"/>.
    /// Returns the number of bytes read (0 = end of file), or a negative
    /// error code (-3 when the path is not on the boot volume).
    /// </summary>
    public static int ReadRange(string path, int offset, byte[] buffer, int capacity)
    {
        if (path == null || path.Length == 0 || buffer == null)
            return -1;
        if (offset < 0 || capacity <= 0 || capacity > buffer.Length)
            return -1;

        fixed (char* p = path)
        fixed (byte* b = buffer)
        {
            return BootFileReadRangeRaw(p, path.Length, offset, b, capacity);
        }
    }

    /// <summary>
    /// Writes <paramref name="count"/> bytes from <paramref name="data"/>
    /// at <paramref name="offset"/>, creating the file when missing and
    /// extending it when appending. Returns the number of bytes written,
    /// or a negative error code (-5 when writing past end of file).
    /// </summary>
    public static int WriteRange(string path, int offset, byte[] data, int count)
    {
        if (path == null || path.Length == 0 || data == null)
            return -1;
        if (offset < 0 || count < 0 || count > data.Length)
            return -1;
        if (count == 0)
            return 0;

        fixed (char* p = path)
        fixed (byte* d = data)
        {
            return BootFileWriteRangeRaw(p, path.Length, offset, d, count);
        }
    }
}
