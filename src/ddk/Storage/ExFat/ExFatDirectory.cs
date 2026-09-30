// NeutrinoOS Phase 10 - exFAT directory entry parser (Task 2).
//
// Implements entry set parsing and construction: File Directory Entry
// (0x85), Stream Extension (0xC0), File Name (0xC1) sets, the full entry
// type dispatch, SetChecksum and NameHash validation, case-insensitive
// name comparison through the volume up-case table, and the directory
// array operations (find, insert, delete) that the driver needs.

using System;

namespace NeutrinoOS.DDK.Storage.ExFat;

/// <summary>
/// A parsed exFAT file/directory entry set (one File Directory Entry
/// plus its secondary entries).
/// </summary>
public sealed class ExFatDirEntry
{
    /// <summary>Index of the File (0x85) entry within its directory.</summary>
    public int EntryIndex;

    /// <summary>SecondaryCount from the File entry (name entries + 1 stream).</summary>
    public int SecondaryCount;

    /// <summary>FileAttributes bits (see ExFatConst.Attr*).</summary>
    public ushort Attributes;

    /// <summary>Stream first cluster (0 for empty files/dirs).</summary>
    public uint FirstCluster;

    /// <summary>Stream DataLength (allocated bytes).</summary>
    public long DataLength;

    /// <summary>Stream ValidDataLength (logical file size in bytes).</summary>
    public long ValidDataLength;

    /// <summary>True when the stream clusters are contiguous (NoFatChain).</summary>
    public bool NoFatChain;

    /// <summary>GeneralSecondaryFlags of the stream extension.</summary>
    public byte StreamFlags;

    /// <summary>File name (UTF-16 code units).</summary>
    public string Name = "";

    /// <summary>NameHash stored in the stream extension.</summary>
    public ushort NameHash;

    /// <summary>CreateTimestamp (32-bit packed).</summary>
    public uint CreateTime;

    /// <summary>LastModifiedTimestamp (32-bit packed).</summary>
    public uint ModifyTime;

    /// <summary>LastAccessedTimestamp (32-bit packed).</summary>
    public uint AccessTime;

    /// <summary>Create10msIncrement.</summary>
    public byte Create10ms;

    /// <summary>LastModified10msIncrement.</summary>
    public byte Modify10ms;

    /// <summary>CreateUtcOffset.</summary>
    public byte CreateUtcOffset;

    /// <summary>LastModifiedUtcOffset.</summary>
    public byte ModifyUtcOffset;

    /// <summary>LastAccessedUtcOffset.</summary>
    public byte AccessUtcOffset;

    /// <summary>True when the entry set failed checksum or hash validation.</summary>
    public bool Corrupt;

    /// <summary>Total entries occupied by the set (1 + SecondaryCount).</summary>
    public int TotalEntries => 1 + SecondaryCount;

    /// <summary>True when the Directory attribute bit is set.</summary>
    public bool IsDirectory => (Attributes & ExFatConst.AttrDirectory) != 0;

    /// <summary>True when the stream is non-empty.</summary>
    public bool HasStream => FirstCluster >= ExFatConst.FirstCluster;

    /// <summary>Converts the attribute bits to the DDK FileAttributes flags.</summary>
    public FileAttributes ToFileAttributes()
    {
        FileAttributes a = FileAttributes.None;
        if ((Attributes & ExFatConst.AttrReadOnly) != 0) a |= FileAttributes.ReadOnly;
        if ((Attributes & ExFatConst.AttrHidden) != 0) a |= FileAttributes.Hidden;
        if ((Attributes & ExFatConst.AttrSystem) != 0) a |= FileAttributes.System;
        if ((Attributes & ExFatConst.AttrArchive) != 0) a |= FileAttributes.Archive;
        return a;
    }

    /// <summary>The entry type (File or Directory) as a DDK FileEntryType.</summary>
    public FileEntryType ToEntryType() =>
        IsDirectory ? FileEntryType.Directory : FileEntryType.File;
}

/// <summary>
/// An in-memory exFAT directory: the full 32-byte entry array plus the
/// operations the driver needs (load, enumerate, find, insert, delete).
/// The array is written back to the cluster chain (or contiguous stream)
/// on flush.
/// </summary>
public unsafe class ExFatDir
{
    private readonly ExFatVolume _vol;

    /// <summary>Raw directory entries (32 bytes each).</summary>
    public byte[] Entries = new byte[0];

    /// <summary>Capacity of <see cref="Entries"/> in entries.</summary>
    public int CapacityEntries;

    /// <summary>First cluster of the directory (root cluster when root).</summary>
    public uint FirstCluster;

    /// <summary>True when the directory is a contiguous stream.</summary>
    public bool NoFatChain;

    /// <summary>Stream DataLength from the directory's own entry set (subdirs only).</summary>
    public long DataLength;

    /// <summary>Stream ValidDataLength (subdirs only).</summary>
    public long ValidDataLength;

    /// <summary>True when this is the root directory (FAT-chained, no entry set).</summary>
    public bool IsRoot;

    /// <summary>Set when the directory grew and its entry set needs updating.</summary>
    public bool Grew;

    /// <summary>The directory's own entry set (null for the root directory).</summary>
    public ExFatDirEntry? SelfEntry;

    /// <summary>The directory that contains this directory (null for the root).</summary>
    public ExFatDir? SelfParent;

    /// <summary>Creates a directory view over a volume.</summary>
    /// <param name="volume">Owning volume.</param>
    public ExFatDir(ExFatVolume volume)
    {
        _vol = volume;
    }

    /// <summary>
    /// Loads the root directory: the cluster chain starting at
    /// FirstClusterOfRootDirectory, followed through the FAT (the root
    /// directory has no NoFatChain flag, spec 3.3), stopping once an End
    /// of Directory entry has been seen in a cluster and the chain ends.
    /// </summary>
    public bool LoadRootDirectory()
    {
        IsRoot = true;
        FirstCluster = _vol.RootDirCluster;
        NoFatChain = false;

        int perCluster = (int)(_vol.ClusterSize / ExFatConst.EntrySize);
        Entries = new byte[perCluster * ExFatConst.EntrySize];
        CapacityEntries = perCluster;

        uint cluster = FirstCluster;
        int entries = 0;
        int guard = 0;
        while (cluster != 0 && cluster >= ExFatConst.FirstCluster &&
               cluster < ExFatConst.FirstCluster + _vol.ClusterCount)
        {
            if (entries + perCluster > CapacityEntries)
            {
                int newCap = CapacityEntries + perCluster;
                var bigger = new byte[newCap * ExFatConst.EntrySize];
                for (int i = 0; i < entries * ExFatConst.EntrySize; i++)
                    bigger[i] = Entries[i];
                Entries = bigger;
                CapacityEntries = newCap;
            }

            // Read the cluster's entries in bounded windows directly into
            // the entry array (safe for clusters larger than the shared
            // scratch buffer).
            int clusterBytes = perCluster * ExFatConst.EntrySize;
            int win = 0;
            while (win < clusterBytes)
            {
                int len = clusterBytes - win;
                if (len > 16 * 1024)
                    len = 16 * 1024;
                int rc = _vol.ReadClusterWindow(cluster, win, len, Entries,
                    entries * ExFatConst.EntrySize + win);
                if (rc != 0)
                    return false;
                win += len;
            }
            entries += perCluster;

            uint next = _vol.FatNext(cluster);
            if (next == 0)
                break;
            cluster = next;
            if (++guard > 0x10000)
                return false;
        }

        DataLength = (long)entries * ExFatConst.EntrySize;
        ValidDataLength = DataLength;
        return entries > 0;
    }

    /// <summary>
    /// Loads a subdirectory: exactly DataLength bytes from the stream.
    /// </summary>
    /// <param name="firstCluster">Stream first cluster.</param>
    /// <param name="noFatChain">Contiguous stream flag.</param>
    /// <param name="dataLength">Stream DataLength (bytes).</param>
    public bool LoadDirectory(uint firstCluster, bool noFatChain, long dataLength)
    {
        IsRoot = false;
        FirstCluster = firstCluster;
        NoFatChain = noFatChain;

        if (dataLength <= 0)
            dataLength = _vol.ClusterSize;
        int bytes = (int)dataLength;
        if (bytes % ExFatConst.EntrySize != 0)
            bytes = ((bytes / ExFatConst.EntrySize) + 1) * ExFatConst.EntrySize;

        Entries = new byte[bytes];
        CapacityEntries = bytes / ExFatConst.EntrySize;
        DataLength = dataLength;
        ValidDataLength = dataLength;

        if (firstCluster < ExFatConst.FirstCluster)
            return false;
        return _vol.DirReadStream(firstCluster, noFatChain, Entries, 0, bytes) == 0;
    }

    /// <summary>
    /// Finds the first primary entry with the given type byte and copies
    /// it into <paramref name="out32"/>. Used for the bitmap (0x81),
    /// up-case (0x82), and label (0x83) critical primaries.
    /// </summary>
    /// <param name="type">Entry type byte to find.</param>
    /// <param name="out32">Receives the 32-byte entry.</param>
    /// <returns>Entry index, or -1.</returns>
    public int FindPrimaryEntry(byte type, byte[] out32)
    {
        int count = CapacityEntries;
        for (int i = 0; i < count; i++)
        {
            int off = i * ExFatConst.EntrySize;
            byte t = Entries[off];
            if (t == ExFatConst.EntryEnd)
                return -1;
            if (t == type)
            {
                for (int b = 0; b < ExFatConst.EntrySize; b++)
                    out32[b] = Entries[off + b];
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Parses a complete entry set starting at <paramref name="index"/>.
    /// Validates the SetChecksum and NameHash; on failure the returned
    /// entry has <see cref="ExFatDirEntry.Corrupt"/> set.
    /// </summary>
    /// <param name="index">Index of the File (0x85) entry.</param>
    /// <param name="entry">Receives the parsed entry set.</param>
    /// <returns>False when the entry is not a File entry set.</returns>
    public bool ReadEntrySet(int index, ExFatDirEntry entry)
    {
        int count = CapacityEntries;
        if (index < 0 || index >= count)
            return false;
        int off = index * ExFatConst.EntrySize;
        if (Entries[off] != ExFatConst.EntryFile)
            return false;

        entry.EntryIndex = index;
        entry.Corrupt = false;
        entry.SecondaryCount = Entries[off + 1];
        ushort setChecksum = ExFatBytes.Read16(Entries, off + 2);
        entry.Attributes = ExFatBytes.Read16(Entries, off + 4);
        entry.CreateTime = ExFatBytes.Read32(Entries, off + 8);
        entry.ModifyTime = ExFatBytes.Read32(Entries, off + 12);
        entry.AccessTime = ExFatBytes.Read32(Entries, off + 16);
        entry.Create10ms = Entries[off + 20];
        entry.Modify10ms = Entries[off + 21];
        entry.CreateUtcOffset = Entries[off + 22];
        entry.ModifyUtcOffset = Entries[off + 23];
        entry.AccessUtcOffset = Entries[off + 24];

        if (index + 1 + entry.SecondaryCount > count)
        {
            entry.Corrupt = true;
            return true;
        }

        // Stream extension must follow directly and be named 0xC0.
        int streamOff = off + ExFatConst.EntrySize;
        if (Entries[streamOff] != ExFatConst.EntryStreamExtension)
        {
            entry.Corrupt = true;
            return true;
        }
        entry.StreamFlags = Entries[streamOff + 1];
        int nameLength = Entries[streamOff + 3];
        entry.NameHash = ExFatBytes.Read16(Entries, streamOff + 4);
        entry.ValidDataLength = (long)ExFatBytes.Read64(Entries, streamOff + 8);
        entry.FirstCluster = ExFatBytes.Read32(Entries, streamOff + 20);
        entry.DataLength = (long)ExFatBytes.Read64(Entries, streamOff + 24);
        entry.NoFatChain = (entry.StreamFlags & ExFatConst.StreamFlagNoFatChain) != 0;

        // Set checksum over all entries (skipping the checksum field).
        fixed (byte* p = Entries)
        {
            ushort computed = ExFatChecksum.ComputeSetChecksum(
                p + off, entry.TotalEntries);
            if (computed != setChecksum)
                entry.Corrupt = true;
        }

        // Name from the File Name entries (15 chars each).
        if (nameLength < 1 || nameLength > ExFatConst.MaxNameChars)
        {
            entry.Corrupt = true;
            return true;
        }
        var chars = new char[nameLength];
        int used = 0;
        for (int s = 1; s <= entry.SecondaryCount && used < nameLength; s++)
        {
            int nameOff = off + (1 + s) * ExFatConst.EntrySize;
            if (Entries[nameOff] != ExFatConst.EntryFileName)
                break;
            for (int c = 0; c < 15 && used < nameLength; c++)
            {
                ushort ch = ExFatBytes.Read16(Entries, nameOff + 2 + c * 2);
                if (ch == 0)
                {
                    entry.Corrupt = true;
                    break;
                }
                chars[used++] = (char)ch;
            }
        }
        if (used != nameLength)
            entry.Corrupt = true;
        entry.Name = used == nameLength ? new string(chars) : new string(chars, 0, used);

        // Name hash validation through the up-case table.
        if (!entry.Corrupt)
        {
            string upcased = _vol.UpcaseName(entry.Name);
            ushort hash = ExFatChecksum.ComputeNameHash(upcased);
            if (hash != entry.NameHash)
                entry.Corrupt = true;
        }
        return true;
    }

    /// <summary>
    /// Finds a file/directory entry set by name (case-insensitive via the
    /// up-case table, spec 6.3.5). Returns the entry index or -1.
    /// </summary>
    /// <param name="name">Name to look up.</param>
    /// <param name="entry">Receives the found entry set.</param>
    public int FindEntry(string name, ExFatDirEntry entry)
    {
        string wanted = _vol.UpcaseName(name);
        ushort wantedHash = ExFatChecksum.ComputeNameHash(wanted);

        int count = CapacityEntries;
        int i = 0;
        while (i < count)
        {
            int off = i * ExFatConst.EntrySize;
            byte t = Entries[off];
            if (t == ExFatConst.EntryEnd)
                return -1;
            if (t != ExFatConst.EntryFile)
            {
                i++;
                continue;
            }

            int secondaries = Entries[off + 1];
            int streamOff = off + ExFatConst.EntrySize;
            if (streamOff + 6 < Entries.Length &&
                Entries[streamOff] == ExFatConst.EntryStreamExtension)
            {
                ushort hash = ExFatBytes.Read16(Entries, streamOff + 4);
                if (hash == wantedHash && ReadEntrySet(i, entry))
                {
                    if (!entry.Corrupt && NamesEqual(_vol.UpcaseName(entry.Name), wanted))
                        return i;
                }
            }
            i += 1 + secondaries;
        }
        return -1;
    }

    /// <summary>
    /// Recomputes a corrupted entry set's NameHash and SetChecksum from
    /// its stored fields (fsck repair). The name entries must decode.
    /// </summary>
    /// <param name="index">Index of the File (0x85) entry.</param>
    public bool RepairSet(int index)
    {
        if (index < 0 || index >= CapacityEntries)
            return false;
        int off = index * ExFatConst.EntrySize;
        if (Entries[off] != ExFatConst.EntryFile)
            return false;
        int secondaries = Entries[off + 1];
        if (index + 1 + secondaries > CapacityEntries)
            return false;
        int streamOff = off + ExFatConst.EntrySize;
        if (Entries[streamOff] != ExFatConst.EntryStreamExtension)
            return false;

        int nameLength = Entries[streamOff + 3];
        if (nameLength < 1 || nameLength > ExFatConst.MaxNameChars)
            return false;
        var chars = new char[nameLength];
        int used = 0;
        for (int s = 1; s <= secondaries && used < nameLength; s++)
        {
            int nameOff = off + (1 + s) * ExFatConst.EntrySize;
            if (Entries[nameOff] != ExFatConst.EntryFileName)
                return false;
            for (int c = 0; c < 15 && used < nameLength; c++)
            {
                ushort ch = ExFatBytes.Read16(Entries, nameOff + 2 + c * 2);
                if (ch == 0)
                    return false;
                chars[used++] = (char)ch;
            }
        }
        if (used != nameLength)
            return false;

        string name = new string(chars);
        ushort hash = ExFatChecksum.ComputeNameHash(_vol.UpcaseName(name));
        ExFatBytes.Write16(Entries, streamOff + 4, hash);
        fixed (byte* p = Entries)
        {
            ushort checksum = ExFatChecksum.ComputeSetChecksum(p + off, 1 + secondaries);
            ExFatBytes.Write16(Entries, off + 2, checksum);
        }
        return true;
    }

    /// <summary>
    /// Char-wise string equality (the Tier-0 JIT is not trusted with
    /// string op_Equality in hot paths; same idiom as the FAT driver).
    /// </summary>
    /// <param name="a">First string.</param>
    /// <param name="b">Second string.</param>
    public static bool NamesEqual(string a, string b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }

    /// <summary>
    /// Enumerates entry sets: returns the next File entry index after
    /// <paramref name="fromIndex"/> (or -1). Deleted/invalid sets are
    /// skipped; the caller uses <see cref="ReadEntrySet"/> for details.
    /// </summary>
    /// <param name="fromIndex">Index to scan from.</param>
    /// <param name="entry">Receives the next parsed entry set.</param>
    public int NextEntry(int fromIndex, ExFatDirEntry entry)
    {
        int count = CapacityEntries;
        int i = fromIndex;
        while (i < count)
        {
            int off = i * ExFatConst.EntrySize;
            byte t = Entries[off];
            if (t == ExFatConst.EntryEnd)
                return -1;
            if (t != ExFatConst.EntryFile)
            {
                i++;
                continue;
            }
            int secondaries = Entries[off + 1];
            if (ReadEntrySet(i, entry))
                return i;
            i += 1 + secondaries;
        }
        return -1;
    }

    /// <summary>
    /// Appends a complete entry set (built by <see cref="BuildSet"/>) to
    /// the first run of free entries, growing the directory by one
    /// cluster when the array runs out. Maintains the End of Directory
    /// marker. Returns the entry index or -1.
    /// </summary>
    /// <param name="setData">Entry set bytes (multiple of 32).</param>
    public int InsertSet(byte[] setData)
    {
        int wanted = setData.Length / ExFatConst.EntrySize;
        int at = FindFreeRun(wanted);
        if (at < 0)
        {
            if (!GrowByOneCluster())
                return -1;
            at = FindFreeRun(wanted);
            if (at < 0)
                return -1;
        }

        for (int i = 0; i < setData.Length; i++)
            Entries[at * ExFatConst.EntrySize + i] = setData[i];

        // Keep exactly one End of Directory marker after the last used
        // entry when the following slot is free (spec 6.1): clearing a
        // deleted/unused slot is safe, leaving used entries untouched.
        int after = at + wanted;
        if (after < CapacityEntries && (Entries[after * ExFatConst.EntrySize] & 0x80) == 0)
            Entries[after * ExFatConst.EntrySize] = ExFatConst.EntryEnd;
        PersistSelf();
        return at;
    }

    /// <summary>
    /// Writes this directory's own stream state (FirstCluster, flags,
    /// DataLength) back through its parent's entry set after a growth;
    /// no-op for the root directory.
    /// </summary>
    public int PersistSelf()
    {
        if (!Grew || SelfEntry == null || SelfParent == null)
            return 0;
        SelfEntry.FirstCluster = FirstCluster;
        SelfEntry.NoFatChain = NoFatChain;
        SelfEntry.DataLength = DataLength;
        SelfEntry.ValidDataLength = DataLength;
        if (!SelfParent.UpdateSetInPlace(SelfEntry))
            return -7;
        Grew = false;
        return SelfParent.FlushDirectory();
    }

    /// <summary>
    /// Finds the first run of <paramref name="wanted"/> free entries.
    /// A slot is free when its type byte has bit 7 clear (deleted/unused)
    /// or it is the End marker (everything after is free too).
    /// </summary>
    /// <param name="wanted">Consecutive entries needed.</param>
    public int FindFreeRun(int wanted)
    {
        int count = CapacityEntries;
        int run = 0;
        int runStart = 0;
        for (int i = 0; i < count; i++)
        {
            byte t = Entries[i * ExFatConst.EntrySize];
            bool free = (t & 0x80) == 0;   // deleted (0x05,0x40,0x41...) or 0x00 End
            if (free)
            {
                if (run == 0)
                    runStart = i;
                run++;
                if (run >= wanted)
                    return runStart;
                if (t == ExFatConst.EntryEnd)
                {
                    // Everything past the End marker is free as well, but
                    // only when the array itself has the space.
                    if (count - runStart >= wanted)
                        return runStart;
                    return -1;   // caller grows the directory
                }
            }
            else
            {
                run = 0;
            }
        }
        return -1;
    }

    /// <summary>Marks an entry set deleted by clearing bit 7 of each entry type.</summary>
    /// <param name="index">Index of the File (0x85) entry.</param>
    public bool DeleteSet(int index)
    {
        if (index < 0 || index >= CapacityEntries)
            return false;
        int off = index * ExFatConst.EntrySize;
        if (Entries[off] != ExFatConst.EntryFile)
            return false;
        int secondaries = Entries[off + 1];
        for (int s = 0; s <= secondaries; s++)
        {
            int e = off + s * ExFatConst.EntrySize;
            Entries[e] = (byte)(Entries[e] & ExFatConst.EntryDeletedMask);
        }
        return true;
    }

    /// <summary>
    /// Grows the directory by one cluster (allocated, zeroed, chained to
    /// the end of the current stream when FAT-chained).
    /// </summary>
    public bool GrowByOneCluster()
    {
        int perCluster = (int)(_vol.ClusterSize / ExFatConst.EntrySize);
        uint clusters = (uint)(CapacityEntries / perCluster);
        uint cluster;

        if (NoFatChain)
        {
            // Contiguous growth keeps the NoFatChain flag valid; when
            // the next cluster is unavailable, convert the existing run
            // to a FAT chain first (mirrors the file growth fallback).
            uint target = FirstCluster + clusters;
            if (target < ExFatConst.FirstCluster + _vol.ClusterCount && !_vol.BitmapIsSet(target))
            {
                _vol.BitmapSet(target, true);
                cluster = target;
            }
            else
            {
                cluster = _vol.AllocateCluster();
                if (cluster == 0)
                    return false;
                if (!ConvertRunToChain(clusters))
                {
                    _vol.FreeCluster(cluster);
                    return false;
                }
                NoFatChain = false;
                _vol.FatSet(FirstCluster + clusters - 1, cluster);
            }
        }
        else
        {
            cluster = _vol.AllocateCluster();
            if (cluster == 0)
                return false;
            // Link to the end of the chain (root directory is chained).
            uint last = FirstCluster;
            uint next = _vol.FatNext(last);
            int guard = 0;
            while (next != 0)
            {
                last = next;
                next = _vol.FatNext(last);
                if (++guard > 0x100000)
                {
                    _vol.FreeCluster(cluster);
                    return false;
                }
            }
            _vol.FatSet(last, cluster);
        }

        // Zero the new cluster.
        if (_vol.ZeroCluster(cluster) != 0)
        {
            _vol.FreeCluster(cluster);
            return false;
        }

        // Extend the in-memory array by one cluster.
        int newCap = CapacityEntries + perCluster;
        var bigger = new byte[newCap * ExFatConst.EntrySize];
        for (int i = 0; i < CapacityEntries * ExFatConst.EntrySize; i++)
            bigger[i] = Entries[i];
        Entries = bigger;
        CapacityEntries = newCap;
        DataLength += _vol.ClusterSize;
        ValidDataLength += _vol.ClusterSize;
        Grew = true;
        return true;
    }

    /// <summary>Links a contiguous run into the FAT (NoFatChain → chained).</summary>
    private bool ConvertRunToChain(uint clusters)
    {
        for (uint i = 0; i + 1 < clusters; i++)
        {
            if (_vol.FatSet(FirstCluster + i, FirstCluster + i + 1) != 0)
                return false;
        }
        return true;
    }

    /// <summary>Writes the entry array back to its clusters.</summary>
    public int FlushDirectory()
    {
        if (_vol.ReadOnly)
            return 0;
        int bytes = CapacityEntries * ExFatConst.EntrySize;
        return _vol.DirWriteStream(FirstCluster, NoFatChain, Entries, 0, bytes);
    }

    /// <summary>
    /// Writes updated entry-set fields (attributes, timestamps and the
    /// stream extension: flags, ValidDataLength, FirstCluster,
    /// DataLength) back into the in-memory array, recomputing the set
    /// checksum in place (name length and name unchanged).
    /// </summary>
    /// <param name="entry">Entry set with updated fields.</param>
    public bool UpdateSetInPlace(ExFatDirEntry entry)
    {
        int index = entry.EntryIndex;
        if (index < 0 || index >= CapacityEntries)
            return false;
        int off = index * ExFatConst.EntrySize;
        if (Entries[off] != ExFatConst.EntryFile)
            return false;

        ExFatBytes.Write16(Entries, off + 4, entry.Attributes);
        ExFatBytes.Write32(Entries, off + 8, entry.CreateTime);
        ExFatBytes.Write32(Entries, off + 12, entry.ModifyTime);
        ExFatBytes.Write32(Entries, off + 16, entry.AccessTime);
        Entries[off + 20] = entry.Create10ms;
        Entries[off + 21] = entry.Modify10ms;
        Entries[off + 22] = entry.CreateUtcOffset;
        Entries[off + 23] = entry.ModifyUtcOffset;
        Entries[off + 24] = entry.AccessUtcOffset;

        // Stream Extension (0xC0) directly follows the File entry.
        int streamOff = off + ExFatConst.EntrySize;
        if (Entries[streamOff] == ExFatConst.EntryStreamExtension)
        {
            byte flags = ExFatConst.StreamFlagAllocationPossible;
            if (entry.NoFatChain)
                flags |= ExFatConst.StreamFlagNoFatChain;
            Entries[streamOff + 1] = flags;
            ExFatBytes.Write64(Entries, streamOff + 8, (ulong)entry.ValidDataLength);
            ExFatBytes.Write32(Entries, streamOff + 20, entry.FirstCluster);
            ExFatBytes.Write64(Entries, streamOff + 24, (ulong)entry.DataLength);
        }

        fixed (byte* p = Entries)
        {
            ushort checksum = ExFatChecksum.ComputeSetChecksum(p + off, entry.TotalEntries);
            ExFatBytes.Write16(Entries, off + 2, checksum);
        }
        return true;
    }

    // ==================== set construction ====================

    /// <summary>
    /// Builds a complete entry set (0x85 + 0xC0 + 0xC1[...]) for a file
    /// or directory with the given name and stream state, computing the
    /// SetChecksum and NameHash.
    /// </summary>
    /// <param name="volume">Volume (for the up-case table).</param>
    /// <param name="name">File name (1-255 chars).</param>
    /// <param name="attributes">File attribute bits.</param>
    /// <param name="firstCluster">Stream first cluster (0 for empty).</param>
    /// <param name="validDataLength">Logical size in bytes.</param>
    /// <param name="dataLength">Allocated size in bytes.</param>
    /// <param name="noFatChain">True for contiguous streams.</param>
    /// <param name="createTime">Packed create timestamp.</param>
    /// <param name="modifyTime">Packed modify timestamp.</param>
    /// <param name="accessTime">Packed access timestamp.</param>
    public static byte[] BuildSet(ExFatVolume volume, string name, ushort attributes,
        uint firstCluster, long validDataLength, long dataLength, bool noFatChain,
        uint createTime, uint modifyTime, uint accessTime)
    {
        int nameEntries = (name.Length + 14) / 15;
        int total = 1 + 1 + nameEntries;
        var set = new byte[total * ExFatConst.EntrySize];

        // File Directory Entry (0x85).
        set[0] = ExFatConst.EntryFile;
        set[1] = (byte)(1 + nameEntries);          // SecondaryCount
        // bytes 2-3: SetChecksum (computed below)
        ExFatBytes.Write16(set, 4, attributes);
        ExFatBytes.Write32(set, 8, createTime);
        ExFatBytes.Write32(set, 12, modifyTime);
        ExFatBytes.Write32(set, 16, accessTime);
        set[20] = 0;   // Create10msIncrement
        set[21] = 0;   // LastModified10msIncrement
        set[22] = 0;   // CreateUtcOffset
        set[23] = 0;   // LastModifiedUtcOffset
        set[24] = 0;   // LastAccessedUtcOffset

        // Stream Extension (0xC0).
        int streamOff = ExFatConst.EntrySize;
        set[streamOff] = ExFatConst.EntryStreamExtension;
        byte flags = ExFatConst.StreamFlagAllocationPossible;
        if (noFatChain)
            flags |= ExFatConst.StreamFlagNoFatChain;
        set[streamOff + 1] = flags;
        set[streamOff + 3] = (byte)name.Length;
        string upcased = volume.UpcaseName(name);
        ExFatBytes.Write16(set, streamOff + 4, ExFatChecksum.ComputeNameHash(upcased));
        ExFatBytes.Write64(set, streamOff + 8, (ulong)validDataLength);
        ExFatBytes.Write32(set, streamOff + 20, firstCluster);
        ExFatBytes.Write64(set, streamOff + 24, (ulong)dataLength);

        // File Name entries (0xC1), 15 UTF-16 units each.
        for (int n = 0; n < nameEntries; n++)
        {
            int off = (2 + n) * ExFatConst.EntrySize;
            set[off] = ExFatConst.EntryFileName;
            for (int c = 0; c < 15; c++)
            {
                int idx = n * 15 + c;
                ushort ch = idx < name.Length ? (ushort)name[idx] : (ushort)0;
                ExFatBytes.Write16(set, off + 2 + c * 2, ch);
            }
        }

        // SetChecksum over all entries.
        fixed (byte* p = set)
        {
            ushort checksum = ExFatChecksum.ComputeSetChecksum(p, total);
            ExFatBytes.Write16(set, 2, checksum);
        }
        return set;
    }

    /// <summary>
    /// Rebuilds an entry set into <paramref name="setData"/> for an
    /// existing parsed entry, recomputing the checksum — used by rename
    /// (new name) and by attribute updates.
    /// </summary>
    /// <param name="volume">Volume (for the up-case table).</param>
    /// <param name="entry">Parsed entry (name/stream/attributes).</param>
    public static byte[] RebuildSet(ExFatVolume volume, ExFatDirEntry entry)
    {
        var set = BuildSet(volume, entry.Name, entry.Attributes, entry.FirstCluster,
            entry.ValidDataLength, entry.DataLength, entry.NoFatChain,
            entry.CreateTime, entry.ModifyTime, entry.AccessTime);
        // Preserve the 10ms increments and UTC offsets of the original set.
        set[20] = entry.Create10ms;
        set[21] = entry.Modify10ms;
        set[22] = entry.CreateUtcOffset;
        set[23] = entry.ModifyUtcOffset;
        set[24] = entry.AccessUtcOffset;
        fixed (byte* p = set)
        {
            ushort checksum = ExFatChecksum.ComputeSetChecksum(p, set.Length / ExFatConst.EntrySize);
            ExFatBytes.Write16(set, 2, checksum);
        }
        return set;
    }
}
