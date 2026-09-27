// NeutrinoOS Phase 10 - exFAT directory enumeration handle (Task 3).
//
// Enumerates a directory's entry sets through ExFatDir.NextEntry,
// translating each parsed set into a DDK FileInfo. Corrupt sets are
// reported once through the filesystem error log and skipped.

using System;

namespace ProtonOS.DDK.Storage.ExFat;

/// <summary>Directory enumeration over an in-memory exFAT directory.</summary>
public sealed class ExFatDirectoryHandle : IDirectoryHandle
{
    private readonly ExFatVolume _vol;
    private readonly ExFatDir _dir;
    private readonly string _dirPath;
    private int _cursor;
    private bool _open = true;

    /// <summary>Creates a handle over a loaded directory.</summary>
    /// <param name="volume">Volume.</param>
    /// <param name="dir">Loaded directory.</param>
    /// <param name="dirPath">Normalized directory path (mount-relative).</param>
    public ExFatDirectoryHandle(ExFatVolume volume, ExFatDir dir, string dirPath)
    {
        _vol = volume;
        _dir = dir;
        _dirPath = dirPath;
    }

    /// <summary>True while the handle is open.</summary>
    public bool IsOpen => _open;

    /// <summary>Releases the handle (no state is held).</summary>
    public void Dispose()
    {
        _open = false;
    }

    /// <summary>Restarts enumeration from the first entry.</summary>
    public void Rewind()
    {
        _cursor = 0;
    }

    /// <summary>
    /// Returns the next live entry, or null at the end of the directory.
    /// </summary>
    public FileInfo? ReadNext()
    {
        if (!_open)
            return null;

        var entry = new ExFatDirEntry();
        while (true)
        {
            int index = _dir.NextEntry(_cursor, entry);
            if (index < 0)
                return null;
            _cursor = index + entry.TotalEntries;

            if (entry.Corrupt)
            {
                LogCorrupt(index);
                continue;
            }
            if (entry.Name.Length == 0)
                continue;
            return ToFileInfo(entry);
        }
    }

    /// <summary>Builds a FileInfo for one entry set.</summary>
    private FileInfo ToFileInfo(ExFatDirEntry entry)
    {
        var info = new FileInfo();
        info.Name = entry.Name;
        info.Path = CombinePath(_dirPath, entry.Name);
        info.Type = entry.ToEntryType();
        info.Size = entry.IsDirectory ? 0 : (ulong)entry.ValidDataLength;
        info.Attributes = entry.ToFileAttributes();
        info.CreationTime = ExFatTime.ToEpochSeconds(entry.CreateTime);
        info.ModificationTime = ExFatTime.ToEpochSeconds(entry.ModifyTime);
        info.AccessTime = ExFatTime.ToEpochSeconds(entry.AccessTime);
        return info;
    }

    /// <summary>Joins a directory path and a name (JIT-safe).</summary>
    private static string CombinePath(string dirPath, string name)
    {
        if (dirPath.Length == 0)
            return "/" + name;
        return dirPath + "/" + name;
    }

    /// <summary>Reports a corrupt entry set once.</summary>
    private void LogCorrupt(int index)
    {
        ulong sector = _vol.ClusterToSector(_dir.FirstCluster) +
            (ulong)((index * ExFatConst.EntrySize) / (int)_vol.SectorSize);
        Console.Error.Write("[exfat] corrupt directory entry set at index ");
        Console.Error.Write(index.ToString());
        Console.Error.Write(" (cluster=");
        Console.Error.Write(_dir.FirstCluster.ToString());
        Console.Error.Write(", sector=");
        Console.Error.Write(sector.ToString());
        Console.Error.WriteLine("), skipping");
    }
}
