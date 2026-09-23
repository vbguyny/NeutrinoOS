// NeutrinoOS korlib - System.IO.DirectoryInfo (Phase 5)
//
// The real BCL's Directory.CreateDirectory(string) returns a
// DirectoryInfo, and compiled applications carry that return type in
// their MemberRef signatures - so korlib must provide the type and match
// the signature for JIT resolution to succeed (previously the mkdir/cp/mv
// utilities failed with "signature mismatch" on CreateDirectory).

namespace System.IO;

/// <summary>
/// Exposes instance methods for creating, moving, and enumerating
/// through directories (Phase 5 subset: FullName/Name/Exists/Extension).
/// </summary>
public sealed class DirectoryInfo : FileSystemInfo
{
    /// <summary>Creates a DirectoryInfo for the given path.</summary>
    public DirectoryInfo(string path)
    {
        if (path == null)
            throw new ArgumentNullException("path");
        FullName = Path.IsPathRooted(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), path);
    }

    /// <summary>The fully qualified path.</summary>
    public override string FullName { get; }

    /// <summary>The directory name.</summary>
    public override string Name => Path.GetFileName(FullName);

    /// <summary>The extension (directories normally have none; empty).</summary>
    public override string Extension => Path.GetExtension(FullName);

    /// <summary>True when the directory exists.</summary>
    public override bool Exists => Directory.Exists(FullName);

    /// <summary>Creates the directory (and returns it) when missing.</summary>
    public DirectoryInfo Create()
    {
        Directory.CreateDirectory(FullName);
        return this;
    }
}
