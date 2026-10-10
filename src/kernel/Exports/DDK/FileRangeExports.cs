// NeutrinoOS kernel - chunked (offset-based) boot-volume file I/O exports.
//
// Kernel-side implementation of the NeutrinoOS.DDK chunked file bridge
// (src/ddk/Kernel/BootFiles.cs). sshd's SFTP subsystem streams files
// through these so transfers are no longer capped by the guest heap
// (the old whole-file bridge buffered entire files in memory and had to
// stay below the ~64 KiB safe-allocation size). See docs/SSH.md.
//
// Call convention: raw pointers only; UTF-16 char* for paths (matching
// the System.IO.File bridge in Platform/FileExports.cs). Offsets are
// int, so chunked access covers the first 2 GiB of a file.

using System.Runtime.InteropServices;

namespace NeutrinoOS.Exports.DDK;

/// <summary>Chunked boot-volume file I/O bridge (see file header).</summary>
public static unsafe class FileRangeExports
{
    /// <summary>
    /// Reads up to capacity bytes at offset from a boot-volume file.
    /// Returns the number of bytes read (0 = end of file), or a negative
    /// error code (see KernelBootReadRange). Kernel-side counterpart of
    /// BootFiles.ReadRange in the DDK.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "Kernel_BootFileReadRange")]
    public static int BootFileReadRange(char* path, int pathLen, int offset, byte* buffer, int capacity)
        => Platform.FileExports.KernelBootReadRange(path, pathLen, offset, buffer, capacity);

    /// <summary>
    /// Writes count bytes at offset into a boot-volume file, creating it
    /// when missing and extending it when appending. Returns the number
    /// of bytes written, or a negative error code (see
    /// KernelBootWriteRange). Kernel-side counterpart of
    /// BootFiles.WriteRange in the DDK.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "Kernel_BootFileWriteRange")]
    public static int BootFileWriteRange(char* path, int pathLen, int offset, byte* data, int count)
        => Platform.FileExports.KernelBootWriteRange(path, pathLen, offset, data, count);
}
