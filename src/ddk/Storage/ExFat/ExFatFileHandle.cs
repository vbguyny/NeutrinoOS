// NeutrinoOS Phase 10 - exFAT file handle (Task 3 read/write paths).
//
// Read and write the data of one file: cluster mapping (FAT chain or
// contiguous NoFatChain streams), allocation on growth with the
// contiguous-to-chained fallback demanded by the spec, ValidDataLength
// and DataLength maintenance, sparse (hole) reads, sector-bounced I/O
// through the volume scratch buffer, and timestamp updates on write.

using System;

namespace NeutrinoOS.DDK.Storage.ExFat;

/// <summary>An open exFAT file.</summary>
public unsafe class ExFatFileHandle : IFileHandle
{
    private readonly ExFatFileSystem _fs;
    private readonly ExFatVolume _vol;
    private readonly ExFatDir _dir;
    private readonly ExFatDirEntry _entry;

    private long _position;
    private readonly FileAccess _access;
    private bool _open;
    private bool _dirty;

    // Allocated size in bytes (cluster multiples). The directory entry's
    // DataLength field carries the exact file size (= ValidDataLength),
    // per the spec; allocation is at least AlignUp(size) and may be
    // larger after truncations of previously bigger files.
    private long _allocLength;

    // Cluster-walk cache: (index, cluster) of the last mapping.
    private long _cacheIndex = -1;
    private uint _cacheCluster;

    /// <summary>Creates a handle over a parsed directory entry set.</summary>
    /// <param name="fs">Owning filesystem.</param>
    /// <param name="volume">Volume.</param>
    /// <param name="dir">Directory that holds the entry.</param>
    /// <param name="entry">The parsed entry set.</param>
    public ExFatFileHandle(ExFatFileSystem fs, ExFatVolume volume, ExFatDir dir, ExFatDirEntry entry)
    {
        _fs = fs;
        _vol = volume;
        _dir = dir;
        _entry = entry;
        _access = FileAccess.ReadWrite;
        _open = true;

        // Allocation reconstruction: the entry's DataLength is the exact
        // size in the current format, but older volumes stored the
        // cluster-rounded allocation there - take the max so both work.
        long size = (long)entry.ValidDataLength;
        long stored = (long)entry.DataLength;
        long basis = stored > size ? stored : size;
        _allocLength = ((basis + _vol.ClusterSize - 1) / _vol.ClusterSize) * _vol.ClusterSize;
    }

    /// <summary>Marks the handle open and optionally seeks to the end.</summary>
    /// <param name="seekEnd">True for append mode.</param>
    public void Opened(bool seekEnd)
    {
        _open = true;
        if (seekEnd)
            _position = _entry.ValidDataLength;
    }

    /// <summary>True while the handle is open.</summary>
    public bool IsOpen => _open;

    /// <summary>Current byte position.</summary>
    public long Position
    {
        get => _position;
        set => _position = value < 0 ? 0 : value;
    }

    /// <summary>Logical file size (ValidDataLength).</summary>
    public long Length => _entry.ValidDataLength;

    /// <summary>Access mode of the handle.</summary>
    public FileAccess Access => _access;

    /// <summary>Closes the handle (persisting any pending metadata).</summary>
    public void Dispose()
    {
        if (_open)
            Flush();
        _open = false;
    }

    /// <summary>Persists metadata and the allocation bitmap when dirty.</summary>
    public FileResult Flush()
    {
        if (!_dirty)
            return FileResult.Success;
        _dir.UpdateSetInPlace(_entry);
        if (_dir.FlushDirectory() != 0)
            return FileResult.IoError;
        if (_vol.FlushBitmap() != 0)
            return FileResult.IoError;
        _dirty = false;
        return FileResult.Success;
    }

    // ==================== reading ====================

    /// <summary>
    /// Reads up to <paramref name="count"/> bytes at the current
    /// position, stopping at ValidDataLength. Unallocated clusters in a
    /// FAT-chained stream read as zeros (sparse holes).
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="count">Maximum bytes to read.</param>
    public int Read(byte* buffer, int count)
    {
        if (!_open)
            return (int)FileResult.InvalidHandle;
        if (count < 0)
            return (int)FileResult.IoError;
        if (_position >= _entry.ValidDataLength)
            return 0;

        long avail = _entry.ValidDataLength - _position;
        if (count > avail)
            count = (int)avail;

        int done = 0;
        while (done < count)
        {
            uint cluster = MapCluster(_position);
            int inCluster = (int)(_position % _vol.ClusterSize);
            int chunk = (int)(_vol.ClusterSize) - inCluster;
            if (chunk > count - done)
                chunk = count - done;

            if (cluster == 0)
            {
                // Sparse hole: zero-fill.
                for (int i = 0; i < chunk; i++)
                    buffer[done + i] = 0;
            }
            else
            {
                int rc = ReadRange(cluster, inCluster, chunk, buffer + done);
                if (rc != 0)
                    return rc;
            }
            done += chunk;
            _position += chunk;
        }
        return done;
    }

    /// <summary>Reads a byte range inside one cluster (bounced).</summary>
    private int ReadRange(uint cluster, int offsetInCluster, int length, byte* dst)
    {
        byte* io = _vol.Io;
        if (io == null)
            return (int)FileResult.IoError;

        int remaining = length;
        ulong sector = _vol.ClusterToSector(cluster) + (ulong)(offsetInCluster / (int)_vol.SectorSize);
        int inSector = offsetInCluster % (int)_vol.SectorSize;

        while (remaining > 0)
        {
            // Bound one window to 32 KiB to stay inside the shared buffer.
            int windowSectors = 1;
            while ((windowSectors + 1) * (int)_vol.SectorSize <= 32 * 1024 &&
                   (windowSectors + 1) * (int)_vol.SectorSize < inSector + remaining)
                windowSectors++;

            int windowBytes = windowSectors * (int)_vol.SectorSize;
            int rc = _vol.ReadBytes(sector, windowBytes, io);
            if (rc != 0)
                return (int)FileResult.IoError;

            int copied = windowBytes - inSector;
            if (copied > remaining)
                copied = remaining;
            for (int i = 0; i < copied; i++)
                dst[i + (length - remaining)] = io[inSector + i];

            remaining -= copied;
            sector += (ulong)windowSectors;
            inSector = 0;
        }
        return 0;
    }

    // ==================== writing ====================

    /// <summary>
    /// Writes <paramref name="count"/> bytes at the current position,
    /// growing the stream (and converting contiguous runs to FAT chains
    /// when the heap cannot extend them in place).
    /// </summary>
    /// <param name="buffer">Source buffer.</param>
    /// <param name="count">Bytes to write.</param>
    public int Write(byte* buffer, int count)
    {
        if (!_open)
            return (int)FileResult.InvalidHandle;
        if ((_access & FileAccess.Write) == 0)
            return (int)FileResult.AccessDenied;
        if (_vol.ReadOnly)
            return (int)FileResult.ReadOnly;
        if (count <= 0)
            return 0;

        long end = _position + count;
        if (end > _allocLength)
        {
            int rc = GrowStream(end);
            if (rc != 0)
                return rc;
        }

        int done = 0;
        while (done < count)
        {
            uint cluster = MapCluster(_position);
            if (cluster == 0)
                return (int)FileResult.IoError;
            int inCluster = (int)(_position % _vol.ClusterSize);
            int chunk = (int)(_vol.ClusterSize) - inCluster;
            if (chunk > count - done)
                chunk = count - done;

            int rc = WriteRange(cluster, inCluster, chunk, buffer + done);
            if (rc != 0)
                return rc;

            done += chunk;
            _position += chunk;
        }

        if (_position > _entry.ValidDataLength)
        {
            _entry.ValidDataLength = _position;
            _entry.DataLength = _entry.ValidDataLength;   // exact size (see _allocLength)
        }

        TouchModifyTime();
        _dirty = true;
        return done;
    }

    /// <summary>Writes a byte range inside one cluster (bounced).</summary>
    private int WriteRange(uint cluster, int offsetInCluster, int length, byte* src)
    {
        byte* io = _vol.Io;
        if (io == null)
            return (int)FileResult.IoError;

        int remaining = length;
        ulong sector = _vol.ClusterToSector(cluster) + (ulong)(offsetInCluster / (int)_vol.SectorSize);
        int inSector = offsetInCluster % (int)_vol.SectorSize;

        while (remaining > 0)
        {
            int windowSectors = 1;
            while ((windowSectors + 1) * (int)_vol.SectorSize <= 32 * 1024 &&
                   (windowSectors + 1) * (int)_vol.SectorSize < inSector + remaining)
                windowSectors++;
            int windowBytes = windowSectors * (int)_vol.SectorSize;

            int contiguous = windowBytes - inSector;
            if (contiguous > remaining)
                contiguous = remaining;

            if (inSector == 0 && contiguous == windowBytes)
            {
                // Full-sector window: write straight through.
                for (int i = 0; i < windowBytes; i++)
                    io[i] = src[length - remaining + i];
                int rc = _vol.WriteBytes(sector, windowBytes, io);
                if (rc != 0)
                    return (int)FileResult.IoError;
            }
            else
            {
                // Partial window: read-modify-write.
                int rc = _vol.ReadBytes(sector, windowBytes, io);
                if (rc != 0)
                    return (int)FileResult.IoError;
                for (int i = 0; i < contiguous; i++)
                    io[inSector + i] = src[length - remaining + i];
                rc = _vol.WriteBytes(sector, windowBytes, io);
                if (rc != 0)
                    return (int)FileResult.IoError;
            }

            remaining -= contiguous;
            sector += (ulong)windowSectors;
            inSector = 0;
        }
        return 0;
    }

    /// <summary>
    /// Grows the stream so that its allocated size covers
    /// <paramref name="end"/> bytes.
    /// </summary>
    private int GrowStream(long end)
    {
        long newAlloc = ((end + _vol.ClusterSize - 1) / _vol.ClusterSize) * _vol.ClusterSize;
        long oldAlloc = _allocLength;
        uint oldClusters = (uint)((oldAlloc + _vol.ClusterSize - 1) / _vol.ClusterSize);
        uint newClusters = (uint)(newAlloc / _vol.ClusterSize);
        if (newClusters <= oldClusters)
            return 0;

        if (_entry.FirstCluster == 0 && oldClusters == 0)
        {
            // First allocation: prefer a contiguous run.
            uint run = _vol.AllocateContiguous(newClusters);
            if (run != 0)
            {
                _entry.FirstCluster = run;
                _entry.NoFatChain = true;
                ZeroClusters(run, true, oldClusters, newClusters);
                return FinishGrow(newAlloc);
            }
            // Fall back to a chained allocation.
            uint first = _vol.AllocateCluster();
            if (first == 0)
                return (int)FileResult.NoSpace;
            _entry.FirstCluster = first;
            _entry.NoFatChain = false;
            uint prev = first;
            for (uint i = 1; i < newClusters; i++)
            {
                uint c = _vol.AllocateCluster();
                if (c == 0)
                    return (int)FileResult.NoSpace;
                _vol.FatSet(prev, c);
                prev = c;
            }
            ZeroClusters(first, false, 0, newClusters);
            return FinishGrow(newAlloc);
        }

        if (_entry.NoFatChain)
        {
            // Try to extend the contiguous run in place.
            bool ok = true;
            var added = new uint[newClusters - oldClusters];
            for (uint i = 0; i < added.Length; i++)
            {
                uint candidate = _entry.FirstCluster + oldClusters + i;
                if (candidate >= ExFatConst.FirstCluster + _vol.ClusterCount ||
                    _vol.BitmapIsSet(candidate))
                {
                    ok = false;
                    break;
                }
                _vol.BitmapSet(candidate, true);
                added[i] = candidate;
            }
            if (ok)
            {
                ZeroClusters(added);
                return FinishGrow(newAlloc);
            }

            // Could not extend contiguously: convert the existing run to
            // a FAT chain and continue with fresh clusters (spec 7.1.2).
            uint lastRun = _entry.FirstCluster + oldClusters - 1;
            if (!ConvertRunToChain(_entry.FirstCluster, oldClusters))
                return (int)FileResult.IoError;
            _entry.NoFatChain = false;
            if (!ConvertRunToChainFreeTail(lastRun, oldClusters, newClusters))
                return (int)FileResult.NoSpace;
            return FinishGrow(newAlloc);
        }

        // FAT-chained growth: walk to the tail and append.
        uint tail = _entry.FirstCluster;
        int guard = 0;
        while (guard < 0x100000)
        {
            uint next = _vol.FatNext(tail);
            if (next == 0)
                break;
            tail = next;
            guard++;
        }
        for (uint i = oldClusters; i < newClusters; i++)
        {
            uint c = _vol.AllocateCluster();
            if (c == 0)
                return (int)FileResult.NoSpace;
            _vol.FatSet(tail, c);
            tail = c;
        }
        ZeroClusters(_entry.FirstCluster, false, oldClusters, newClusters);
        return FinishGrow(newAlloc);
    }

    /// <summary>Links a contiguous run into the FAT (NoFatChain → chained).</summary>
    private bool ConvertRunToChain(uint first, uint count)
    {
        for (uint i = 0; i + 1 < count; i++)
        {
            if (_vol.FatSet(first + i, first + i + 1) != 0)
                return false;
        }
        return _vol.FatSet(first + count - 1, ExFatConst.FatEndOfChain) == 0;
    }

    /// <summary>Appends fresh chained clusters after a converted run.</summary>
    private bool ConvertRunToChainFreeTail(uint lastRunCluster, uint oldClusters, uint newClusters)
    {
        uint tail = lastRunCluster;
        for (uint i = oldClusters; i < newClusters; i++)
        {
            uint c = _vol.AllocateCluster();
            if (c == 0)
                return false;
            _vol.FatSet(tail, c);
            tail = c;
        }
        return true;
    }

    /// <summary>Zero-fills freshly allocated clusters.</summary>
    private void ZeroClusters(uint first, bool contiguous, uint oldClusters, uint newClusters)
    {
        for (uint i = oldClusters; i < newClusters; i++)
        {
            uint c = contiguous ? first + i : ClusterAt(first, i);
            _vol.ZeroCluster(c);
        }
    }

    /// <summary>Zero-fills an explicit cluster list (contiguous extension).</summary>
    private void ZeroClusters(uint[] clusters)
    {
        if (clusters == null)
            return;
        for (int i = 0; i < clusters.Length; i++)
            _vol.ZeroCluster(clusters[i]);
    }

    /// <summary>Walks to cluster index <paramref name="index"/> in a chain.</summary>
    private uint ClusterAt(uint first, uint index)
    {
        uint c = first;
        for (uint i = 0; i < index; i++)
        {
            uint next = _vol.FatNext(c);
            if (next == 0)
                return 0;
            c = next;
        }
        return c;
    }

    /// <summary>Updates allocation/entry metadata after a growth.</summary>
    private int FinishGrow(long newAlloc)
    {
        _allocLength = newAlloc;
        _entry.DataLength = _entry.ValidDataLength;   // exact size (see _allocLength)
        _dirty = true;
        // NOTE: the cluster-map cache (index, cluster) stays valid across
        // growth - growth only appends clusters; resetting it here forced
        // a full chain re-walk from cluster 0 after every growth window
        // (O(n^2) on large files).
        _dir.UpdateSetInPlace(_entry);
        if (_dir.FlushDirectory() != 0)
            return (int)FileResult.IoError;
        if (_vol.FlushBitmap() != 0)
            return (int)FileResult.IoError;
        _dirty = false;
        return 0;
    }

    // ==================== length ====================

    /// <summary>
    /// Sets the logical file length: extending zero-fills up to the new
    /// size, shrinking frees clusters beyond it and updates DataLength.
    /// </summary>
    /// <param name="length">New length in bytes.</param>
    public FileResult SetLength(long length)
    {
        if (!_open)
            return FileResult.InvalidHandle;
        if (length < 0)
            return FileResult.IoError;
        if ((_access & FileAccess.Write) == 0)
            return FileResult.AccessDenied;
        if (_vol.ReadOnly)
            return FileResult.ReadOnly;

        if (length == _entry.ValidDataLength)
            return FileResult.Success;

        if (length < _entry.ValidDataLength)
        {
            long keepAlloc = ((length + _vol.ClusterSize - 1) / _vol.ClusterSize) * _vol.ClusterSize;
            uint keepClusters = (uint)(keepAlloc / _vol.ClusterSize);

            if (length == 0)
            {
                _fs.FreeEntryStream(_entry);
                _entry.FirstCluster = 0;
                _entry.ValidDataLength = 0;
                _entry.DataLength = 0;
                _entry.NoFatChain = false;
                _allocLength = 0;
            }
            else
            {
                // Free everything past the kept clusters.
                if (keepAlloc < _allocLength)
                {
                    uint cut;
                    if (_entry.NoFatChain)
                    {
                        cut = _entry.FirstCluster + keepClusters;
                        uint extra = (uint)((_allocLength - keepAlloc) / _vol.ClusterSize);
                        for (uint i = 0; i < extra; i++)
                            _vol.FreeCluster(cut + i);
                    }
                    else
                    {
                        uint c = _entry.FirstCluster;
                        for (uint i = 0; i < keepClusters; i++)
                        {
                            uint next = _vol.FatNext(c);
                            if (next == 0)
                                break;
                            c = next;
                        }
                        _fs.FreeChain(_vol.FatNext(c), false);
                        _vol.FatSet(c, ExFatConst.FatEndOfChain);
                    }
                    _allocLength = keepAlloc;
                }
                _entry.ValidDataLength = length;
                _entry.DataLength = length;   // exact size (see _allocLength)
            }
            TouchModifyTime();
            _dirty = true;
            _cacheIndex = -1;
            if (Flush() != FileResult.Success)
                return FileResult.IoError;
            return FileResult.Success;
        }

        // Extend: write zeros from the current end to the new length.
        long saved = _position;
        _position = _entry.ValidDataLength;
        long toWrite = length - _entry.ValidDataLength;
        byte* io = _vol.Io;
        if (io == null)
            return FileResult.IoError;
        while (toWrite > 0)
        {
            int chunk = toWrite > 16 * 1024 ? 16 * 1024 : (int)toWrite;
            for (int i = 0; i < chunk; i++)
                io[i] = 0;
            int written = Write(io, chunk);
            if (written != chunk)
            {
                _position = saved;
                return FileResult.NoSpace;
            }
            toWrite -= chunk;
        }
        _position = saved;
        return FileResult.Success;
    }

    // ==================== internals ====================

    /// <summary>
    /// Maps a file offset to its cluster (0 for a sparse hole, i.e. a
    /// zero FAT entry inside a chained stream).
    /// </summary>
    private uint MapCluster(long offset)
    {
        long index = offset / _vol.ClusterSize;
        if (index == 0)
            return _entry.FirstCluster;

        if (_entry.NoFatChain)
            return _entry.FirstCluster + (uint)index;

        // FAT chain walk with a small cache.
        if (_cacheIndex <= index && _cacheIndex >= 0)
        {
            uint c = _cacheCluster;
            for (long i = _cacheIndex; i < index; i++)
            {
                uint next = _vol.FatNext(c);
                if (next == 0)
                    return 0;
                c = next;
            }
            _cacheIndex = index;
            _cacheCluster = c;
            return c;
        }

        uint cluster = _entry.FirstCluster;
        for (long i = 0; i < index; i++)
        {
            uint next = _vol.FatNext(cluster);
            if (next == 0)
                return 0;
            cluster = next;
        }
        _cacheIndex = index;
        _cacheCluster = cluster;
        return cluster;
    }

    /// <summary>Updates the last-modified and access timestamps.</summary>
    private void TouchModifyTime()
    {
        uint now = ExFatTime.FromEpochSeconds(ExFatTime.NowEpoch());
        _entry.ModifyTime = now;
        _entry.AccessTime = now;
    }
}
