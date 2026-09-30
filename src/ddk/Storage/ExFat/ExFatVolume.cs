// NeutrinoOS Phase 10 - exFAT volume structure parser and low-level I/O.
//
// Implements Task 1 of Phase 10: main/backup boot region parsing and
// validation (including the boot checksum), FAT access, the allocation
// bitmap, the up-case table (with full compressed-form decompression),
// and cluster addressing into the cluster heap.

using System;
using NeutrinoOS.DDK.Kernel;

namespace NeutrinoOS.DDK.Storage.ExFat;

/// <summary>
/// Parsed and validated exFAT volume geometry with the low-level
/// cluster/FAT/bitmap accessors used by the driver, the formatter, and
/// fsck. Boot region validation follows the exFAT specification chapter 3;
/// a volume whose main boot region fails validation falls back to the
/// backup boot region when that one is valid.
/// </summary>
public unsafe class ExFatVolume
{
    /// <summary>Underlying block device.</summary>
    public IBlockDevice Device = null!;

    /// <summary>True when the volume is mounted read-only.</summary>
    public bool ReadOnly;

    // ---- Boot sector geometry (spec 3.1) ----
    /// <summary>PartitionOffset field (informational).</summary>
    public ulong PartitionOffset;
    /// <summary>VolumeLength field (sectors).</summary>
    public ulong VolumeLength;
    /// <summary>FatOffset field (sectors).</summary>
    public uint FatOffset;
    /// <summary>FatLength field (sectors, per FAT).</summary>
    public uint FatLength;
    /// <summary>ClusterHeapOffset field (sectors).</summary>
    public uint ClusterHeapOffset;
    /// <summary>ClusterCount field.</summary>
    public uint ClusterCount;
    /// <summary>FirstClusterOfRootDirectory field.</summary>
    public uint RootDirCluster;
    /// <summary>VolumeSerialNumber field.</summary>
    public uint VolumeSerial;
    /// <summary>FileSystemRevision field (0x0100 for 1.0).</summary>
    public ushort Revision;
    /// <summary>VolumeFlags field.</summary>
    public ushort VolumeFlags;
    /// <summary>BytesPerSectorShift field.</summary>
    public byte BytesPerSectorShift;
    /// <summary>SectorsPerClusterShift field.</summary>
    public byte SectorsPerClusterShift;
    /// <summary>NumberOfFats field (1 or 2).</summary>
    public byte NumberOfFats;
    /// <summary>PercentInUse field (0-100 or 0xFF).</summary>
    public byte PercentInUse;

    /// <summary>Volume sector size in bytes (2^BytesPerSectorShift).</summary>
    public uint SectorSize;
    /// <summary>Cluster size in bytes.</summary>
    public uint ClusterSize;
    /// <summary>Sectors per cluster.</summary>
    public uint SectorsPerCluster;
    /// <summary>Number of clusters from FirstCluster index onward (ClusterCount).</summary>
    public uint ClusterHeapClusters;

    // ---- Loaded metadata ----
    /// <summary>Up-cased character mapping (65536 entries, spec 7.2).</summary>
    public ushort[] Upcase = new ushort[65536];
    /// <summary>Raw (decompressed) up-case table length in bytes.</summary>
    public int UpcaseRawLength;
    /// <summary>Raw up-case table data (for checksum verification).</summary>
    public byte[] UpcaseRaw = new byte[0];
    /// <summary>Root directory cluster chain bitmap (in-memory allocation bitmap).</summary>
    public byte[] Bitmap = new byte[0];
    /// <summary>Allocation bitmap data length in bytes (spec 7.1.1).</summary>
    public ulong BitmapLength;
    /// <summary>Allocation bitmap first cluster.</summary>
    public uint BitmapCluster;
    /// <summary>Allocation bitmap stream flags (bit1 = NoFatChain).</summary>
    public byte BitmapFlags;
    /// <summary>Up-case table first cluster.</summary>
    public uint UpcaseCluster;
    /// <summary>Up-case table stream flags.</summary>
    public byte UpcaseFlags;
    /// <summary>Up-case table data length (compressed bytes on disk).</summary>
    public ulong UpcaseLength;
    /// <summary>Cached free cluster count (bitmap scan).</summary>
    public uint FreeClusterCount;
    /// <summary>Volume label ("" when none).</summary>
    public string VolumeLabel = "";
    /// <summary>True when the main boot region was invalid and the backup was used.</summary>
    public bool UsedBackupBoot;

    /// <summary>Device block size in bytes (device I/O granularity).</summary>
    public uint DeviceBlockSize;
    /// <summary>Shift from device blocks to volume sectors.</summary>
    private int _devToVolShift;

    /// <summary>Scratch I/O buffer provider (64 KiB; kernel pages or host pin).</summary>
    private readonly ExFatScratch _scratch = new ExFatScratch();
    /// <summary>Small bounce buffer for window I/O (8 KiB).</summary>
    private readonly ExFatScratch _tailScratch = new ExFatScratch();
    /// <summary>Scratch buffer pointer (allocated on first use).</summary>
    private byte* _io;
    /// <summary>Bounce buffer pointer (allocated on first use).</summary>
    private byte* _tail;
    private const int IoBytes = 64 * 1024;
    private const int TailBytes = 8 * 1024;

    /// <summary>Diagnostic: stage of the last failed Mount call.</summary>
    public string LastError = "";

    /// <summary>True when the boot region checksum validated (or was repaired).</summary>
    public bool BootChecksumOk = true;

    /// <summary>True when the MAIN boot region validated (false when the
    /// backup region had to be used or repair is pending).</summary>
    public bool MainBootOk = true;

    /// <summary>Lenient mode (fsck): proceed past boot checksum failures.</summary>
    private bool _lenient;

    /// <summary>Next free cluster search hint.</summary>
    public uint AllocHint = 2;

    // ======================= device I/O =======================

    /// <summary>
    /// Reads raw bytes at a volume sector offset into a buffer of
    /// <paramref name="count"/> bytes (must be sector-aligned).
    /// </summary>
    /// <param name="sector">Start volume sector.</param>
    /// <param name="count">Byte count (multiple of the volume sector size).</param>
    /// <param name="dst">Destination buffer.</param>
    /// <returns>0 on success, negative FileResult on failure.</returns>
    public int ReadBytes(ulong sector, int count, byte* dst)
    {
        EnsureIo();
        if (count < 0)
            return -7;
        int done = 0;
        while (done < count)
        {
            int chunk = count - done;
            if (chunk > IoBytes)
                chunk = IoBytes;
            int rc = ReadBytesDirect(sector + (ulong)(done / (int)SectorSize), chunk, dst + done);
            if (rc < 0)
                return rc;
            done += chunk;
        }
        return 0;
    }

    /// <summary>
    /// Writes raw bytes at a volume sector offset (sector-aligned).
    /// </summary>
    /// <param name="sector">Start volume sector.</param>
    /// <param name="count">Byte count (multiple of the volume sector size).</param>
    /// <param name="src">Source buffer.</param>
    /// <returns>0 on success, negative FileResult on failure.</returns>
    public int WriteBytes(ulong sector, int count, byte* src)
    {
        if (ReadOnly)
            return -8;
        if (count < 0)
            return -7;
        int done = 0;
        while (done < count)
        {
            int chunk = count - done;
            if (chunk > IoBytes)
                chunk = IoBytes;
            int rc = WriteBytesDirect(sector + (ulong)(done / (int)SectorSize), chunk, src + done);
            if (rc < 0)
                return rc;
            done += chunk;
        }
        return 0;
    }

    /// <summary>
    /// Reads up to 64 KiB starting at a volume sector (internal fast
    /// path). Byte counts smaller than one device block are rounded up;
    /// callers must leave one device block of slack in the destination.
    /// </summary>
    private int ReadBytesDirect(ulong sector, int count, byte* dst)
    {
        uint deviceBlocks = (uint)((count + (int)DeviceBlockSize - 1) / (int)DeviceBlockSize);
        ulong devLba = sector << _devToVolShift;
        int rc = Device.Read(devLba, deviceBlocks, dst);
        return rc == (int)deviceBlocks ? 0 : -7;
    }

    /// <summary>
    /// Writes up to 64 KiB starting at a volume sector (internal fast
    /// path). Byte counts smaller than one device block are rounded up;
    /// callers must leave one device block of slack in the source.
    /// </summary>
    private int WriteBytesDirect(ulong sector, int count, byte* src)
    {
        uint deviceBlocks = (uint)((count + (int)DeviceBlockSize - 1) / (int)DeviceBlockSize);
        ulong devLba = sector << _devToVolShift;
        int rc = Device.Write(devLba, deviceBlocks, src);
        return rc == (int)deviceBlocks ? 0 : -7;
    }

    /// <summary>Shift from volume sectors to device blocks.</summary>
    private int DevBlockShift()
    {
        int shift = 0;
        uint b = DeviceBlockSize;
        while (b > 1)
        {
            b >>= 1;
            shift++;
        }
        return shift;
    }

    /// <summary>Allocates the shared I/O buffer on first use.</summary>
    private void EnsureIo()
    {
        if (_io == null)
            _io = _scratch.Get(IoBytes);
        if (_tail == null)
            _tail = _tailScratch.Get(TailBytes);
    }

    /// <summary>Releases the scratch buffers (called on unmount).</summary>
    public void Release()
    {
        _scratch.Release();
        _tailScratch.Release();
        _io = null;
        _tail = null;
    }

    /// <summary>Small bounce buffer (8 KiB) for sector-aligned windows.</summary>
    private byte* Tail
    {
        get
        {
            EnsureIo();
            return _tail;
        }
    }

    /// <summary>Shared scratch buffer (null when allocation failed).</summary>
    public byte* Io
    {
        get
        {
            EnsureIo();
            return _io;
        }
    }

    // ======================= boot region =======================

    /// <summary>
    /// Reads and fully validates the volume's boot regions and loads
    /// FAT/bitmap/up-case metadata. Returns 0 on success.
    /// </summary>
    /// <param name="device">Block device to mount from.</param>
    /// <param name="readOnly">True for read-only mounts.</param>
    public int Mount(IBlockDevice device, bool readOnly)
    {
        return MountInternal(device, readOnly, false);
    }

    /// <summary>
    /// Lenient variant used by fsck: boot-region checksum mismatches are
    /// tolerated (recorded in <see cref="BootChecksumOk"/>) so the tree
    /// can still be checked and repaired. Returns 0 when the geometry is
    /// usable.
    /// </summary>
    /// <param name="device">Block device.</param>
    /// <param name="readOnly">True for read-only checks.</param>
    public int MountLenient(IBlockDevice device, bool readOnly)
    {
        return MountInternal(device, readOnly, true);
    }

    private int MountInternal(IBlockDevice device, bool readOnly, bool lenient)
    {
        _lenient = lenient;
        Device = device;
        ReadOnly = readOnly;
        DeviceBlockSize = device.BlockSize == 0 ? 512 : device.BlockSize;
        _devToVolShift = 0;
        EnsureIo();
        if (_io == null)
            return -7;

        // The boot sector is always in the first sector of the device as
        // seen through the device block size; read one device block first
        // to learn the volume sector size, then re-read properly.
        int rc = ReadBootSectorProbe();
        if (rc < 0)
        {
            LastError = "boot-probe";
            return rc;
        }

        uint need = SectorSize > DeviceBlockSize ? SectorSize : DeviceBlockSize;
        _devToVolShift = (int)(Log2(need) - Log2(DeviceBlockSize));

        rc = ParseBootRegion(0, false);
        if (rc != 0)
        {
            MainBootOk = false;
            // Spec: fall back to the backup boot region (sectors 12-23).
            rc = ParseBootRegion(ExFatConst.BootRegionSectors, true);
            if (rc != 0)
            {
                if (!lenient)
                {
                    LastError = "boot-region-main=" + rc.ToString();
                    return -7;   // FileResult.IoError-ish: corrupt volume
                }
                // fsck mode: accept the main region's fields without the
                // checksum so the tree can be validated and repaired.
                rc = ParseBootRegionLenient(0);
                if (rc != 0)
                {
                    LastError = "boot-region-lenient";
                    return -7;
                }
                BootChecksumOk = false;
            }
        }
        else
        {
            MainBootOk = true;
        }

        // Device block granularity must be <= volume sector size.
        if (DeviceBlockSize > SectorSize)
        {
            LastError = "block-size";
            return -7;
        }

        rc = LoadBitmap();
        if (rc != 0)
        {
            LastError = "bitmap=" + rc.ToString();
            return rc;
        }

        rc = LoadUpcase();
        if (rc != 0)
        {
            LastError = "upcase=" + rc.ToString();
            return rc;
        }

        CountFreeClusters();
        return 0;
    }

    /// <summary>Reads the first device block to detect the volume sector size.</summary>
    private int ReadBootSectorProbe()
    {
        int rc = Device.Read(0, 1, _io);
        if (rc != 1)
            return -7;
        byte shift = _io[108];
        if (shift < 9 || shift > 12)
            return -7;
        BytesPerSectorShift = shift;
        SectorSize = 1u << shift;
        return 0;
    }

    /// <summary>
    /// Parses and validates one boot region (main at sector 0 or backup
    /// at sector 12) including the boot checksum. Returns 0 on success.
    /// </summary>
    /// <param name="baseSector">Region start (0 or 12).</param>
    /// <param name="isBackup">True when parsing the backup region.</param>
    public int ParseBootRegion(ulong baseSector, bool isBackup)
    {
        EnsureIo();
        // Read 11 sectors + checksum sector (12 sectors total).
        int regionBytes = ExFatConst.BootRegionSectors * (int)SectorSize;
        if (regionBytes > IoBytes)
            return -7;
        int rc = ReadRegionBytes(baseSector, regionBytes, _io);
        if (rc != 0)
            return rc;

        rc = ValidateBootSector(_io, (int)SectorSize);
        if (rc != 0)
            return rc;

        // Boot checksum over the 11 content sectors, stored in sector 11.
        uint computed = ExFatChecksum.ComputeBootChecksum(_io, 11, (int)SectorSize);
        uint stored = ExFatBytes.Read32FromPtr(_io, 11 * (int)SectorSize);
        uint stored2 = ExFatBytes.Read32FromPtr(_io, 11 * (int)SectorSize + 4);
        if (computed != stored || computed != stored2)
            return -1;   // corrupt

        UsedBackupBoot = isBackup;
        ApplyBootFields(_io);
        BootChecksumOk = true;
        return 0;
    }

    /// <summary>
    /// Parses a boot sector without requiring a valid checksum (fsck
    /// recovery path). The fixed fields must still be plausible.
    /// </summary>
    /// <param name="baseSector">Region base (0 for main).</param>
    private int ParseBootRegionLenient(ulong baseSector)
    {
        EnsureIo();
        int sectorBytes = (int)SectorSize;
        if (sectorBytes == 0)
            sectorBytes = 512;
        int rc = ReadRegionBytes(baseSector, sectorBytes, _io);
        if (rc != 0)
            return rc;
        sectorBytes = (int)SectorSize;
        byte* b = _io;
        if (b[0] != 0xEB || b[1] != 0x76 || b[2] != 0x90)
            return -1;
        if (b[3] != 'E' || b[4] != 'X' || b[5] != 'F' || b[6] != 'A' ||
            b[7] != 'T' || b[8] != ' ' || b[9] != ' ' || b[10] != ' ')
            return -1;
        ApplyBootFields(b);
        return 0;
    }

    /// <summary>
    /// Rebuilds the boot regions. When <paramref name="fromBackup"/> is
    /// true the (valid) backup region is copied over the main region;
    /// otherwise the checksum sector is recomputed from the main
    /// region's contents and the whole region is mirrored to the backup.
    /// Used by fsck repair.
    /// </summary>
    /// <param name="fromBackup">Restore main from backup.</param>
    public int RepairBootRegion(bool fromBackup)
    {
        if (ReadOnly)
            return -8;
        EnsureIo();
        int regionBytes = ExFatConst.BootRegionSectors * (int)SectorSize;
        if (regionBytes > IoBytes)
            return -7;

        if (fromBackup)
        {
            int rc1 = ReadRegionBytes((ulong)ExFatConst.BootRegionSectors, regionBytes, _io);
            if (rc1 != 0)
                return rc1;
            int rc2 = WriteRegionBytes(0, regionBytes, _io);
            if (rc2 != 0)
                return rc2;
            MainBootOk = true;
            BootChecksumOk = true;
            return 0;
        }

        int rc = ReadRegionBytes(0, regionBytes, _io);
        if (rc != 0)
            return rc;

        uint checksum = ExFatChecksum.ComputeBootChecksum(_io, 11, (int)SectorSize);
        for (int i = 0; i < (int)SectorSize / 4; i++)
            ExFatBytes.Write32ToPtr(_io, 11 * (int)SectorSize + i * 4, checksum);

        rc = WriteRegionBytes(0, regionBytes, _io);
        if (rc != 0)
            return rc;
        rc = WriteRegionBytes((ulong)ExFatConst.BootRegionSectors, regionBytes, _io);
        if (rc != 0)
            return rc;
        MainBootOk = true;
        BootChecksumOk = true;
        return 0;
    }

    /// <summary>Writes boot region bytes handling device-block scaling.</summary>
    private int WriteRegionBytes(ulong baseSector, int bytes, byte* src)
    {
        int done = 0;
        while (done < bytes)
        {
            int chunk = bytes - done;
            if (chunk > IoBytes - (int)DeviceBlockSize)
                chunk = IoBytes - (int)DeviceBlockSize;
            int rc = WriteBytesDirect(baseSector + (ulong)(done / (int)SectorSize), chunk, src + done);
            if (rc < 0)
                return rc;
            done += chunk;
        }
        return 0;
    }

    /// <summary>
    /// Rewrites the up-case table entry's checksum field from the actual
    /// table data (fsck repair).
    /// </summary>
    public int RepairUpcaseChecksum()
    {
        if (ReadOnly)
            return -8;
        if (UpcaseRawLength == 0)
            return -7;
        uint actual = ExFatChecksum.ComputeUpcaseChecksum(UpcaseRaw, UpcaseRawLength);

        var dir = new ExFatDir(this);
        if (!dir.LoadRootDirectory())
            return -7;
        byte[] entry = new byte[ExFatConst.EntrySize];
        int idx = dir.FindPrimaryEntry(ExFatConst.EntryUpcaseTable, entry);
        if (idx < 0)
            return -7;

        int off = idx * ExFatConst.EntrySize;
        ExFatBytes.Write32(dir.Entries, off + 4, actual);
        if (dir.FlushDirectory() != 0)
            return -7;
        UpcaseChecksumOk = true;
        return 0;
    }

    /// <summary>Reads boot region bytes handling device-block scaling.</summary>
    private int ReadRegionBytes(ulong baseSector, int bytes, byte* dst)
    {
        int done = 0;
        while (done < bytes)
        {
            int chunk = bytes - done;
            if (chunk > IoBytes)
                chunk = IoBytes;
            int rc = ReadBytesDirect(baseSector + (ulong)(done / (int)SectorSize), chunk, dst + done);
            if (rc < 0)
                return rc;
            done += chunk;
        }
        return 0;
    }

    /// <summary>Validates the fixed boot sector fields (spec 3.1).</summary>
    private int ValidateBootSector(byte* b, int sectorSize)
    {
        if (b[0] != 0xEB || b[1] != 0x76 || b[2] != 0x90)
            return -1;
        if (b[3] != 'E' || b[4] != 'X' || b[5] != 'F' || b[6] != 'A' || b[7] != 'T' ||
            b[8] != ' ' || b[9] != ' ' || b[10] != ' ')
            return -1;
        for (int i = 11; i < 64; i++)
            if (b[i] != 0)
                return -1;
        if (sectorSize < 512 || b[510] != 0x55 || b[511] != 0xAA)
            return -1;

        byte sectorShift = b[108];
        byte clusterShift = b[109];
        if (sectorShift < 9 || sectorShift > 12)
            return -1;
        if (clusterShift > 25)
            return -1;
        if (sectorShift + clusterShift > 25)
            return -1;
        if (b[110] < 1 || b[110] > 2)
            return -1;
        if (b[112] > 100 && b[112] != 0xFF)
            return -1;
        return 0;
    }

    /// <summary>Copies the parsed geometry from the boot buffer.</summary>
    private void ApplyBootFields(byte* b)
    {
        PartitionOffset = ReadU64(b, 64);
        VolumeLength = ReadU64(b, 72);
        FatOffset = ReadU32(b, 80);
        FatLength = ReadU32(b, 84);
        ClusterHeapOffset = ReadU32(b, 88);
        ClusterCount = ReadU32(b, 92);
        RootDirCluster = ReadU32(b, 96);
        VolumeSerial = ReadU32(b, 100);
        Revision = (ushort)(b[104] | (b[105] << 8));
        VolumeFlags = (ushort)(b[106] | (b[107] << 8));
        BytesPerSectorShift = b[108];
        SectorsPerClusterShift = b[109];
        NumberOfFats = b[110];
        PercentInUse = b[112];

        SectorSize = 1u << BytesPerSectorShift;
        SectorsPerCluster = 1u << SectorsPerClusterShift;
        ClusterSize = SectorSize * SectorsPerCluster;
        ClusterHeapClusters = ClusterCount;

        // Structural sanity (spec 3.1 constraints).
        if (FatOffset < 24)
            FatOffset = 24;
        if (RootDirCluster < ExFatConst.FirstCluster)
            RootDirCluster = ExFatConst.FirstCluster;
    }

    private static uint ReadU32(byte* b, int off) =>
        (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));

    private static ulong ReadU64(byte* b, int off) =>
        ReadU32(b, off) | ((ulong)ReadU32(b, off + 4) << 32);

    private static uint Log2(uint v)
    {
        uint r = 0;
        while (v > 1)
        {
            v >>= 1;
            r++;
        }
        return r;
    }

    // ======================= clusters and FAT =======================

    /// <summary>Converts a cluster index to its first volume sector.</summary>
    /// <param name="cluster">Cluster index (>= 2).</param>
    public ulong ClusterToSector(uint cluster) =>
        ClusterHeapOffset + (ulong)(cluster - ExFatConst.FirstCluster) * SectorsPerCluster;

    /// <summary>Reads a full cluster's data into <paramref name="dst"/>.</summary>
    /// <param name="cluster">Cluster index.</param>
    /// <param name="dst">Destination buffer (ClusterSize bytes).</param>
    public int ReadCluster(uint cluster, byte* dst)
    {
        int left = (int)ClusterSize;
        ulong sector = ClusterToSector(cluster);
        while (left > 0)
        {
            int chunk = left > IoBytes ? IoBytes : left;
            int rc = ReadBytesDirect(sector, chunk, dst);
            if (rc < 0)
                return rc;
            dst += chunk;
            sector += (ulong)(chunk / (int)SectorSize);
            left -= chunk;
        }
        return 0;
    }

    /// <summary>Writes a full cluster's data from <paramref name="src"/>.</summary>
    /// <param name="cluster">Cluster index.</param>
    /// <param name="src">Source buffer (ClusterSize bytes).</param>
    public int WriteCluster(uint cluster, byte* src)
    {
        if (ReadOnly)
            return -8;
        int left = (int)ClusterSize;
        ulong sector = ClusterToSector(cluster);
        while (left > 0)
        {
            int chunk = left > IoBytes ? IoBytes : left;
            int rc = WriteBytesDirect(sector, chunk, src);
            if (rc < 0)
                return rc;
            src += chunk;
            sector += (ulong)(chunk / (int)SectorSize);
            left -= chunk;
        }
        return 0;
    }

    /// <summary>Fills a cluster with zeroes.</summary>
    /// <param name="cluster">Cluster index.</param>
    public int ZeroCluster(uint cluster)
    {
        if (ReadOnly)
            return -8;
        byte* buf = Io;
        int left = (int)ClusterSize;
        ulong sector = ClusterToSector(cluster);
        while (left > 0)
        {
            int chunk = left > IoBytes ? IoBytes : left;
            for (int i = 0; i < chunk; i++)
                buf[i] = 0;
            int rc = WriteBytesDirect(sector, chunk, buf);
            if (rc < 0)
                return rc;
            sector += (ulong)(chunk / (int)SectorSize);
            left -= chunk;
        }
        return 0;
    }

    /// <summary>Sector offset of the FAT entry for a cluster.</summary>
    private ulong FatEntrySector(uint cluster) =>
        FatOffset + (ulong)(cluster >> (int)(BytesPerSectorShift - 2));

    /// <summary>Byte offset of the FAT entry inside its sector.</summary>
    private int FatEntryOffset(uint cluster) => (int)(cluster & ((SectorSize >> 2) - 1)) * 4;

    /// <summary>Reads the FAT entry of a cluster.</summary>
    /// <param name="cluster">Cluster index.</param>
    public uint FatGet(uint cluster)
    {
        byte* buf = Io;
        if (buf == null)
            return 0;
        if (ReadBytesDirect(FatEntrySector(cluster), (int)SectorSize, buf) < 0)
            return 0;
        return ExFatBytes.Read32FromPtr(buf, FatEntryOffset(cluster));
    }

    /// <summary>
    /// Writes the FAT entry of a cluster into the active FAT (and the
    /// second FAT when present, keeping both consistent).
    /// </summary>
    /// <param name="cluster">Cluster index.</param>
    /// <param name="value">New FAT entry value.</param>
    public int FatSet(uint cluster, uint value)
    {
        if (ReadOnly)
            return -8;
        byte* buf = Io;
        if (buf == null)
            return -7;
        ulong sector = FatEntrySector(cluster);
        int off = FatEntryOffset(cluster);

        int fats = NumberOfFats == 2 ? 2 : 1;
        for (int f = 0; f < fats; f++)
        {
            ulong fatSector = sector + (ulong)f * FatLength;
            if (ReadBytesDirect(fatSector, (int)SectorSize, buf) < 0)
                return -7;
            ExFatBytes.Write32ToPtr(buf, off, value);
            if (WriteBytesDirect(fatSector, (int)SectorSize, buf) < 0)
                return -7;
        }
        return 0;
    }

    /// <summary>Walks to the next cluster of a chain (with loop guard).</summary>
    /// <param name="cluster">Current cluster.</param>
    public uint FatNext(uint cluster)
    {
        uint next = FatGet(cluster);
        if (next == ExFatConst.FatEndOfChain || next == ExFatConst.FatBadCluster)
            return 0;
        if (next < ExFatConst.FirstCluster || next >= ExFatConst.FirstCluster + ClusterCount)
            return 0;
        return next;
    }

    // ======================= allocation bitmap =======================

    /// <summary>True when the allocation bitmap bit of a cluster is set.</summary>
    /// <param name="cluster">Cluster index (>= 2).</param>
    public bool BitmapIsSet(uint cluster)
    {
        ulong byteIndex = (ulong)(cluster - ExFatConst.FirstCluster) >> 3;
        if (byteIndex >= BitmapLength)
            return true;   // out of range: treat as allocated
        int bit = (int)((cluster - ExFatConst.FirstCluster) & 7);
        return (Bitmap[(int)byteIndex] & (1 << bit)) != 0;
    }

    /// <summary>Sets/clears an allocation bitmap bit in memory.</summary>
    /// <param name="cluster">Cluster index.</param>
    /// <param name="allocated">New bit state.</param>
    public void BitmapSet(uint cluster, bool allocated)
    {
        ulong byteIndex = (ulong)(cluster - ExFatConst.FirstCluster) >> 3;
        if (byteIndex >= BitmapLength)
            return;
        int bit = (int)((cluster - ExFatConst.FirstCluster) & 7);
        byte mask = (byte)(1 << bit);
        bool was = (Bitmap[(int)byteIndex] & mask) != 0;
        if (was == allocated)
            return;
        if (allocated)
        {
            Bitmap[(int)byteIndex] |= mask;
            if (FreeClusterCount > 0)
                FreeClusterCount--;
        }
        else
        {
            Bitmap[(int)byteIndex] = (byte)(Bitmap[(int)byteIndex] & ~mask);
            FreeClusterCount++;
        }
    }

    /// <summary>
    /// Allocates one cluster: finds a free bit, sets it, terminates the
    /// FAT chain, and returns the cluster index (0 when full).
    /// </summary>
    public uint AllocateCluster()
    {
        if (ReadOnly)
            return 0;
        uint c = AllocateClusterBit();
        if (c == 0)
            return 0;
        FatSet(c, ExFatConst.FatEndOfChain);
        return c;
    }

    /// <summary>
    /// Allocates <paramref name="wanted"/> contiguous clusters for a
    /// NoFatChain stream. Returns the first cluster (0 when no run of
    /// that length exists).
    /// </summary>
    /// <param name="wanted">Number of contiguous clusters.</param>
    public uint AllocateContiguous(uint wanted)
    {
        if (ReadOnly || wanted == 0)
            return 0;
        uint runStart = 0;
        uint run = 0;
        uint last = ExFatConst.FirstCluster + ClusterCount;
        for (uint c = AllocHint; c < last; c++)
        {
            if (!BitmapIsSet(c))
            {
                if (run == 0)
                    runStart = c;
                run++;
                if (run >= wanted)
                {
                    for (uint a = runStart; a < runStart + wanted; a++)
                        BitmapSet(a, true);
                    AllocHint = runStart + wanted;
                    return runStart;
                }
            }
            else
            {
                run = 0;
            }
        }
        // Wrap-around search from the start of the heap.
        for (uint c = ExFatConst.FirstCluster; c < AllocHint && c < last; c++)
        {
            if (!BitmapIsSet(c))
            {
                if (run == 0)
                    runStart = c;
                run++;
                if (run >= wanted)
                {
                    for (uint a = runStart; a < runStart + wanted; a++)
                        BitmapSet(a, true);
                    AllocHint = runStart + wanted;
                    return runStart;
                }
            }
            else
            {
                run = 0;
            }
        }
        return 0;
    }

    /// <summary>Allocates a single free bit and returns the cluster index.</summary>
    private uint AllocateClusterBit()
    {
        uint last = ExFatConst.FirstCluster + ClusterCount;
        for (uint c = AllocHint; c < last; c++)
        {
            if (!BitmapIsSet(c))
            {
                BitmapSet(c, true);
                AllocHint = c + 1;
                return c;
            }
        }
        for (uint c = ExFatConst.FirstCluster; c < AllocHint; c++)
        {
            if (!BitmapIsSet(c))
            {
                BitmapSet(c, true);
                AllocHint = c + 1;
                return c;
            }
        }
        return 0;
    }

    /// <summary>Frees a cluster and its FAT entry.</summary>
    /// <param name="cluster">Cluster index.</param>
    public void FreeCluster(uint cluster)
    {
        BitmapSet(cluster, false);
        FatSet(cluster, 0);
    }

    /// <summary>Counts free clusters from the in-memory bitmap.</summary>
    public void CountFreeClusters()
    {
        uint free = 0;
        uint clusters = ClusterCount;
        for (uint i = 0; i < clusters; i++)
        {
            ulong byteIndex = (ulong)i >> 3;
            if (byteIndex >= BitmapLength)
                break;
            int bit = (int)(i & 7);
            if ((Bitmap[(int)byteIndex] & (1 << bit)) == 0)
                free++;
        }
        FreeClusterCount = free;
    }

    /// <summary>Loads the allocation bitmap stream into memory.</summary>
    private int LoadBitmap()
    {
        var dir = new ExFatDir(this);
        if (!dir.LoadRootDirectory())
            return -7;

        byte[] entry = new byte[ExFatConst.EntrySize];
        int idx = dir.FindPrimaryEntry(ExFatConst.EntryAllocationBitmap, entry);
        if (idx < 0)
            return -7;

        BitmapCluster = ExFatBytes.Read32(entry, 20);
        BitmapLength = ExFatBytes.Read64(entry, 24);
        BitmapFlags = 0;

        if (BitmapLength == 0 || BitmapLength > (ulong)8 * 1024 * 1024)
            BitmapLength = ((ulong)ClusterCount + 7) / 8;

        Bitmap = new byte[(int)BitmapLength];
        if (BitmapCluster == 0 || BitmapCluster < ExFatConst.FirstCluster)
            return -7;

        // The allocation bitmap stream is contiguous (spec 7.1.1); fall
        // back to the FAT chain when the first cluster is chained, which
        // some formatters emit.
        int rc = DirReadStream(BitmapCluster, true, Bitmap, 0, (int)BitmapLength);
        if (rc != 0)
            rc = DirReadStream(BitmapCluster, false, Bitmap, 0, (int)BitmapLength);
        return rc;
    }

    /// <summary>
    /// Reads a stream (FAT chain or contiguous) into a managed buffer,
    /// in sector-aligned windows so any cluster size is safe.
    /// </summary>
    /// <param name="firstCluster">Stream first cluster.</param>
    /// <param name="noFatChain">True for contiguous streams.</param>
    /// <param name="dst">Destination array.</param>
    /// <param name="dstOffset">Offset into <paramref name="dst"/>.</param>
    /// <param name="length">Byte count to read.</param>
    public int DirReadStream(uint firstCluster, bool noFatChain, byte[] dst, int dstOffset, int length)
    {
        uint cluster = firstCluster;
        int done = 0;
        int guard = 0;
        while (done < length)
        {
            if (cluster == 0 || cluster < ExFatConst.FirstCluster ||
                cluster >= ExFatConst.FirstCluster + ClusterCount)
                return -7;
            int inCluster = done % (int)ClusterSize;
            int chunk = (int)ClusterSize - inCluster;
            if (chunk > length - done)
                chunk = length - done;
            if (chunk > 16 * 1024)
                chunk = 16 * 1024;

            int rc = ReadClusterWindow(cluster, inCluster, chunk, dst, dstOffset + done);
            if (rc != 0)
                return rc;
            done += chunk;

            if (done < length && inCluster + chunk >= (int)ClusterSize)
            {
                cluster = noFatChain ? cluster + 1 : FatNext(cluster);
                if (cluster == 0)
                    return -7;
            }
            else if (done < length && chunk == 16 * 1024)
            {
                // Partial cluster consumed via multiple windows: the
                // cluster advances only when the cluster is exhausted;
                // the loop recomputes inCluster from done, so nothing to do.
            }
            if (++guard > 0x1000000)
                return -7;   // chain loop guard
        }
        return 0;
    }

    /// <summary>Reads the first <paramref name="bytes"/> of a cluster.</summary>
    private int ReadClusterPart(uint cluster, byte* dst, int bytes)
    {
        ulong sector = ClusterToSector(cluster);
        int left = bytes;
        while (left > 0)
        {
            // Keep one device block of slack in the shared buffer for
            // the rounded-up transfer of a partial final block.
            int max = IoBytes - (int)DeviceBlockSize;
            int chunk = left > max ? max : left;
            int rc = ReadBytesDirect(sector, chunk, dst);
            if (rc < 0)
                return rc;
            dst += chunk;
            sector += (ulong)(chunk / (int)SectorSize);
            left -= chunk;
        }
        return 0;
    }

    /// <summary>
    /// Writes a managed buffer into a stream (FAT or contiguous) in
    /// sector-aligned windows.
    /// </summary>
    /// <param name="firstCluster">Stream first cluster.</param>
    /// <param name="noFatChain">True for contiguous streams.</param>
    /// <param name="src">Source array.</param>
    /// <param name="srcOffset">Offset into <paramref name="src"/>.</param>
    /// <param name="length">Byte count to write.</param>
    public int DirWriteStream(uint firstCluster, bool noFatChain, byte[] src, int srcOffset, int length)
    {
        if (ReadOnly)
            return -8;
        uint cluster = firstCluster;
        int done = 0;
        int guard = 0;
        while (done < length)
        {
            if (cluster == 0 || cluster < ExFatConst.FirstCluster ||
                cluster >= ExFatConst.FirstCluster + ClusterCount)
                return -7;
            int inCluster = done % (int)ClusterSize;
            int chunk = (int)ClusterSize - inCluster;
            if (chunk > length - done)
                chunk = length - done;
            if (chunk > 16 * 1024)
                chunk = 16 * 1024;

            int rc = WriteClusterWindow(cluster, inCluster, chunk, src, srcOffset + done);
            if (rc != 0)
                return rc;
            done += chunk;

            if (done < length && inCluster + chunk >= (int)ClusterSize)
            {
                cluster = noFatChain ? cluster + 1 : FatNext(cluster);
                if (cluster == 0)
                    return -7;
            }
            if (++guard > 0x1000000)
                return -7;
        }
        return 0;
    }

    /// <summary>
    /// Reads a byte range inside one cluster through sector-aligned
    /// windows (bounced via the small tail buffer so any sector size
    /// and any in-cluster offset is handled).
    /// </summary>
    /// <param name="cluster">Cluster index.</param>
    /// <param name="inCluster">Byte offset inside the cluster.</param>
    /// <param name="length">Byte count (&lt;= 16 KiB).</param>
    /// <param name="dst">Destination array.</param>
    /// <param name="dstOffset">Offset into <paramref name="dst"/>.</param>
    public int ReadClusterWindow(uint cluster, int inCluster, int length, byte[] dst, int dstOffset)
    {
        byte* t = Tail;
        if (t == null)
            return -7;
        int inSector = inCluster % (int)SectorSize;
        ulong sector = ClusterToSector(cluster) + (ulong)(inCluster / (int)SectorSize);
        int produced = 0;
        while (produced < length)
        {
            int rc = ReadBytesDirect(sector, (int)SectorSize, t);
            if (rc != 0)
                return rc;
            int avail = (int)SectorSize - inSector;
            if (avail > length - produced)
                avail = length - produced;
            for (int i = 0; i < avail; i++)
                dst[dstOffset + produced + i] = t[inSector + i];
            produced += avail;
            sector++;
            inSector = 0;
        }
        return 0;
    }

    /// <summary>
    /// Writes a byte range inside one cluster through sector-aligned
    /// read-modify-write windows.
    /// </summary>
    /// <param name="cluster">Cluster index.</param>
    /// <param name="inCluster">Byte offset inside the cluster.</param>
    /// <param name="length">Byte count (&lt;= 16 KiB).</param>
    /// <param name="src">Source array.</param>
    /// <param name="srcOffset">Offset into <paramref name="src"/>.</param>
    public int WriteClusterWindow(uint cluster, int inCluster, int length, byte[] src, int srcOffset)
    {
        if (ReadOnly)
            return -8;
        byte* t = Tail;
        if (t == null)
            return -7;
        int inSector = inCluster % (int)SectorSize;
        ulong sector = ClusterToSector(cluster) + (ulong)(inCluster / (int)SectorSize);
        int done = 0;
        while (done < length)
        {
            int rc = ReadBytesDirect(sector, (int)SectorSize, t);
            if (rc != 0)
                return rc;
            int avail = (int)SectorSize - inSector;
            if (avail > length - done)
                avail = length - done;
            for (int i = 0; i < avail; i++)
                t[inSector + i] = src[srcOffset + done + i];
            rc = WriteBytesDirect(sector, (int)SectorSize, t);
            if (rc != 0)
                return rc;
            done += avail;
            sector++;
            inSector = 0;
        }
        return 0;
    }

    /// <summary>Writes the first <paramref name="bytes"/> of a cluster.</summary>
    private int WriteClusterPart(uint cluster, byte* src, int bytes)
    {
        ulong sector = ClusterToSector(cluster);
        int left = bytes;
        while (left > 0)
        {
            // Keep one device block of slack for rounded transfers; the
            // rounded write re-sends the bytes past <paramref name="bytes"/>
            // from the shared buffer (harmless: same data region).
            int max = IoBytes - (int)DeviceBlockSize;
            int chunk = left > max ? max : left;
            int rc = WriteBytesDirect(sector, chunk, src);
            if (rc < 0)
                return rc;
            src += chunk;
            sector += (ulong)(chunk / (int)SectorSize);
            left -= chunk;
        }
        return 0;
    }

    // ======================= up-case table =======================

    /// <summary>
    /// Loads and decompresses the up-case table (spec 7.2) and verifies
    /// its checksum.
    /// </summary>
    private int LoadUpcase()
    {
        var dir = new ExFatDir(this);
        if (!dir.LoadRootDirectory())
            return -7;

        byte[] entry = new byte[ExFatConst.EntrySize];
        int idx = dir.FindPrimaryEntry(ExFatConst.EntryUpcaseTable, entry);
        if (idx < 0)
            return -7;

        uint expectedChecksum = ExFatBytes.Read32(entry, 4);
        UpcaseCluster = ExFatBytes.Read32(entry, 20);
        UpcaseLength = ExFatBytes.Read64(entry, 24);
        UpcaseFlags = 0;

        if (UpcaseCluster < ExFatConst.FirstCluster || UpcaseLength == 0 ||
            UpcaseLength > 1024 * 1024)
            return -7;

        UpcaseRaw = new byte[(int)UpcaseLength];
        // The up-case table stream is contiguous (spec 7.2).
        int rc = DirReadStream(UpcaseCluster, true, UpcaseRaw, 0, (int)UpcaseLength);
        if (rc != 0)
            rc = DirReadStream(UpcaseCluster, false, UpcaseRaw, 0, (int)UpcaseLength);
        if (rc != 0)
            return rc;

        uint actual = ExFatChecksum.ComputeUpcaseChecksum(UpcaseRaw, (int)UpcaseLength);
        bool checksumOk = (actual == expectedChecksum);

        rc = DecompressUpcase(UpcaseRaw, (int)UpcaseLength);
        if (rc != 0)
            return -7;

        // A checksum mismatch on a read-only mount invalidates the
        // volume (spec 7.2.4); read-write mounts keep going so fsck can
        // repair, but the mismatch is reported to the caller.
        UpcaseChecksumOk = checksumOk;
        return 0;
    }

    /// <summary>True when the on-disk up-case checksum matched.</summary>
    public bool UpcaseChecksumOk = true;

    /// <summary>
    /// Decompresses the compressed up-case table into
    /// <see cref="Upcase"/> (65536 entries).
    /// </summary>
    /// <param name="table">Compressed table bytes.</param>
    /// <param name="length">Compressed length in bytes.</param>
    public int DecompressUpcase(byte[] table, int length)
    {
        int pos = 0;
        int outIndex = 0;
        while (pos + 1 < length && outIndex < 65536)
        {
            ushort value = (ushort)(table[pos] | (table[pos + 1] << 8));
            pos += 2;
            // 0xFFFF starts a compressed run only when the count and
            // mapped value follow; the canonical table ends with a bare
            // 0xFFFF that maps U+FFFF to itself (spec 7.2.2).
            if (value == 0xFFFF && pos + 3 < length)
            {
                ushort count = (ushort)(table[pos] | (table[pos + 1] << 8));
                ushort mapped = (ushort)(table[pos + 2] | (table[pos + 3] << 8));
                pos += 4;
                for (int i = 0; i < count && outIndex < 65536; i++)
                    Upcase[outIndex++] = mapped;
            }
            else
            {
                Upcase[outIndex++] = value;
            }
        }
        // Entries beyond the table default to identity mapping.
        while (outIndex < 65536)
        {
            Upcase[outIndex] = (ushort)outIndex;
            outIndex++;
        }
        UpcaseRawLength = length;
        return 0;
    }

    /// <summary>
    /// Up-cases a name through the loaded table (used for case-insensitive
    /// comparison and the file name hash).
    /// </summary>
    /// <param name="name">Source name.</param>
    public string UpcaseName(string name)
    {
        var chars = new char[name.Length];
        for (int i = 0; i < name.Length; i++)
            chars[i] = (char)Upcase[name[i]];
        return new string(chars);
    }

    // ======================= volume flags =======================

    /// <summary>
    /// Writes the VolumeDirty flag on the main boot sector (and the
    /// backup). VolumeFlags is excluded from the boot checksum, so no
    /// checksum update is needed.
    /// </summary>
    /// <param name="dirty">New flag state.</param>
    public int SetVolumeDirty(bool dirty)
    {
        if (ReadOnly)
            return -8;
        ushort flags = VolumeFlags;
        if (dirty)
            flags |= ExFatConst.VolumeFlagVolumeDirty;
        else
            flags = (ushort)(flags & ~ExFatConst.VolumeFlagVolumeDirty);
        return WriteVolumeFlags(flags);
    }

    /// <summary>Writes raw VolumeFlags (main + backup boot sectors).</summary>
    /// <param name="flags">New flags value.</param>
    public int WriteVolumeFlags(ushort flags)
    {
        byte* buf = Io;
        if (buf == null)
            return -7;

        // Main boot sector flags at offset 106.
        if (ReadBytesDirect(0, (int)SectorSize, buf) < 0)
            return -7;
        buf[106] = (byte)flags;
        buf[107] = (byte)(flags >> 8);
        if (WriteBytesDirect(0, (int)SectorSize, buf) < 0)
            return -7;

        // Backup boot sector at sector 12.
        if (ReadBytesDirect(12, (int)SectorSize, buf) < 0)
            return -7;
        buf[106] = (byte)flags;
        buf[107] = (byte)(flags >> 8);
        if (WriteBytesDirect(12, (int)SectorSize, buf) < 0)
            return -7;

        VolumeFlags = flags;
        return 0;
    }

    /// <summary>Writes the PercentInUse field (main + backup boot sectors).</summary>
    /// <param name="percent">0-100 or 0xFF.</param>
    public int WritePercentInUse(byte percent)
    {
        if (ReadOnly)
            return -8;
        byte* buf = Io;
        if (buf == null)
            return -7;

        if (ReadBytesDirect(0, (int)SectorSize, buf) < 0)
            return -7;
        buf[112] = percent;
        if (WriteBytesDirect(0, (int)SectorSize, buf) < 0)
            return -7;

        if (ReadBytesDirect(12, (int)SectorSize, buf) < 0)
            return -7;
        buf[112] = percent;
        if (WriteBytesDirect(12, (int)SectorSize, buf) < 0)
            return -7;

        PercentInUse = percent;
        return 0;
    }

    /// <summary>Flushes the in-memory allocation bitmap to the cluster heap.</summary>
    public int FlushBitmap()
    {
        if (ReadOnly || Bitmap.Length == 0)
            return 0;
        // The allocation bitmap stream is contiguous (spec 7.1.1).
        return DirWriteStream(BitmapCluster, true, Bitmap, 0, Bitmap.Length);
    }
}
