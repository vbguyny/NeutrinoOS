// NeutrinoOS korlib - System.IO.FileInfo (Phase 5)
//
// Minimal BCL-compatible FileInfo used by the Phase 5 utilities (ls -l,
// path checks, ...). Derived from FileSystemInfo (FullName/Name/Exists/
// Extension are overrides, matching the real BCL shape so JIT-compiled
// applications resolve their member references). NeutrinoOS's boot FAT
// volume has no timestamps or attributes at the bridge level, so those
// members are omitted (documented deviation; see
// docs/PHASE5-UTILITIES.md).

namespace System.IO;

/// <summary>
/// Provides properties for creating, copying, deleting, moving, and
/// opening files (Phase 5 subset: Name/FullName/DirectoryName/Extension/
/// Exists/Length).
/// </summary>
public sealed class FileInfo : FileSystemInfo
{
    private readonly string _path;

    /// <summary>Creates a FileInfo for the given path (relative paths are used as-is).</summary>
    public FileInfo(string path)
    {
        if (path == null)
            throw new ArgumentNullException("path");
        _path = path;
        FullName = Path.IsPathRooted(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), path);
    }

    /// <summary>The fully qualified path.</summary>
    public override string FullName { get; }

    /// <summary>The file name including its extension.</summary>
    public override string Name => Path.GetFileName(FullName);

    /// <summary>The file extension including the leading dot, or empty.</summary>
    public override string Extension => Path.GetExtension(FullName);

    /// <summary>True when the file exists on the boot volume.</summary>
    public override bool Exists => File.Exists(FullName);

    /// <summary>The directory portion of the path (null/empty for the root).</summary>
    public string? DirectoryName => Path.GetDirectoryName(FullName);

    /// <summary>
    /// The size of the file in bytes. Throws FileNotFoundException when
    /// the file does not exist (BCL behavior).
    /// </summary>
    public long Length
    {
        get
        {
            int size = File.GetFileSize(FullName);
            if (size < 0)
                throw new FileNotFoundException("Could not find file '" + FullName + "'");
            return size;
        }
    }
}
