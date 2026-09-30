// NeutrinoOS Phase 10 - exFAT filesystem driver (Task 3).
//
// Implements the DDK IFileSystem interface over an ExFatVolume: mount /
// unmount with VolumeDirty maintenance, path resolution through entry
// sets, file and directory creation, deletion and rename, and the
// file handle with cluster allocation (NoFatChain contiguous streams
// with a FAT-chain fallback).

using System;
using NeutrinoOS.DDK.Drivers;
using NeutrinoOS.DDK.Kernel;

namespace NeutrinoOS.DDK.Storage.ExFat;

/// <summary>
/// exFAT filesystem driver. Mount it through the VFS on any block
/// device whose first sector carries a valid exFAT boot region; all
/// path operations are case-insensitive through the volume up-case
/// table (Windows-compatible).
/// </summary>
public unsafe class ExFatFileSystem : IFileSystem
{
    private ExFatVolume _vol = null!;
    private bool _mounted;
    private bool _readOnly;

    // Mount options (Task 4): accepted and validated; the DDK FileInfo
    // model does not carry ownership, so uid/gid/umask are stored for
    // reporting and future use.
    private int _uid;
    private int _gid;
    private int _umask = 0x16;   // 022 octal
    private bool _asciiNames;

    /// <summary>Identifier used in mount option validation.</summary>
    public const string TypeName = "exfat";

    /// <summary>Inside view of the volume (used by fsck and automount).</summary>
    public ExFatVolume Volume => _vol;

    #region IDriver

    /// <summary>Driver name ("exfat").</summary>
    public string DriverName => "exfat";

    /// <summary>Driver version.</summary>
    public Version DriverVersion => new Version(1, 0, 0);

    /// <summary>This driver is a filesystem.</summary>
    public DriverType Type => DriverType.Filesystem;

    /// <summary>Driver lifecycle state.</summary>
    public DriverState State { get; private set; } = DriverState.Loaded;

    /// <summary>Marks the driver running.</summary>
    public bool Initialize()
    {
        State = DriverState.Running;
        return true;
    }

    /// <summary>Unmounts when needed and stops the driver.</summary>
    public void Shutdown()
    {
        if (_mounted)
            Unmount();
        State = DriverState.Stopped;
    }

    /// <summary>No-op suspend.</summary>
    public void Suspend() { }

    /// <summary>No-op resume.</summary>
    public void Resume() { }

    #endregion

    #region IFileSystem

    /// <summary>Filesystem name shown by mount/df ("exFAT").</summary>
    public string FilesystemName => "exFAT";

    /// <summary>exFAT is read/write, case-preserving/case-insensitive, with timestamps.</summary>
    public FilesystemCapabilities Capabilities =>
        FilesystemCapabilities.Read |
        FilesystemCapabilities.Write |
        FilesystemCapabilities.CasePreserving |
        FilesystemCapabilities.Timestamps |
        FilesystemCapabilities.SparseFiles;

    /// <summary>True once mounted.</summary>
    public bool IsMounted => _mounted;

    /// <summary>Volume label from the root directory ("" when none).</summary>
    public string? VolumeLabel => _mounted ? _vol.VolumeLabel : null;

    /// <summary>Total bytes in the cluster heap.</summary>
    public ulong TotalBytes => _mounted ? (ulong)_vol.ClusterCount * _vol.ClusterSize : 0;

    /// <summary>Free bytes derived from the allocation bitmap.</summary>
    public ulong FreeBytes => _mounted ? (ulong)_vol.FreeClusterCount * _vol.ClusterSize : 0;

    /// <summary>uid mount option (accepted; not applied by the DDK file model).</summary>
    public int Uid => _uid;

    /// <summary>gid mount option (accepted; not applied by the DDK file model).</summary>
    public int Gid => _gid;

    /// <summary>umask mount option (accepted; not applied by the DDK file model).</summary>
    public int Umask => _umask;

    /// <summary>
    /// Parses comma-separated mount options (ro, rw, uid=, gid=,
    /// umask=, iocharset=). Returns null on success or an error string.
    /// </summary>
    /// <param name="options">Option string from the mount command.</param>
    public string? SetMountOptions(string options)
    {
        if (string.IsNullOrEmpty(options))
            return null;
        string[] parts = SplitOptions(options);
        for (int i = 0; i < parts.Length; i++)
        {
            string o = parts[i];
            if (o.Length == 0)
                continue;
            if (ExFatDir.NamesEqual(o, "ro"))
            {
                _readOnly = true;
            }
            else if (ExFatDir.NamesEqual(o, "rw"))
            {
                _readOnly = false;
            }
            else if (StartsWithStr(o, "uid="))
            {
                _uid = ParseInt(o, 4);
            }
            else if (StartsWithStr(o, "gid="))
            {
                _gid = ParseInt(o, 4);
            }
            else if (StartsWithStr(o, "umask="))
            {
                _umask = ParseOctal(o, 6);
            }
            else if (StartsWithStr(o, "iocharset="))
            {
                string cs = o.Substring(10);
                if (ExFatDir.NamesEqual(cs, "utf8") || ExFatDir.NamesEqual(cs, "utf-8"))
                    _asciiNames = false;
                else if (ExFatDir.NamesEqual(cs, "ascii"))
                    _asciiNames = true;
                else
                    return "unknown iocharset: " + cs;
            }
            else
            {
                return "unknown option: " + o;
            }
        }
        return null;
    }

    /// <summary>Splits an option string on commas (no LINQ, JIT-safe).</summary>
    private static string[] SplitOptions(string options)
    {
        int count = 1;
        for (int i = 0; i < options.Length; i++)
            if (options[i] == ',')
                count++;
        var parts = new string[count];
        int part = 0;
        int start = 0;
        for (int i = 0; i <= options.Length; i++)
        {
            if (i == options.Length || options[i] == ',')
            {
                parts[part++] = options.Substring(start, i - start);
                start = i + 1;
            }
        }
        return parts;
    }

    /// <summary>String starts-with helper (JIT-safe).</summary>
    private static bool StartsWithStr(string s, string prefix)
    {
        if (s.Length < prefix.Length)
            return false;
        for (int i = 0; i < prefix.Length; i++)
            if (s[i] != prefix[i])
                return false;
        return true;
    }

    /// <summary>Decimal parse starting at an offset (0 on failure).</summary>
    private static int ParseInt(string s, int from)
    {
        int v = 0;
        for (int i = from; i < s.Length; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9')
                break;
            v = v * 10 + (c - '0');
        }
        return v;
    }

    /// <summary>Octal parse starting at an offset (0 on failure).</summary>
    private static int ParseOctal(string s, int from)
    {
        int v = 0;
        for (int i = from; i < s.Length; i++)
        {
            char c = s[i];
            if (c < '0' || c > '7')
                break;
            v = v * 8 + (c - '0');
        }
        return v;
    }

    /// <summary>
    /// Probes a block device for an exFAT volume: JumpBoot and
    /// FileSystemName fields plus a valid boot checksum (Task 4).
    /// </summary>
    /// <param name="device">Device to probe.</param>
    public bool Probe(IBlockDevice device)
    {
        if (device == null || device.BlockSize == 0)
            return false;

        var scratch = new ExFatScratch();
        byte* buf = scratch.Get(64 * 1024);
        if (buf == null)
            return false;
        try
        {
            int rc = device.Read(0, 1, buf);
            if (rc != 1)
                return false;
            if (buf[0] != 0xEB || buf[1] != 0x76 || buf[2] != 0x90)
                return false;
            if (buf[3] != 'E' || buf[4] != 'X' || buf[5] != 'F' || buf[6] != 'A' ||
                buf[7] != 'T' || buf[8] != ' ' || buf[9] != ' ' || buf[10] != ' ')
                return false;

            byte sectorShift = buf[108];
            if (sectorShift < 9 || sectorShift > 12)
                return false;
            uint sectorSize = 1u << sectorShift;
            if (device.BlockSize > sectorSize)
                return false;

            // Validate the boot checksum over sectors 0-10 (main region).
            if (12UL * sectorSize > 64 * 1024)
                return false;
            uint deviceBlocks = (uint)(12UL * sectorSize / device.BlockSize);
            rc = device.Read(0, deviceBlocks, buf);
            if (rc != (int)deviceBlocks)
                return false;

            uint computed = ExFatChecksum.ComputeBootChecksum(buf, 11, (int)sectorSize);
            uint stored = ExFatBytes.Read32FromPtr(buf, 11 * (int)sectorSize);
            return computed == stored;
        }
        finally
        {
            scratch.Release();
        }
    }

    /// <summary>
    /// Mounts the volume. Fails when the boot regions are invalid or the
    /// up-case table checksum does not match (spec 7.2.4). Read-write
    /// mounts set the VolumeDirty flag; <see cref="Unmount"/> clears it.
    /// </summary>
    /// <param name="device">Block device holding the volume.</param>
    /// <param name="readOnly">True to mount read-only.</param>
    public FileResult Mount(IBlockDevice? device, bool readOnly = false)
    {
        if (_mounted)
            return FileResult.AlreadyExists;
        if (device == null)
            return FileResult.InvalidPath;

        _readOnly = readOnly;
        _vol = new ExFatVolume();
        int rc = _vol.Mount(device, readOnly);
        if (rc != 0)
        {
            LogError("mount failed: " + _vol.LastError, 0, 0);
            return FileResult.IoError;
        }
        if (!_vol.UpcaseChecksumOk)
        {
            LogError("up-case table checksum mismatch", 0, 0);
            return FileResult.IoError;
        }

        // Read the volume label from the root directory.
        var root = new ExFatDir(_vol);
        if (!root.LoadRootDirectory())
            return FileResult.IoError;
        byte[] lbl = new byte[ExFatConst.EntrySize];
        int li = root.FindPrimaryEntry(ExFatConst.EntryVolumeLabel, lbl);
        _vol.VolumeLabel = li >= 0 ? ReadLabel(lbl) : "";

        if (!readOnly)
        {
            // VolumeDirty is excluded from the boot checksum, so both
            // boot regions can be updated in place (spec 3.1).
            _vol.SetVolumeDirty(true);
        }
        _mounted = true;
        return FileResult.Success;
    }

    /// <summary>Decodes the 0x83 volume label entry.</summary>
    private static string ReadLabel(byte[] entry)
    {
        int len = entry[1];
        if (len < 0 || len > ExFatConst.MaxLabelChars)
            len = 0;
        var chars = new char[len];
        for (int i = 0; i < len; i++)
            chars[i] = (char)ExFatBytes.Read16(entry, 2 + i * 2);
        return new string(chars);
    }

    /// <summary>
    /// Unmounts: flushes the allocation bitmap, updates PercentInUse,
    /// clears VolumeDirty and flushes the device.
    /// </summary>
    public FileResult Unmount()
    {
        if (!_mounted)
            return FileResult.NotFound;

        if (!_readOnly)
        {
            _vol.FlushBitmap();
            uint used = _vol.ClusterCount - _vol.FreeClusterCount;
            byte percent = _vol.ClusterCount == 0
                ? (byte)0
                : (byte)((ulong)used * 100 / _vol.ClusterCount);
            _vol.WritePercentInUse(percent);
            _vol.SetVolumeDirty(false);
        }
        _vol.Device.Flush();
        _vol.Release();
        _mounted = false;
        return FileResult.Success;
    }

    /// <summary>Get information about a file or directory.</summary>
    /// <param name="path">Path relative to the mount point.</param>
    /// <param name="info">Receives the entry information.</param>
    public FileResult GetInfo(string path, out FileInfo? info)
    {
        info = null;
        var entry = new ExFatDirEntry();
        int rc = Resolve(path, entry);
        if (rc < 0)
            return (FileResult)rc;
        info = ToFileInfo(path, entry);
        return FileResult.Success;
    }

    /// <summary>Fills a DDK FileInfo from a directory entry set.</summary>
    private FileInfo ToFileInfo(string path, ExFatDirEntry entry)
    {
        var info = new FileInfo();
        info.Name = entry.Name;
        info.Path = path;
        info.Type = entry.ToEntryType();
        info.Size = entry.IsDirectory ? 0 : (ulong)entry.ValidDataLength;
        info.Attributes = entry.ToFileAttributes();
        info.CreationTime = ExFatTime.ToEpochSeconds(entry.CreateTime);
        info.ModificationTime = ExFatTime.ToEpochSeconds(entry.ModifyTime);
        info.AccessTime = ExFatTime.ToEpochSeconds(entry.AccessTime);
        return info;
    }

    /// <summary>
    /// Resolves a path to its entry set. "/" resolves to the root
    /// directory (its synthetic entry has no name).
    /// </summary>
    /// <param name="path">Path like "/dir/file.txt".</param>
    /// <param name="entry">Receives the entry set.</param>
    /// <returns>0 on success, negative FileResult on failure.</returns>
    public int Resolve(string path, ExFatDirEntry entry)
    {
        if (!_mounted)
            return (int)FileResult.IoError;

        string leaf;
        var dir = ResolveParent(path, out leaf);
        if (dir == null)
            return (int)FileResult.NotFound;

        if (leaf.Length == 0)
        {
            entry.EntryIndex = -1;
            entry.Attributes = ExFatConst.AttrDirectory;
            entry.Name = "";
            entry.FirstCluster = _vol.RootDirCluster;
            entry.NoFatChain = false;
            entry.DataLength = dir.DataLength;
            entry.ValidDataLength = dir.DataLength;
            return 0;
        }

        int index = dir.FindEntry(leaf, entry);
        return index >= 0 ? 0 : (int)FileResult.NotFound;
    }

    /// <summary>
    /// Resolves the directory that contains the last path component.
    /// Returns null when an intermediate component is missing or not a
    /// directory.
    /// </summary>
    /// <param name="path">Path to resolve.</param>
    /// <param name="leaf">Receives the last component ("" for root).</param>
    public ExFatDir? ResolveParent(string path, out string leaf)
    {
        leaf = "";
        string norm = NormalizePath(path);
        if (norm.Length == 0)
        {
            var root = new ExFatDir(_vol);
            if (!root.LoadRootDirectory())
                return null;
            return root;
        }

        var current = new ExFatDir(_vol);
        if (!current.LoadRootDirectory())
            return null;

        int start = 0;
        while (true)
        {
            int slash = norm.IndexOf('/', start);
            string component = slash < 0 ? norm.Substring(start) : norm.Substring(start, slash - start);
            bool last = slash < 0;

            if (last)
            {
                leaf = component;
                return current;
            }

            if (component.Length == 0)
            {
                start = slash + 1;
                continue;
            }

            var child = new ExFatDirEntry();
            int idx = current.FindEntry(component, child);
            if (idx < 0 || !child.IsDirectory)
                return null;

            var next = new ExFatDir(_vol);
            if (!next.LoadDirectory(child.FirstCluster, child.NoFatChain, child.DataLength))
                return null;
            next.SelfEntry = child;
            next.SelfParent = current;
            current = next;
            start = slash + 1;
        }
    }

    /// <summary>Normalizes a path: trims leading/trailing slashes.</summary>
    /// <param name="path">Input path.</param>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "";
        int start = 0;
        int end = path.Length;
        while (start < end && (path[start] == '/' || path[start] == '\\'))
            start++;
        while (end > start && (path[end - 1] == '/' || path[end - 1] == '\\'))
            end--;
        return path.Substring(start, end - start);
    }

    /// <summary>Opens a file per the DDK open modes.</summary>
    /// <param name="path">Path relative to the mount point.</param>
    /// <param name="mode">Open mode.</param>
    /// <param name="access">Requested access.</param>
    /// <param name="handle">Receives the file handle.</param>
    public FileResult OpenFile(string path, FileMode mode, FileAccess access, out IFileHandle? handle)
    {
        handle = null;
        if (!_mounted)
            return FileResult.IoError;

        string leaf;
        var dir = ResolveParent(path, out leaf);
        if (dir == null || leaf.Length == 0)
            return FileResult.NotFound;

        var entry = new ExFatDirEntry();
        int index = dir.FindEntry(leaf, entry);
        bool exists = index >= 0;

        if (exists && entry.IsDirectory)
            return FileResult.IsADirectory;

        switch (mode)
        {
            case FileMode.Open:
            case FileMode.Truncate:
            case FileMode.Append:
                if (!exists)
                    return FileResult.NotFound;
                break;
            case FileMode.CreateNew:
                if (exists)
                    return FileResult.AlreadyExists;
                break;
            case FileMode.OpenOrCreate:
            case FileMode.Create:
                break;
        }

        if (exists && (access & FileAccess.Write) != 0)
        {
            if (_readOnly)
                return FileResult.ReadOnly;
            if ((entry.Attributes & ExFatConst.AttrReadOnly) != 0)
                return FileResult.AccessDenied;
        }

        if (!exists)
        {
            if (_readOnly)
                return FileResult.ReadOnly;
            FileResult cr = CreateFileEntry(dir, leaf, out entry);
            if (cr != FileResult.Success)
                return cr;
        }
        else if (mode == FileMode.Truncate || mode == FileMode.Create)
        {
            if (_readOnly)
                return FileResult.ReadOnly;
            TruncateEntry(dir, entry, 0);
        }

        var fh = new ExFatFileHandle(this, _vol, dir, entry);
        fh.Opened(mode == FileMode.Append);
        handle = fh;
        return FileResult.Success;
    }

    /// <summary>Creates an empty file entry set in a directory.</summary>
    /// <param name="dir">Parent directory.</param>
    /// <param name="name">File name.</param>
    /// <param name="entry">Receives the created entry.</param>
    public FileResult CreateFileEntry(ExFatDir dir, string name, out ExFatDirEntry entry)
    {
        entry = new ExFatDirEntry();
        if (name.Length == 0 || name.Length > ExFatConst.MaxNameChars)
            return FileResult.NameTooLong;

        uint now = ExFatTime.FromEpochSeconds(ExFatTime.NowEpoch());
        byte[] set = ExFatDir.BuildSet(_vol, name, 0, 0, 0, 0, false, now, now, now);
        int at = dir.InsertSet(set);
        if (at < 0)
            return FileResult.NoSpace;
        if (dir.FlushDirectory() != 0)
            return FileResult.IoError;

        if (!dir.ReadEntrySet(at, entry))
            return FileResult.IoError;
        return FileResult.Success;
    }

    /// <summary>Opens a directory for enumeration.</summary>
    /// <param name="path">Directory path.</param>
    /// <param name="handle">Receives the directory handle.</param>
    public FileResult OpenDirectory(string path, out IDirectoryHandle? handle)
    {
        handle = null;
        if (!_mounted)
            return FileResult.IoError;

        string norm = NormalizePath(path);
        ExFatDir dir;
        if (norm.Length == 0)
        {
            dir = new ExFatDir(_vol);
            if (!dir.LoadRootDirectory())
                return FileResult.IoError;
        }
        else
        {
            var entry = new ExFatDirEntry();
            int rc = Resolve(path, entry);
            if (rc < 0)
                return (FileResult)rc;
            if (!entry.IsDirectory)
                return FileResult.NotADirectory;
            dir = new ExFatDir(_vol);
            if (!dir.LoadDirectory(entry.FirstCluster, entry.NoFatChain, entry.DataLength))
                return FileResult.IoError;
        }

        handle = new ExFatDirectoryHandle(_vol, dir, norm);
        return FileResult.Success;
    }

    /// <summary>Creates a directory (one zeroed cluster, contiguous).</summary>
    /// <param name="path">Directory path.</param>
    public FileResult CreateDirectory(string path)
    {
        if (!_mounted)
            return FileResult.IoError;
        if (_readOnly)
            return FileResult.ReadOnly;

        string leaf;
        var dir = ResolveParent(path, out leaf);
        if (dir == null || leaf.Length == 0)
            return FileResult.InvalidPath;
        if (leaf.Length > ExFatConst.MaxNameChars)
            return FileResult.NameTooLong;

        var existing = new ExFatDirEntry();
        if (dir.FindEntry(leaf, existing) >= 0)
            return FileResult.AlreadyExists;

        uint cluster = _vol.AllocateCluster();
        if (cluster == 0)
            return FileResult.NoSpace;
        if (_vol.ZeroCluster(cluster) != 0)
        {
            _vol.FreeCluster(cluster);
            return FileResult.IoError;
        }

        uint now = ExFatTime.FromEpochSeconds(ExFatTime.NowEpoch());
        byte[] set = ExFatDir.BuildSet(_vol, leaf, ExFatConst.AttrDirectory, cluster,
            _vol.ClusterSize, _vol.ClusterSize, true, now, now, now);
        int at = dir.InsertSet(set);
        if (at < 0)
        {
            FreeChain(cluster, true);
            return FileResult.NoSpace;
        }
        if (dir.FlushDirectory() != 0)
            return FileResult.IoError;
        if (!_readOnly)
            _vol.FlushBitmap();
        return FileResult.Success;
    }

    /// <summary>Deletes a file (frees its cluster chain).</summary>
    /// <param name="path">File path.</param>
    public FileResult DeleteFile(string path)
    {
        if (!_mounted)
            return FileResult.IoError;
        if (_readOnly)
            return FileResult.ReadOnly;

        string leaf;
        var dir = ResolveParent(path, out leaf);
        if (dir == null || leaf.Length == 0)
            return FileResult.NotFound;

        var entry = new ExFatDirEntry();
        int index = dir.FindEntry(leaf, entry);
        if (index < 0)
            return FileResult.NotFound;
        if (entry.IsDirectory)
            return FileResult.IsADirectory;

        FreeEntryStream(entry);
        if (!dir.DeleteSet(index))
            return FileResult.IoError;
        if (dir.FlushDirectory() != 0)
            return FileResult.IoError;
        _vol.FlushBitmap();
        return FileResult.Success;
    }

    /// <summary>Deletes an empty directory.</summary>
    /// <param name="path">Directory path.</param>
    public FileResult DeleteDirectory(string path)
    {
        if (!_mounted)
            return FileResult.IoError;
        if (_readOnly)
            return FileResult.ReadOnly;

        string leaf;
        var dir = ResolveParent(path, out leaf);
        if (dir == null || leaf.Length == 0)
            return FileResult.NotFound;

        var entry = new ExFatDirEntry();
        int index = dir.FindEntry(leaf, entry);
        if (index < 0)
            return FileResult.NotFound;
        if (!entry.IsDirectory)
            return FileResult.NotADirectory;

        // Must be empty: scan the child directory for any live entry
        // (corrupt sets still count as content and block the delete).
        var child = new ExFatDir(_vol);
        if (entry.HasStream && !child.LoadDirectory(entry.FirstCluster, entry.NoFatChain, entry.DataLength))
            return FileResult.IoError;
        var probe = new ExFatDirEntry();
        if (child.NextEntry(0, probe) >= 0)
            return FileResult.NotEmpty;

        FreeEntryStream(entry);
        if (!dir.DeleteSet(index))
            return FileResult.IoError;
        if (dir.FlushDirectory() != 0)
            return FileResult.IoError;
        _vol.FlushBitmap();
        return FileResult.Success;
    }

    /// <summary>
    /// Renames/moves an entry within the volume: the set is rebuilt with
    /// the new name (checksum and hash recomputed) and re-inserted; the
    /// stream itself is untouched.
    /// </summary>
    /// <param name="oldPath">Existing path.</param>
    /// <param name="newPath">New path.</param>
    public FileResult Rename(string oldPath, string newPath)
    {
        if (!_mounted)
            return FileResult.IoError;
        if (_readOnly)
            return FileResult.ReadOnly;

        string oldLeaf;
        var oldDir = ResolveParent(oldPath, out oldLeaf);
        string newLeaf;
        var newDir = ResolveParent(newPath, out newLeaf);
        if (oldDir == null || newDir == null || oldLeaf.Length == 0 || newLeaf.Length == 0)
            return FileResult.NotFound;
        if (newLeaf.Length > ExFatConst.MaxNameChars)
            return FileResult.NameTooLong;

        var entry = new ExFatDirEntry();
        int index = oldDir.FindEntry(oldLeaf, entry);
        if (index < 0 || entry.Corrupt)
            return FileResult.NotFound;

        // Same location and name: nothing to do.
        if (SameDirectory(oldPath, newPath) && ExFatDir.NamesEqual(entry.Name, newLeaf))
            return FileResult.Success;
        if (entry.IsDirectory && IsWithinPath(newPath, oldPath))
            return FileResult.InvalidPath;

        var existing = new ExFatDirEntry();
        if (newDir.FindEntry(newLeaf, existing) >= 0)
            return FileResult.AlreadyExists;

        // Preserve the stream; only the name entry changes (Task 3).
        uint now = ExFatTime.FromEpochSeconds(ExFatTime.NowEpoch());
        byte[] set = ExFatDir.BuildSet(_vol, newLeaf, entry.Attributes, entry.FirstCluster,
            entry.ValidDataLength, entry.DataLength, entry.NoFatChain,
            entry.CreateTime, now, entry.AccessTime);

        bool sameDir = SameDirectory(oldPath, newPath);
        int at = newDir.InsertSet(set);
        if (at < 0)
            return FileResult.NoSpace;
        if (!newDir.DeleteSet(index))
        {
            newDir.DeleteSet(at);
            return FileResult.IoError;
        }
        // The two resolver objects hold independent copies of the same
        // directory; flushing both would undo the change, so when the
        // rename stays in one directory only the second object (which
        // has both edits) is written back.
        if (newDir.FlushDirectory() != 0)
            return FileResult.IoError;
        if (!sameDir && oldDir.FlushDirectory() != 0)
            return FileResult.IoError;
        return FileResult.Success;
    }

    /// <summary>
    /// True when both paths name entries in the same directory.
    /// </summary>
    /// <param name="pathA">First path.</param>
    /// <param name="pathB">Second path.</param>
    private static bool SameDirectory(string pathA, string pathB)
    {
        string pa = ParentPath(pathA);
        string pb = ParentPath(pathB);
        return ExFatDir.NamesEqual(pa, pb);
    }

    /// <summary>Normalized parent directory path ("" for the root).</summary>
    /// <param name="path">File path.</param>
    private static string ParentPath(string path)
    {
        string norm = NormalizePath(path);
        int slash = norm.LastIndexOf('/');
        if (slash < 0)
            return "";
        return norm.Substring(0, slash);
    }

    /// <summary>
    /// True when <paramref name="path"/> lies strictly under
    /// <paramref name="prefix"/> (component boundary aware).
    /// </summary>
    /// <param name="path">Path to test.</param>
    /// <param name="prefix">Ancestor path.</param>
    private static bool IsWithinPath(string path, string prefix)
    {
        string p = NormalizePath(path);
        string q = NormalizePath(prefix);
        if (q.Length == 0 || p.Length <= q.Length)
            return false;
        if (!StartsWithStr(p, q))
            return false;
        return p[q.Length] == '/';
    }

    /// <summary>True when the path exists.</summary>
    /// <param name="path">Path to check.</param>
    public bool Exists(string path)
    {
        if (!_mounted)
            return false;
        var entry = new ExFatDirEntry();
        return Resolve(path, entry) == 0;
    }

    /// <summary>
    /// Applies an attribute change to a file or directory: bits in
    /// <paramref name="clear"/> are removed, bits in
    /// <paramref name="set"/> are added, then the set checksum is
    /// recomputed and persisted (exfatattrib core).
    /// </summary>
    /// <param name="path">Path relative to the mount point.</param>
    /// <param name="clear">Attribute bits to clear.</param>
    /// <param name="set">Attribute bits to set.</param>
    public FileResult SetAttributes(string path, ushort clear, ushort set)
    {
        if (!_mounted)
            return FileResult.IoError;
        if (_readOnly)
            return FileResult.ReadOnly;

        string leaf;
        var dir = ResolveParent(path, out leaf);
        if (dir == null || leaf.Length == 0)
            return FileResult.NotFound;
        var entry = new ExFatDirEntry();
        int index = dir.FindEntry(leaf, entry);
        if (index < 0 || entry.Corrupt)
            return FileResult.NotFound;

        entry.Attributes = (ushort)((entry.Attributes & ~clear) | set);
        if (!dir.UpdateSetInPlace(entry))
            return FileResult.IoError;
        if (dir.FlushDirectory() != 0)
            return FileResult.IoError;
        return FileResult.Success;
    }

    /// <summary>
    /// Sets (or clears, with "") the volume label: updates the existing
    /// 0x83 Volume Label entry, creates one when missing, or marks the
    /// old entry unused when clearing (exfatlabel core).
    /// </summary>
    /// <param name="label">New label (max 11 UTF-16 chars).</param>
    public FileResult SetVolumeLabel(string label)
    {
        if (!_mounted)
            return FileResult.IoError;
        if (_readOnly)
            return FileResult.ReadOnly;
        if (label.Length > ExFatConst.MaxLabelChars)
            return FileResult.NameTooLong;

        var root = new ExFatDir(_vol);
        if (!root.LoadRootDirectory())
            return FileResult.IoError;
        byte[] probe = new byte[ExFatConst.EntrySize];
        int idx = root.FindPrimaryEntry(ExFatConst.EntryVolumeLabel, probe);

        if (idx >= 0)
        {
            int off = idx * ExFatConst.EntrySize;
            if (label.Length == 0)
            {
                // Clear: mark the entry unused (bit 7 cleared).
                root.Entries[off] = (byte)(root.Entries[off] & ExFatConst.EntryDeletedMask);
            }
            else
            {
                root.Entries[off + 1] = (byte)label.Length;
                for (int i = 0; i < ExFatConst.MaxLabelChars; i++)
                {
                    ushort ch = i < label.Length ? label[i] : (ushort)0;
                    ExFatBytes.Write16(root.Entries, off + 2 + i * 2, ch);
                }
            }
            if (root.FlushDirectory() != 0)
                return FileResult.IoError;
        }
        else if (label.Length > 0)
        {
            var entryBytes = new byte[ExFatConst.EntrySize];
            entryBytes[0] = ExFatConst.EntryVolumeLabel;
            entryBytes[1] = (byte)label.Length;
            for (int i = 0; i < label.Length; i++)
                ExFatBytes.Write16(entryBytes, 2 + i * 2, label[i]);
            if (root.InsertSet(entryBytes) < 0)
                return FileResult.NoSpace;
            if (root.FlushDirectory() != 0)
                return FileResult.IoError;
        }

        _vol.VolumeLabel = label;
        return FileResult.Success;
    }

    // ==================== helpers ====================

    /// <summary>Frees a stream's cluster chain (or contiguous run).</summary>
    /// <param name="entry">Entry whose stream is freed.</param>
    public void FreeEntryStream(ExFatDirEntry entry)
    {
        if (!entry.HasStream)
            return;
        uint clusters = (uint)((entry.DataLength + _vol.ClusterSize - 1) / _vol.ClusterSize);
        if (clusters == 0)
            clusters = 1;
        FreeChain(entry.FirstCluster, entry.NoFatChain, clusters);
    }

    /// <summary>Frees a chain/run of clusters.</summary>
    /// <param name="first">First cluster.</param>
    /// <param name="noFatChain">True for contiguous runs.</param>
    /// <param name="count">Known cluster count (0 to walk the FAT).</param>
    public void FreeChain(uint first, bool noFatChain, uint count = 0)
    {
        if (first < ExFatConst.FirstCluster)
            return;
        if (noFatChain && count > 0)
        {
            for (uint i = 0; i < count; i++)
                _vol.FreeCluster(first + i);
            return;
        }
        uint cluster = first;
        int guard = 0;
        while (cluster >= ExFatConst.FirstCluster)
        {
            uint next = _vol.FatNext(cluster);
            _vol.FreeCluster(cluster);
            if (next == 0)
                break;
            cluster = next;
            if (++guard > 0x100000)
                break;
        }
    }

    /// <summary>Truncates an entry's stream to a new length (internal).</summary>
    /// <param name="dir">Directory holding the entry.</param>
    /// <param name="entry">Entry to truncate.</param>
    /// <param name="newLength">New logical length.</param>
    public void TruncateEntry(ExFatDir dir, ExFatDirEntry entry, long newLength)
    {
        if (newLength == 0)
        {
            if (entry.HasStream)
            {
                FreeEntryStream(entry);
                entry.FirstCluster = 0;
            }
            entry.ValidDataLength = 0;
            entry.DataLength = 0;
            entry.NoFatChain = false;
        }
        dir.UpdateSetInPlace(entry);
        dir.FlushDirectory();
        _vol.FlushBitmap();
    }

    /// <summary>Logs an error with the cluster and sector of the offending structure.</summary>
    /// <param name="message">Message text.</param>
    /// <param name="cluster">Related cluster index (0 when none).</param>
    /// <param name="sector">Related sector (0 when none).</param>
    public void LogError(string message, uint cluster, ulong sector)
    {
        Console.Error.Write("[exfat] ");
        Console.Error.Write(message);
        if (cluster != 0 || sector != 0)
        {
            Console.Error.Write(" (cluster=");
            Console.Error.Write(cluster.ToString());
            Console.Error.Write(", sector=");
            Console.Error.Write(sector.ToString());
            Console.Error.Write(")");
        }
        Console.Error.WriteLine();
    }

    #endregion
}
