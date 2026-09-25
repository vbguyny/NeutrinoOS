// ProtonOS DDK - FAT long-filename entry creation (Phase 8 fix)
//
// The original single CreateLongNameEntry method (FatFileSystem.cs) was
// miscompiled by the on-device Tier-0 JIT: the per-slot scan computed the
// free-run start correctly (verified by tracing each slot), but the code
// after the scan read a corrupted value for the same local (runStart read
// back as -1, so the insert position degenerated to "past the end of the
// directory", the resulting cluster walk fell off the chain into an
// end-of-chain FAT value 0x0FFFFFF8, and even a `return false` inside the
// failure branch was not honoured). The write path then reported success
// while no directory entry ever reached the disk: guest writes to
// long filenames (e.g. npkg's repos.json.tmp / installed.json) silently
// vanished.
//
// Fix strategy (documented as finding F15 in docs/PHASE8-REPORT.md):
// split the work into small methods whose basic blocks stay well inside
// what the Tier-0 JIT compiles correctly. CreateDirectoryEntry now calls
// CreateLongNameEntrySafe; the legacy method is retained for reference
// only and must not be called.

using System;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Platform;

namespace ProtonOS.Drivers.Storage.Fat;

public unsafe partial class FatFileSystem
{
    /// <summary>
    /// Phase 8: the JIT-safe replacement for CreateLongNameEntry.
    /// Creates a directory entry for a name that does not fit 8.3:
    /// a unique 8.3 alias plus the LFN entries carrying the real name.
    /// Small helper methods: FindLfnRun (scan + extend), BuildUniqueAlias,
    /// WriteLfnChain.
    /// </summary>
    private bool CreateLongNameEntrySafe(uint dirCluster, string name, byte attr, int lfnCount,
                                         out FatDirEntry entry, out int entryIndex)
    {
        entry = default;
        entryIndex = -1;
        if (_device == null || _readOnly)
            return false;

        int need = lfnCount + 1;
        bool isRoot = dirCluster == 0 && _fatType != FatType.Fat32;
        uint dirBytes = isRoot ? _rootEntryCount * 32 : _bytesPerCluster;
        int entriesPerCluster = (int)(dirBytes / 32);

        string[] existing;
        int existingCount;
        uint runCluster;
        int runSlot;
        int runChainPos;
        if (!FindLfnRun(dirCluster, isRoot, entriesPerCluster, need,
                        out existing, out existingCount, out runCluster, out runSlot, out runChainPos))
            return false;

        var aliasArr = new byte[11];
        if (!BuildUniqueAlias(name, existing, existingCount, aliasArr))
            return false;

        if (!WriteLfnChain(dirCluster, isRoot, entriesPerCluster, runCluster, runSlot,
                           name, lfnCount, attr, aliasArr, out entry))
            return false;

        entryIndex = isRoot
            ? runSlot + lfnCount
            : runChainPos * entriesPerCluster + runSlot + lfnCount;

        return true;
    }

    /// <summary>
    /// Scans a directory's cluster chain for a run of <paramref name="need"/>
    /// consecutive free slots, collecting existing short names on the way.
    /// When the chain has no such run, a fresh cluster is appended and used.
    /// Outputs the containing cluster, the slot offset of the run start and
    /// the run start's cluster position within the chain.
    /// </summary>
    private bool FindLfnRun(uint dirCluster, bool isRoot, int entriesPerCluster, int need,
                            out string[] existing, out int existingCount,
                            out uint runCluster, out int runSlot, out int runChainPos)
    {
        existing = new string[512];
        existingCount = 0;
        runCluster = 0;
        runSlot = 0;
        runChainPos = 0;

        uint dirBytes = isRoot ? _rootEntryCount * 32 : _bytesPerCluster;
        ulong pageCount = ((ulong)dirBytes + 4095) / 4096;
        ulong bufferPhys = Memory.AllocatePages(pageCount);
        if (bufferPhys == 0)
            return false;
        byte* buffer = (byte*)Memory.PhysToVirt(bufferPhys);

        try
        {
            uint cluster = dirCluster;
            uint tail = dirCluster;
            bool sawEnd = false;
            bool found = false;
            int runLen = 0;
            int runStart = -1;
            int chainPos = 0;

            for (int guard = 0; guard < 4096 && !found; guard++)
            {
                if (isRoot)
                {
                    if (!ReadRootDirectory(buffer, dirBytes))
                    {
                        Memory.FreePages(bufferPhys, pageCount);
                        return false;
                    }
                }
                else if (!ReadCluster(cluster, buffer))
                {
                    Memory.FreePages(bufferPhys, pageCount);
                    return false;
                }

                var slots = (FatDirEntry*)buffer;
                for (int i = 0; i < entriesPerCluster; i++)
                {
                    var e = slots[i];
                    bool free;
                    if (sawEnd)
                    {
                        free = true;
                    }
                    else if (e.Name[0] == 0)
                    {
                        sawEnd = true;
                        free = true;
                    }
                    else if (e.Name[0] == 0xE5)
                    {
                        free = true;
                    }
                    else if ((e.Attr & (byte)FatAttr.LongName) == (byte)FatAttr.LongName)
                    {
                        free = false;
                    }
                    else if ((e.Attr & (byte)FatAttr.VolumeId) != 0)
                    {
                        free = false;
                    }
                    else
                    {
                        if (existingCount < existing.Length)
                        {
                            var nb = new char[11];
                            for (int b = 0; b < 11; b++)
                                nb[b] = (char)e.Name[b];
                            existing[existingCount++] = new string(nb);
                        }
                        free = false;
                    }

                    if (free)
                    {
                        if (runLen == 0)
                        {
                            runStart = i;
                            runChainPos = chainPos;
                            runCluster = isRoot ? 0 : cluster;
                        }
                        runLen++;
                        if (runLen >= need)
                        {
                            found = true;
                            break;
                        }
                    }
                    else
                    {
                        runLen = 0;
                        runStart = -1;
                    }
                }

                if (found)
                    break;
                if (isRoot)
                    break;              // FAT12/16 root is fixed size

                tail = cluster;
                uint next = GetFatEntry(cluster);
                if (FatCluster.IsEndOfChain(next, _fatType))
                {
                    // No run in the existing chain: append one zeroed cluster.
                    uint nc = ExtendClusterChain(tail);
                    if (nc == 0)
                    {
                        Memory.FreePages(bufferPhys, pageCount);
                        return false;
                    }
                    for (uint b = 0; b < _bytesPerCluster; b++)
                        buffer[b] = 0;
                    if (!WriteCluster(nc, buffer))
                    {
                        Memory.FreePages(bufferPhys, pageCount);
                        return false;
                    }
                    WriteFat();
                    cluster = nc;
                    chainPos++;
                    runLen = 0;         // fresh cluster: all slots free
                    sawEnd = true;      // treat unread slots as free too
                    continue;
                }
                cluster = next;
                chainPos++;
            }

            Memory.FreePages(bufferPhys, pageCount);
            if (!found)
                return false;

            runSlot = runStart;
            return true;
        }
        catch
        {
            Memory.FreePages(bufferPhys, pageCount);
            return false;
        }
    }

    /// <summary>
    /// Picks the first unused "BASE~N.EXT" alias for a long name.
    /// Returns false when no alias can be produced.
    /// </summary>
    private static bool BuildUniqueAlias(string name, string[] existing, int existingCount, byte[] aliasArr)
    {
        for (int k = 1; k < 1000; k++)
        {
            if (!BuildShortAlias(name, k, aliasArr))
                return false;
            bool used = false;
            for (int i = 0; i < existingCount && !used; i++)
                used = AliasMatches(existing[i], aliasArr);
            if (!used)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Writes the LFN slot chain followed by the short directory entry,
    /// starting at (<paramref name="runCluster"/>, <paramref name="runSlot"/>).
    /// Outputs the freshly written short entry.
    /// </summary>
    private bool WriteLfnChain(uint dirCluster, bool isRoot, int entriesPerCluster,
                               uint runCluster, int runSlot,
                               string name, int lfnCount, byte attr, byte[] aliasArr,
                               out FatDirEntry entry)
    {
        entry = default;
        int need = lfnCount + 1;
        uint dirBytes = isRoot ? _rootEntryCount * 32 : _bytesPerCluster;
        ulong pageCount = ((ulong)dirBytes + 4095) / 4096;
        ulong bufferPhys = Memory.AllocatePages(pageCount);
        if (bufferPhys == 0)
            return false;
        byte* buffer = (byte*)Memory.PhysToVirt(bufferPhys);

        try
        {
            byte checksum = ComputeLfnChecksum(aliasArr);
            uint wc = runCluster;
            int written = 0;

            for (int guard = 0; guard < 4096 && written < need; guard++)
            {
                if (isRoot)
                {
                    if (!ReadRootDirectory(buffer, dirBytes))
                    {
                        Memory.FreePages(bufferPhys, pageCount);
                        return false;
                    }
                }
                else if (!ReadCluster(wc, buffer))
                {
                    Memory.FreePages(bufferPhys, pageCount);
                    return false;
                }

                var slots = (FatDirEntry*)buffer;
                int startAt = written == 0 ? runSlot : 0;
                for (int i = startAt; i < entriesPerCluster && written < need; i++)
                {
                    var slot = slots + i;
                    if (written < lfnCount)
                    {
                        BuildLfnSlot(slot, name, lfnCount, written, checksum);
                    }
                    else
                    {
                        for (int b = 0; b < 11; b++)
                            slot->Name[b] = aliasArr[b];
                        slot->Attr = attr;
                        slot->NTRes = 0;
                        slot->CrtTimeTenth = 0;
                        slot->CrtTime = 0;
                        slot->CrtDate = 0;
                        slot->LstAccDate = 0;
                        slot->FstClusHI = 0;
                        slot->WrtTime = 0;
                        slot->WrtDate = 0;
                        slot->FstClusLO = 0;
                        slot->FileSize = 0;
                        entry = *slot;
                    }
                    written++;
                }

                bool ok;
                if (isRoot)
                {
                    ulong rootSector = _reservedSectors + (_numFats * _fatSizeSectors);
                    int result = _device.Write(rootSector, _rootDirSectors, buffer);
                    ok = result == (int)_rootDirSectors;
                }
                else
                {
                    ok = WriteCluster(wc, buffer);
                }

                if (!ok)
                {
                    Memory.FreePages(bufferPhys, pageCount);
                    return false;
                }

                if (written < need && !isRoot)
                {
                    wc = GetFatEntry(wc);
                    if (FatCluster.IsEndOfChain(wc, _fatType))
                    {
                        Memory.FreePages(bufferPhys, pageCount);
                        return false;
                    }
                }
            }

            Memory.FreePages(bufferPhys, pageCount);
            return written >= need;
        }
        catch
        {
            Memory.FreePages(bufferPhys, pageCount);
            return false;
        }
    }
}
