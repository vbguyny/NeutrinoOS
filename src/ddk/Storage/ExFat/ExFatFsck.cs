// NeutrinoOS Phase 10 - exFAT check/repair engine (Task 5 core).
//
// Implements the fsck.exfat checks and repairs over an ExFatVolume:
// boot region checksums (main + backup), up-case table checksum,
// directory entry sets (SetChecksum and NameHash), stream/chain
// consistency (lengths, loops, cross-links), allocation bitmap
// reconciliation against the FAT and the directory tree, VolumeDirty
// and PercentInUse handling. Repair mode (-y) fixes everything that is
// mechanically correctable and reports the rest.

using System;
using System.Text;

namespace NeutrinoOS.DDK.Storage.ExFat;

/// <summary>Aggregated fsck verdict.</summary>
public class ExFatFsckResult
{
    /// <summary>Problems found.</summary>
    public int Errors;

    /// <summary>Problems repaired (subset of <see cref="Errors"/>).</summary>
    public int Repaired;

    /// <summary>Problems that need manual attention (not repaired).</summary>
    public int Unrepaired;

    /// <summary>Informational notes (e.g., volume dirty).</summary>
    public int Warnings;

    /// <summary>Human-readable log lines (bounded).</summary>
    public StringBuilder Log = new StringBuilder();

    /// <summary>Appends a log line (bounded to ~200 lines).</summary>
    /// <param name="line">Message.</param>
    public void Note(string line)
    {
        if (Log.Length < 32 * 1024)
        {
            Log.Append(line);
            Log.Append('\n');
        }
    }
}

/// <summary>exFAT check/repair implementation (see file header).</summary>
public static unsafe class ExFatFsck
{
    /// <summary>
    /// Checks (and optionally repairs) an exFAT volume on a block
    /// device. The device must be writable when <paramref name="repair"/>
    /// is true.
    /// </summary>
    /// <param name="device">Device holding the volume.</param>
    /// <param name="repair">True for -y style automatic repair.</param>
    /// <param name="result">Receives counts and log lines.</param>
    /// <returns>0 when no unrepaired problems remain, 1 otherwise.</returns>
    public static int Run(IBlockDevice device, bool repair, ExFatFsckResult result)
    {
        if (device == null)
        {
            result.Note("fatal: no device");
            result.Unrepaired++;
            return 1;
        }

        var vol = new ExFatVolume();
        int rc = vol.MountLenient(device, !repair);
        if (rc != 0)
        {
            result.Note("fatal: cannot read volume (" + vol.LastError + ")");
            result.Unrepaired++;
            return 1;
        }

        // ---- boot regions ----
        if (!vol.BootChecksumOk || !vol.MainBootOk)
        {
            result.Errors++;
            bool fromBackup = vol.BootChecksumOk && !vol.MainBootOk;
            if (repair)
            {
                if (vol.RepairBootRegion(fromBackup) == 0)
                {
                    result.Note(fromBackup
                        ? "repaired: main boot region restored from backup"
                        : "repaired: boot region checksum");
                    result.Repaired++;
                }
                else
                {
                    result.Note("error: boot region checksum (repair failed)");
                    result.Unrepaired++;
                }
            }
            else
            {
                result.Note(fromBackup
                    ? "error: main boot region invalid (backup region is usable)"
                    : "error: boot region checksum mismatch");
                result.Unrepaired++;
            }
        }

        // ---- up-case table ----
        if (!vol.UpcaseChecksumOk)
        {
            result.Errors++;
            if (repair)
            {
                if (vol.RepairUpcaseChecksum() == 0)
                {
                    result.Note("repaired: up-case table checksum");
                    result.Repaired++;
                }
                else
                {
                    result.Note("error: up-case table checksum (repair failed)");
                    result.Unrepaired++;
                }
            }
            else
            {
                result.Note("error: up-case table checksum mismatch");
                result.Unrepaired++;
            }
        }

        // ---- VolumeDirty ----
        if ((vol.VolumeFlags & ExFatConst.VolumeFlagVolumeDirty) != 0)
        {
            result.Warnings++;
            result.Note("note: VolumeDirty flag is set (volume was not unmounted cleanly)");
            if (repair)
            {
                if (vol.SetVolumeDirty(false) == 0)
                {
                    result.Note("repaired: VolumeDirty flag cleared");
                    result.Repaired++;
                }
            }
        }
        if ((vol.VolumeFlags & ExFatConst.VolumeFlagMediaFailure) != 0)
        {
            result.Warnings++;
            result.Note("note: MediaFailure flag is set");
        }

        // ---- tree walk: validate sets, chains, and build the shadow bitmap ----
        var state = new TreeState(vol);
        MarkMetadata(vol, state);
        WalkDirectory(vol, vol.RootDirCluster, false, 0, 0, state, repair, result);

        // ---- bitmap reconciliation ----
        ReconcileBitmap(vol, state, repair, result);

        if (repair)
        {
            vol.FlushBitmap();
            uint used = vol.ClusterCount - vol.FreeClusterCount;
            byte percent = vol.ClusterCount == 0
                ? (byte)0
                : (byte)((ulong)used * 100 / vol.ClusterCount);
            vol.WritePercentInUse(percent);
        }
        else
        {
            // Read-only check still reports the computed usage.
            byte onDisk = vol.PercentInUse;
            uint used = vol.ClusterCount - vol.FreeClusterCount;
            byte computed = vol.ClusterCount == 0
                ? (byte)0
                : (byte)((ulong)used * 100 / vol.ClusterCount);
            if (onDisk != computed && onDisk != ExFatConst.PercentInUseUnknown)
            {
                result.Warnings++;
                result.Note("note: PercentInUse is " + onDisk.ToString() +
                    "%, filesystem is " + computed.ToString() + "% full");
            }
        }

        device.Flush();
        vol.Release();
        return result.Unrepaired > 0 ? 1 : 0;
    }

    /// <summary>Per-run tree state: cluster shadow bitmap.</summary>
    private sealed class TreeState
    {
        public readonly byte[] Reachable;
        public TreeState(ExFatVolume vol)
        {
            Reachable = new byte[(vol.ClusterCount + 7) / 8];
        }
        public bool Mark(uint cluster)
        {
            int bit = (int)((cluster - ExFatConst.FirstCluster) & 7);
            int idx = (int)((cluster - ExFatConst.FirstCluster) >> 3);
            if (idx < 0 || idx >= Reachable.Length)
                return false;
            bool seen = (Reachable[idx] & (1 << bit)) != 0;
            Reachable[idx] |= (byte)(1 << bit);
            return !seen;
        }
    }

    /// <summary>
    /// Marks the volume metadata that does not live behind file entry
    /// sets: the root directory chain, the allocation bitmap stream and
    /// the up-case table stream.
    /// </summary>
    private static void MarkMetadata(ExFatVolume vol, TreeState state)
    {
        // Root directory (FAT chain).
        uint c = vol.RootDirCluster;
        int guard = 0;
        while (c >= ExFatConst.FirstCluster && c < ExFatConst.FirstCluster + vol.ClusterCount)
        {
            state.Mark(c);
            uint next = vol.FatNext(c);
            if (next == 0 || ++guard > 0x100000)
                break;
            c = next;
        }

        // Allocation bitmap (contiguous).
        uint bitmapClusters = (uint)((vol.BitmapLength + vol.ClusterSize - 1) / vol.ClusterSize);
        for (uint i = 0; i < bitmapClusters; i++)
            state.Mark(vol.BitmapCluster + i);

        // Up-case table (contiguous).
        uint upcaseClusters = (uint)((vol.UpcaseLength + vol.ClusterSize - 1) / vol.ClusterSize);
        for (uint i = 0; i < upcaseClusters; i++)
            state.Mark(vol.UpcaseCluster + i);
    }

    /// <summary>Walks a directory (and recursively its subdirectories).</summary>
    private static void WalkDirectory(ExFatVolume vol, uint firstCluster, bool noFatChain,
        long dataLength, int depth, TreeState state, bool repair, ExFatFsckResult result)
    {
        if (depth > 64)
        {
            result.Errors++;
            result.Unrepaired++;
            result.Note("error: directory nesting deeper than 64 at cluster " + firstCluster.ToString());
            return;
        }

        var dir = new ExFatDir(vol);
        bool ok = depth == 0
            ? dir.LoadRootDirectory()
            : dir.LoadDirectory(firstCluster, noFatChain, dataLength);
        if (!ok)
        {
            result.Errors++;
            result.Unrepaired++;
            result.Note("error: cannot read directory at cluster " + firstCluster.ToString());
            return;
        }

        var entry = new ExFatDirEntry();
        int index = dir.NextEntry(0, entry);
        while (index >= 0)
        {
            bool changed = false;
            bool critical = false;

            if (entry.Corrupt)
            {
                result.Errors++;
                // Repair: recompute the set checksum and name hash when
                // the name itself decoded cleanly.
                if (repair && entry.Name.Length > 0 && dir.RepairSet(index))
                {
                    result.Note("repaired: entry set checksum for \"" + entry.Name + "\"");
                    result.Repaired++;
                    changed = true;
                }
                else if (!(repair && entry.Name.Length > 0))
                {
                    result.Note("error: corrupt entry set at index " + index.ToString() +
                        " (unrepaired)");
                    result.Unrepaired++;
                    critical = true;
                }
            }

            if (!critical && entry.Name.Length > 0)
            {
                // Stream consistency.
                if (entry.ValidDataLength > entry.DataLength && entry.DataLength > 0)
                {
                    result.Errors++;
                    if (repair)
                    {
                        entry.ValidDataLength = entry.DataLength;
                        dir.UpdateSetInPlace(entry);
                        changed = true;
                        result.Note("repaired: ValidDataLength clamped for \"" + entry.Name + "\"");
                        result.Repaired++;
                    }
                    else
                    {
                        result.Note("error: ValidDataLength > DataLength for \"" + entry.Name + "\"");
                    }
                }

                if (entry.HasStream)
                {
                    uint needed = (uint)((entry.DataLength + vol.ClusterSize - 1) / vol.ClusterSize);
                    WalkChain(vol, entry, needed, state, repair, result, out bool streamFixed);
                    if (streamFixed)
                        changed = true;
                    if (entry.IsDirectory)
                    {
                        WalkDirectory(vol, entry.FirstCluster, entry.NoFatChain,
                            entry.DataLength, depth + 1, state, repair, result);
                    }
                }
                else if (entry.DataLength != 0 || entry.ValidDataLength != 0)
                {
                    result.Errors++;
                    if (repair)
                    {
                        entry.DataLength = 0;
                        entry.ValidDataLength = 0;
                        dir.UpdateSetInPlace(entry);
                        changed = true;
                        result.Note("repaired: zero-length stream for \"" + entry.Name + "\"");
                        result.Repaired++;
                    }
                    else
                    {
                        result.Note("error: stream without clusters for \"" + entry.Name + "\"");
                    }
                }
            }

            if (changed)
                dir.FlushDirectory();

            index = dir.NextEntry(index + entry.TotalEntries, entry);
        }
    }

    /// <summary>
    /// Validates one stream's cluster chain, marking clusters in the
    /// shadow bitmap. Reports double usage and FAT loops; trims chains
    /// longer than the stream needs.
    /// </summary>
    private static void WalkChain(ExFatVolume vol, ExFatDirEntry entry, uint needed,
        TreeState state, bool repair, ExFatFsckResult result, out bool fixedStream)
    {
        fixedStream = false;
        uint cluster = entry.FirstCluster;
        uint walked = 0;
        uint lastWalked = 0;
        int guard = 0;
        while (cluster >= ExFatConst.FirstCluster &&
               cluster < ExFatConst.FirstCluster + vol.ClusterCount)
        {
            if (!state.Mark(cluster))
            {
                result.Errors++;
                result.Unrepaired++;
                result.Note("error: cluster " + cluster.ToString() +
                    " used twice or chain loop (in \"" + entry.Name + "\")");
                break;
            }
            walked++;
            lastWalked = cluster;
            if (walked >= needed && needed > 0)
            {
                // The stream has all the clusters it needs. If the FAT
                // chain continues anyway, trim it (repair).
                uint next = entry.NoFatChain ? (cluster + 1) : vol.FatNext(cluster);
                bool more = !entry.NoFatChain && next != 0;
                if (more)
                {
                    result.Errors++;
                    if (repair)
                    {
                        FreeChainFrom(vol, next);
                        vol.FatSet(cluster, ExFatConst.FatEndOfChain);
                        result.Note("repaired: trimmed extra clusters after " + cluster.ToString() +
                            " in \"" + entry.Name + "\"");
                        result.Repaired++;
                        fixedStream = true;
                    }
                    else
                    {
                        result.Note("error: chain longer than DataLength in \"" + entry.Name + "\"");
                    }
                }
                break;
            }
            cluster = entry.NoFatChain ? cluster + 1 : vol.FatNext(cluster);
            if (++guard > 0x1000000)
            {
                result.Errors++;
                result.Unrepaired++;
                result.Note("error: FAT loop in \"" + entry.Name + "\"");
                break;
            }
        }

        if (walked < needed && (needed > 0))
        {
            result.Errors++;
            result.Unrepaired++;
            result.Note("error: chain shorter than DataLength in \"" + entry.Name + "\" (" +
                walked.ToString() + " of " + needed.ToString() + " clusters)");
        }

        // Force-close contiguous streams that hit the heap end.
        if (lastWalked != 0 && entry.NoFatChain && walked < needed)
        {
            // Report only; data beyond the chain is unreadable.
        }
    }

    /// <summary>Frees a chain starting at a cluster.</summary>
    private static void FreeChainFrom(ExFatVolume vol, uint cluster)
    {
        int guard = 0;
        while (cluster >= ExFatConst.FirstCluster &&
               cluster < ExFatConst.FirstCluster + vol.ClusterCount)
        {
            uint next = vol.FatNext(cluster);
            vol.FreeCluster(cluster);
            if (next == 0)
                break;
            cluster = next;
            if (++guard > 0x1000000)
                break;
        }
    }

    /// <summary>
    /// Reconciles the on-disk allocation bitmap with the shadow bitmap
    /// built from the  walk: allocated-but-unreachable clusters are
    /// freed (repair), reachable-but-free clusters are allocated
    /// (repair). Reports the diff either way.
    /// </summary>
    private static void ReconcileBitmap(ExFatVolume vol, TreeState state, bool repair,
        ExFatFsckResult result)
    {
        uint orphans = 0;
        uint missing = 0;
        uint clusters = vol.ClusterCount;
        for (uint i = 0; i < clusters; i++)
        {
            bool onDisk = vol.BitmapIsSet(i + ExFatConst.FirstCluster);
            int idx = (int)(i >> 3);
            int bit = (int)(i & 7);
            bool reachable = (state.Reachable[idx] & (1 << bit)) != 0;
            if (onDisk && !reachable)
            {
                orphans++;
                if (repair)
                    vol.BitmapSet(i + ExFatConst.FirstCluster, false);
            }
            else if (!onDisk && reachable)
            {
                missing++;
                if (repair)
                    vol.BitmapSet(i + ExFatConst.FirstCluster, true);
            }
        }

        if (orphans > 0)
        {
            result.Errors++;
            if (repair)
            {
                result.Note("repaired: freed " + orphans.ToString() + " orphaned cluster(s)");
                result.Repaired++;
            }
            else
            {
                result.Note("error: " + orphans.ToString() + " orphaned allocated cluster(s)");
                result.Unrepaired++;
            }
        }
        if (missing > 0)
        {
            result.Errors++;
            if (repair)
            {
                result.Note("repaired: allocated " + missing.ToString() + " cluster(s) missing from the bitmap");
                result.Repaired++;
            }
            else
            {
                result.Note("error: " + missing.ToString() + " in-use cluster(s) marked free");
                result.Unrepaired++;
            }
        }
    }
}
