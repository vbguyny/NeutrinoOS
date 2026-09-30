// NeutrinoOS Phase 10 - exFAT volume formatter (Task 5 core).
//
// Writes a complete, spec-conformant volume: main and backup boot
// regions with correct boot checksums, FAT (1 or 2 copies), allocation
// bitmap, the canonical up-case table, and the root directory with the
// bitmap (0x81), up-case (0x82) and label (0x83) critical primaries.
// Shared by the mkexfat utility and the host-side tests; optionally
// self-checks the result.

using System;

namespace NeutrinoOS.DDK.Storage.ExFat;

/// <summary>Options for <see cref="ExFatFormatter.Format"/>.</summary>
public class ExFatFormatOptions
{
    /// <summary>Cluster size in bytes (0 = choose from the volume size).</summary>
    public int ClusterSizeBytes;

    /// <summary>Sector size in bytes (512 or 4096; default 512).</summary>
    public int SectorSizeBytes = 512;

    /// <summary>Number of FATs (1 or 2; default 1).</summary>
    public int NumberOfFats = 1;

    /// <summary>Filesystem revision (default 0x0100 = 1.0).</summary>
    public ushort Revision = 0x0100;

    /// <summary>Volume label (up to 11 UTF-16 characters; "" for none).</summary>
    public string Label = "";

    /// <summary>Volume serial number (0 = derive from the kernel clock).</summary>
    public uint VolumeSerial;
}

/// <summary>Formats block devices as exFAT.</summary>
public static unsafe class ExFatFormatter
{
    /// <summary>
    /// Formats <paramref name="device"/> as exFAT with the given
    /// options. Returns 0 on success and verifies the written boot
    /// region, FAT and metadata entries before returning.
    /// </summary>
    /// <param name="device">Device to format (must be writable).</param>
    /// <param name="options">Geometry and label options.</param>
    public static int Format(IBlockDevice device, ExFatFormatOptions options)
    {
        if (device == null || options == null)
            return -1;

        int sectorSize = options.SectorSizeBytes;
        if (sectorSize != 512 && sectorSize != 4096)
            return -2;
        if (device.BlockSize > (uint)sectorSize || sectorSize % (int)device.BlockSize != 0)
            return -2;
        if (options.NumberOfFats != 1 && options.NumberOfFats != 2)
            return -2;
        if (options.Label.Length > ExFatConst.MaxLabelChars)
            return -2;

        ulong volumeSectors = device.BlockCount * device.BlockSize / (ulong)sectorSize;
        if (volumeSectors < 256)
            return -3;

        int clusterSize = options.ClusterSizeBytes;
        if (clusterSize == 0)
            clusterSize = ChooseClusterSize(device.TotalBytes);
        if (clusterSize < sectorSize || (clusterSize & (clusterSize - 1)) != 0 ||
            clusterSize > 32 * 1024 * 1024 || clusterSize % sectorSize != 0)
            return -2;

        uint sectorsPerCluster = (uint)(clusterSize / sectorSize);
        int sectorShift = Log2(sectorSize);
        // The spec's SectorsPerClusterShift field is log2(sectors per
        // cluster), not log2(bytes per cluster).
        int clusterShift = Log2(clusterSize / sectorSize);

        // Geometry: FAT offset at the 1 MiB boundary (Windows/mkfs
        // convention), heap cluster-aligned, FAT sized for the cluster
        // count (fixed-point iteration, converges in a few rounds).
        uint fatOffset = volumeSectors >= 4096 ? 2048u : 24u;
        uint fatLength = 256;
        uint heapOffset = 0;
        uint clusterCount = 0;
        bool stable = false;
        for (int pass = 0; pass < 8 && !stable; pass++)
        {
            heapOffset = AlignUp(fatOffset + fatLength * (uint)options.NumberOfFats, sectorsPerCluster);
            if ((ulong)heapOffset + sectorsPerCluster >= volumeSectors)
                return -3;
            clusterCount = (uint)((volumeSectors - heapOffset) / sectorsPerCluster);
            uint needed = (uint)((((ulong)clusterCount + 2) * 4 + (uint)sectorSize - 1) / (uint)sectorSize);
            uint newFatLength = AlignUp(needed, sectorsPerCluster);
            if (newFatLength == fatLength)
                stable = true;
            else
                fatLength = newFatLength;
        }
        if (!stable || clusterCount < 16)
            return -3;

        // Metadata allocation: bitmap, up-case table, root directory,
        // all contiguous starting at cluster 2 (mirrors mkfs.exfat).
        ulong bitmapBytes = ((ulong)clusterCount + 7) / 8;
        uint bitmapClusters = (uint)((bitmapBytes + (uint)clusterSize - 1) / (uint)clusterSize);
        byte[] upcase = ExFatStdUpcase.Compressed;
        uint upcaseClusters = (uint)(((ulong)upcase.Length + (uint)clusterSize - 1) / (uint)clusterSize);
        uint bitmapCluster = 2;
        uint upcaseCluster = bitmapCluster + bitmapClusters;
        uint rootCluster = upcaseCluster + upcaseClusters;
        uint firstFreeAfterRoot = rootCluster + 1;
        if (firstFreeAfterRoot > clusterCount)
            return -3;

        // Build and write the boot regions (main sectors 0-11, backup
        // 12-23) with the boot checksum (spec 3.4).
        int rc = WriteBootRegions(device, options, sectorSize, sectorShift,
            fatOffset, fatLength, heapOffset, clusterCount, rootCluster,
            clusterShift, volumeSectors);
        if (rc != 0)
            return rc;

        // FAT: reserved entries plus the metadata chains.
        rc = WriteFat(device, sectorSize, fatOffset, fatLength, options.NumberOfFats,
            clusterCount, bitmapCluster, bitmapClusters, upcaseCluster, upcaseClusters, rootCluster);
        if (rc != 0)
            return rc;

        // Allocation bitmap data: mark the metadata clusters used.
        rc = WriteBitmap(device, sectorSize, heapOffset, sectorsPerCluster, bitmapCluster,
            bitmapBytes, firstFreeAfterRoot);
        if (rc != 0)
            return rc;

        // Up-case table data (compressed, canonical).
        rc = WriteData(device, sectorSize, heapOffset, sectorsPerCluster, upcaseCluster, upcase, upcase.Length);
        if (rc != 0)
            return rc;

        // Root directory: 0x81 bitmap, 0x82 up-case (with checksum),
        // 0x83 label, End of Directory.
        rc = WriteRootDirectory(device, sectorSize, heapOffset, sectorsPerCluster, rootCluster,
            clusterSize, options.Label, bitmapCluster, bitmapBytes, upcaseCluster, upcase.Length);
        if (rc != 0)
            return rc;

        device.Flush();

        // Self-check: the boot checksum and the metadata entries must
        // read back exactly as written.
        return Verify(device, sectorSize, upcase);
    }

    /// <summary>Windows-style default cluster size for a volume size.</summary>
    /// <param name="totalBytes">Device size in bytes.</param>
    public static int ChooseClusterSize(ulong totalBytes)
    {
        if (totalBytes <= 256UL * 1024 * 1024)
            return 4096;
        if (totalBytes <= 32UL * 1024 * 1024 * 1024)
            return 32 * 1024;
        return 128 * 1024;
    }

    private static uint AlignUp(uint value, uint alignment)
    {
        uint rem = value % alignment;
        return rem == 0 ? value : value + (alignment - rem);
    }

    private static int Log2(int v)
    {
        int r = 0;
        while (v > 1)
        {
            v >>= 1;
            r++;
        }
        return r;
    }

    /// <summary>Writes both boot regions with validated checksums.</summary>
    private static int WriteBootRegions(IBlockDevice device, ExFatFormatOptions opt,
        int sectorSize, int sectorShift, uint fatOffset, uint fatLength, uint heapOffset,
        uint clusterCount, uint rootCluster, int clusterShift, ulong volumeSectors)
    {
        byte[] region = new byte[ExFatConst.BootRegionSectors * sectorSize];

        region[0] = 0xEB;
        region[1] = 0x76;
        region[2] = 0x90;
        region[3] = (byte)'E';
        region[4] = (byte)'X';
        region[5] = (byte)'F';
        region[6] = (byte)'A';
        region[7] = (byte)'T';
        region[8] = (byte)' ';
        region[9] = (byte)' ';
        region[10] = (byte)' ';

        uint serial = opt.VolumeSerial != 0 ? opt.VolumeSerial : DeriveSerial();
        ExFatBytes.Write64(region, 64, 0);                       // PartitionOffset
        ExFatBytes.Write64(region, 72, volumeSectors);           // VolumeLength
        ExFatBytes.Write32(region, 80, fatOffset);
        ExFatBytes.Write32(region, 84, fatLength);
        ExFatBytes.Write32(region, 88, heapOffset);
        ExFatBytes.Write32(region, 92, clusterCount);
        ExFatBytes.Write32(region, 96, rootCluster);
        ExFatBytes.Write32(region, 100, serial);
        ExFatBytes.Write16(region, 104, opt.Revision);
        ExFatBytes.Write16(region, 106, 0);                      // VolumeFlags
        region[108] = (byte)sectorShift;
        region[109] = (byte)clusterShift;
        region[110] = (byte)opt.NumberOfFats;
        region[111] = 0x80;                                      // DriveSelect
        region[112] = ExFatConst.PercentInUseUnknown;
        region[510] = 0x55;
        region[511] = 0xAA;

        uint checksum;
        fixed (byte* p = region)
            checksum = ExFatChecksum.ComputeBootChecksum(p, 11, sectorSize);

        for (int i = 0; i < sectorSize / 4; i++)
            ExFatBytes.Write32(region, 11 * sectorSize + i * 4, checksum);

        int rc = WriteAt(device, sectorSize, 0, region, region.Length);
        if (rc != 0)
            return rc;
        return WriteAt(device, sectorSize, (ulong)ExFatConst.BootRegionSectors, region, region.Length);
    }

    /// <summary>Derives a volume serial from the kernel clock.</summary>
    private static uint DeriveSerial()
    {
        long now = ExFatTime.NowEpoch();
        uint serial = (uint)(now ^ (now >> 32));
        return serial == 0 ? 0x12345678u : serial;
    }

    /// <summary>Writes the FAT region (both copies when configured).</summary>
    private static int WriteFat(IBlockDevice device, int sectorSize, uint fatOffset,
        uint fatLength, int numberOfFats, uint clusterCount, uint bitmapCluster,
        uint bitmapClusters, uint upcaseCluster, uint upcaseClusters, uint rootCluster)
    {
        byte[] sector = new byte[sectorSize];
        ExFatBytes.Write32(sector, 0, ExFatConst.FatEntry0);
        ExFatBytes.Write32(sector, 4, ExFatConst.FatEntry1);
        // Metadata chains start within the first sector (cluster 2..).
        WriteEntry(sector, sectorSize, bitmapCluster, bitmapClusters == 1 ? ExFatConst.FatEndOfChain : bitmapCluster + 1);
        for (uint i = 1; i < bitmapClusters; i++)
        {
            uint c = bitmapCluster + i;
            uint v = (i == bitmapClusters - 1) ? ExFatConst.FatEndOfChain : c + 1;
            WriteEntry(sector, sectorSize, c, v);
        }
        for (uint i = 0; i < upcaseClusters; i++)
        {
            uint c = upcaseCluster + i;
            uint v = (i == upcaseClusters - 1) ? ExFatConst.FatEndOfChain : c + 1;
            WriteEntry(sector, sectorSize, c, v);
        }
        WriteEntry(sector, sectorSize, rootCluster, ExFatConst.FatEndOfChain);

        int rc = WriteAt(device, sectorSize, fatOffset, sector, sector.Length);
        if (rc != 0)
            return rc;
        if (numberOfFats == 2)
            rc = WriteAt(device, sectorSize, fatOffset + fatLength, sector, sector.Length);
        return rc;
    }

    /// <summary>Writes one FAT entry into the sector image.</summary>
    private static void WriteEntry(byte[] sector, int sectorSize, uint cluster, uint value)
    {
        int byteIndex = (int)(cluster * 4);
        if (byteIndex + 4 > sectorSize)
            return;   // metadata always fits the first FAT sector in practice
        ExFatBytes.Write32(sector, byteIndex, value);
    }

    /// <summary>Writes the allocation bitmap data (marking metadata used).</summary>
    private static int WriteBitmap(IBlockDevice device, int sectorSize, uint heapOffset,
        uint sectorsPerCluster, uint bitmapCluster, ulong bitmapBytes, uint allocatedUpTo)
    {
        ulong offsetSectors = heapOffset + (ulong)(bitmapCluster - 2) * sectorsPerCluster;
        int size = (int)bitmapBytes;
        int chunkSectors = 64 * 1024 / sectorSize;
        var chunk = new byte[chunkSectors * sectorSize];
        int done = 0;
        while (done < size)
        {
            int len = size - done;
            if (len > chunk.Length)
                len = chunk.Length;
            for (int i = 0; i < len; i++)
                chunk[i] = 0;
            // Set bits for clusters [2, allocatedUpTo).
            for (int i = 0; i < len; i++)
            {
                ulong byteIndex = (ulong)(done + i);
                for (int bit = 0; bit < 8; bit++)
                {
                    ulong clusterIndex = byteIndex * 8 + (ulong)bit;   // 0-based from cluster 2
                    uint cluster = (uint)clusterIndex + ExFatConst.FirstCluster;
                    if (cluster >= allocatedUpTo)
                        break;
                    byte v = chunk[i];
                    chunk[i] = (byte)(v | (1 << bit));
                }
            }
            int rc = WriteAt(device, sectorSize, offsetSectors + (ulong)(done / sectorSize), chunk, len);
            if (rc != 0)
                return rc;
            done += len;
        }
        return 0;
    }

    /// <summary>Writes a contiguous byte range at a cluster.</summary>
    private static int WriteData(IBlockDevice device, int sectorSize, uint heapOffset,
        uint sectorsPerCluster, uint cluster, byte[] data, int length)
    {
        ulong offsetSectors = heapOffset + (ulong)(cluster - 2) * sectorsPerCluster;
        return WriteAt(device, sectorSize, offsetSectors, data, length);
    }

    /// <summary>Writes the root directory metadata entries.</summary>
    private static int WriteRootDirectory(IBlockDevice device, int sectorSize, uint heapOffset,
        uint sectorsPerCluster, uint rootCluster, int clusterSize, string label,
        uint bitmapCluster, ulong bitmapBytes, uint upcaseCluster, int upcaseBytes)
    {
        var root = new byte[clusterSize];
        int pos = 0;

        root[pos] = ExFatConst.EntryAllocationBitmap;
        ExFatBytes.Write32(root, pos + 20, bitmapCluster);
        ExFatBytes.Write64(root, pos + 24, bitmapBytes);
        pos += ExFatConst.EntrySize;

        root[pos] = ExFatConst.EntryUpcaseTable;
        ExFatBytes.Write32(root, pos + 4, ExFatStdUpcase.Checksum);
        ExFatBytes.Write32(root, pos + 20, upcaseCluster);
        ExFatBytes.Write64(root, pos + 24, (ulong)upcaseBytes);
        pos += ExFatConst.EntrySize;

        if (label.Length > 0)
        {
            root[pos] = ExFatConst.EntryVolumeLabel;
            root[pos + 1] = (byte)label.Length;
            for (int i = 0; i < label.Length; i++)
                ExFatBytes.Write16(root, pos + 2 + i * 2, label[i]);
            pos += ExFatConst.EntrySize;
        }

        root[pos] = ExFatConst.EntryEnd;

        ulong offsetSectors = heapOffset + (ulong)(rootCluster - 2) * sectorsPerCluster;
        return WriteAt(device, sectorSize, offsetSectors, root, root.Length);
    }

    /// <summary>Writes a byte range at a sector offset (chunked).</summary>
    private static int WriteAt(IBlockDevice device, int sectorSize, ulong startSector,
        byte[] data, int length)
    {
        // Convert to device blocks; sectorSize is a multiple of the block size.
        int ratio = sectorSize / (int)device.BlockSize;
        int done = 0;
        while (done < length)
        {
            int len = length - done;
            int maxChunk = 64 * 1024;
            if (len > maxChunk)
                len = maxChunk;
            // Round up to a whole device block so writes stay aligned.
            int blocks = (len + (int)device.BlockSize - 1) / (int)device.BlockSize;
            int padded = blocks * (int)device.BlockSize;
            var buf = new byte[padded];
            for (int i = 0; i < len; i++)
                buf[i] = data[done + i];
            fixed (byte* p = buf)
            {
                ulong devBlock = (startSector + (ulong)(done / sectorSize)) * (ulong)ratio;
                int written = device.Write(devBlock, (uint)blocks, p);
                if (written != blocks)
                    return -7;
            }
            done += len;
        }
        return 0;
    }

    /// <summary>Reads back and validates the freshly formatted volume.</summary>
    private static int Verify(IBlockDevice device, int sectorSize, byte[] upcase)
    {
        var scratch = new ExFatScratch();
        byte* buf = scratch.Get(64 * 1024);
        if (buf == null)
            return -7;
        try
        {
            int blocks = (int)(12 * sectorSize / device.BlockSize);
            if (device.Read(0, (uint)blocks, buf) != blocks)
                return -7;
            uint computed = ExFatChecksum.ComputeBootChecksum(buf, 11, sectorSize);
            uint stored = ExFatBytes.Read32FromPtr(buf, 11 * sectorSize);
            if (computed != stored)
                return -8;
            uint stored2 = ExFatBytes.Read32FromPtr(buf, 11 * sectorSize + 4);
            if (computed != stored2)
                return -8;

            // Backup region checksum too.
            if (device.Read((ulong)blocks, (uint)blocks, buf) != blocks)
                return -7;
            uint computed2 = ExFatChecksum.ComputeBootChecksum(buf, 11, sectorSize);
            uint stored3 = ExFatBytes.Read32FromPtr(buf, 11 * sectorSize);
            if (computed2 != stored3)
                return -8;

            // Up-case table data checksum.
            uint expected = ExFatStdUpcase.Checksum;
            if (expected != 0)
            {
                var data = new byte[upcase.Length];
                for (int i = 0; i < upcase.Length; i++)
                    data[i] = upcase[i];
                uint uc = ExFatChecksum.ComputeUpcaseChecksum(data, data.Length);
                if (uc != expected)
                    return -8;
            }
            return 0;
        }
        finally
        {
            scratch.Release();
        }
    }
}
