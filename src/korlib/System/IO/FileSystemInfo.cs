// NeutrinoOS korlib - System.IO.FileSystemInfo (Phase 5)
//
// Base class for FileInfo (and, later, DirectoryInfo). The real BCL
// declares FullName/Name/Exists/Extension here and FileInfo overrides
// them; JIT-compiled applications reference these members on
// FileSystemInfo, so the korlib type must exist for resolution to
// succeed (previously "TypeRef not found: System.IO.FileSystemInfo").

namespace System.IO;

/// <summary>
/// Provides the base class for both <see cref="FileInfo"/> and (later)
/// directory objects. Phase 5 subset: FullName, Name, Exists and
/// Extension.
/// </summary>
public abstract class FileSystemInfo
{
    /// <summary>The full path of the file or directory.</summary>
    public abstract string FullName { get; }

    /// <summary>The name (with extension) of the file or directory.</summary>
    public abstract string Name { get; }

    /// <summary>True when the file or directory exists.</summary>
    public abstract bool Exists { get; }

    /// <summary>The extension (including the leading dot), or empty.</summary>
    public abstract string Extension { get; }
}
