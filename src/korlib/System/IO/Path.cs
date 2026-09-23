// NeutrinoOS korlib - System.IO.Path
//
// Phase 4: path string helpers for console applications. NeutrinoOS uses
// '/' as the directory separator exclusively (the FAT driver accepts
// leading '/' and subdirectory paths like "/apps/out.txt"); '\' is not a
// separator. Deviations from the official BCL: no drive letters, no
// GetFullPath normalization (paths are already root-relative), no
// GetTempFileName/GetRandomFileName/GetInvalidPathChars.

namespace System.IO;

/// <summary>
/// Performs operations on String instances that contain file or directory
/// path information. See the file header for the NeutrinoOS subset.
/// </summary>
public static class Path
{
    /// <summary>Gets the platform directory separator character ('/').</summary>
    public const char DirectorySeparatorChar = '/';

    /// <summary>Gets the platform alternate directory separator character ('/'; both separators are '/').</summary>
    public const char AltDirectorySeparatorChar = '/';

    /// <summary>Gets the path separator character used to split path lists (':').</summary>
    public const char PathSeparator = ':';

    /// <summary>Changes the extension of a path string ("" removes it).</summary>
    public static string ChangeExtension(string path, string? extension)
    {
        if (path == null)
            return null!;

        int dot = path.LastIndexOf('.');
        int sep = path.LastIndexOf('/');
        if (dot < 0 || dot < sep)
            return extension == null ? path : path + "." + extension;

        string root = path.Substring(0, dot);
        if (string.IsNullOrEmpty(extension))
            return root;
        return root + "." + extension;
    }

    /// <summary>Combines two path strings with a separating '/' (does not normalize "." or "..").</summary>
    public static string Combine(string path1, string path2)
    {
        if (path1 == null || path2 == null)
            throw new ArgumentNullException(path1 == null ? "path1" : "path2");
        if (path1.Length == 0)
            return path2;
        if (path2.Length == 0)
            return path1;
        if (IsPathRooted(path2))
            return path2;
        if (path1[path1.Length - 1] == '/')
            return path1 + path2;
        return path1 + "/" + path2;
    }

    /// <summary>Returns the directory portion of a path, or "" at the root.</summary>
    public static string? GetDirectoryName(string path)
    {
        if (path == null)
            return null;
        string trimmed = path;
        while (trimmed.Length > 1 && trimmed[trimmed.Length - 1] == '/')
            trimmed = trimmed.Substring(0, trimmed.Length - 1);
        int sep = trimmed.LastIndexOf('/');
        if (sep < 0)
            return "";
        if (sep == 0)
            return "/";
        return trimmed.Substring(0, sep);
    }

    /// <summary>Returns the file name (with extension) of a path, or "" when the path ends in a separator.</summary>
    public static string GetFileName(string path)
    {
        if (path == null)
            return null!;
        int sep = path.LastIndexOf('/');
        if (sep < 0)
            return path;
        return path.Substring(sep + 1);
    }

    /// <summary>Returns the file name without its extension.</summary>
    public static string GetFileNameWithoutExtension(string path)
    {
        string name = GetFileName(path);
        int dot = name.LastIndexOf('.');
        if (dot < 0)
            return name;
        return name.Substring(0, dot);
    }

    /// <summary>Returns the extension (including the leading '.') of a path, or "".</summary>
    public static string GetExtension(string path)
    {
        if (path == null)
            return null!;
        int dot = path.LastIndexOf('.');
        int sep = path.LastIndexOf('/');
        if (dot < 0 || dot < sep)
            return "";
        return path.Substring(dot);
    }

    /// <summary>Determines whether a path includes a root ('/' prefix).</summary>
    public static bool IsPathRooted(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        return path![0] == '/';
    }

    /// <summary>Determines whether the path has a file extension.</summary>
    public static bool HasExtension(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        int dot = path!.LastIndexOf('.');
        int sep = path.LastIndexOf('/');
        return dot >= 0 && dot > sep;
    }

    /// <summary>
    /// Resolves a path to an absolute root-relative path: relative paths
    /// are combined with the current directory (Directory.GetCurrentDirectory,
    /// set by the shell's cd built-in) and "."/".." segments are collapsed.
    /// Phase 5: this is the single place where relative paths become
    /// absolute; korlib's File/Directory/FileStream call it so relative
    /// paths work for the shell and for JIT-compiled utilities alike.
    /// </summary>
    public static string GetFullPath(string path)
    {
        if (path == null)
            throw new ArgumentNullException("path");
        if (path.Length > 0 && path[0] == '/')
            return NormalizeAbsolute(path);

        string cwd = Directory.GetCurrentDirectory();
        if (string.IsNullOrEmpty(cwd))
            cwd = "/";
        if (cwd.Length > 1 && cwd[cwd.Length - 1] == '/')
            cwd = cwd.Substring(0, cwd.Length - 1);
        return NormalizeAbsolute(cwd + "/" + path);
    }

    /// <summary>
    /// Collapses duplicate/trailing separators and "."/".." segments in
    /// an absolute path. The result always starts with '/'.
    /// </summary>
    private static string NormalizeAbsolute(string path)
    {
        const int MaxDepth = 32;
        int[] starts = new int[MaxDepth];
        int[] lens = new int[MaxDepth];
        int depth = 0;

        int i = 0;
        int n = path.Length;
        while (i < n)
        {
            while (i < n && path[i] == '/')
                i++;
            int start = i;
            while (i < n && path[i] != '/')
                i++;
            int len = i - start;
            if (len == 0)
                continue;
            if (len == 1 && path[start] == '.')
                continue;
            if (len == 2 && path[start] == '.' && path[start + 1] == '.')
            {
                if (depth > 0)
                    depth--;
                continue;
            }
            if (depth < MaxDepth)
            {
                starts[depth] = start;
                lens[depth] = len;
                depth++;
            }
        }

        if (depth == 0)
            return "/";

        var sb = new Text.StringBuilder();
        sb.Append('/');
        for (int d = 0; d < depth; d++)
        {
            if (d > 0)
                sb.Append('/');
            sb.Append(path, starts[d], lens[d]);
        }
        return sb.ToString();
    }

    /// <summary>Returns the root ("/") of the specified path.</summary>
    public static string? GetPathRoot(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        return path![0] == '/' ? "/" : "";
    }
}
