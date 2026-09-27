// NeutrinoOS Phase 10 - exFAT on-disk structures and algorithms.
//
// Constants, field offsets, and the checksum algorithms defined by the
// Microsoft exFAT specification. Everything here is byte-level so it can
// be used both by the driver and by the disk tooling (mkexfat, fsck).
//
// Reference: https://learn.microsoft.com/en-us/windows/win32/fileio/exfat-specification

using System;

namespace ProtonOS.DDK.Storage.ExFat;

/// <summary>exFAT constant field values (spec chapter 3-7).</summary>
public static class ExFatConst
{
    /// <summary>Boot sector JumpBoot field (EB 76 90).</summary>
    public const uint JumpBoot = 0x9076EB;

    /// <summary>"EXFAT   " filesystem name in the boot sector.</summary>
    public const ulong FileSystemName = 0x2020205441465845;

    /// <summary>Boot signature word (55 AA) at offset 510.</summary>
    public const ushort BootSignature = 0xAA55;

    /// <summary>First cluster index in the cluster heap.</summary>
    public const uint FirstCluster = 2;

    /// <summary>FAT entry: cluster 0 (media type).</summary>
    public const uint FatEntry0 = 0xFFFFFFF8;

    /// <summary>FAT entry: cluster 1 (end of chain marker).</summary>
    public const uint FatEntry1 = 0xFFFFFFFF;

    /// <summary>FAT entry value marking the end of an allocation chain.</summary>
    public const uint FatEndOfChain = 0xFFFFFFFF;

    /// <summary>FAT entry value marking a bad cluster.</summary>
    public const uint FatBadCluster = 0xFFFFFFF7;

    // ---- Directory entry types (spec 6.1) ----

    /// <summary>End of directory marker.</summary>
    public const byte EntryEnd = 0x00;

    /// <summary>Allocation Bitmap (critical primary).</summary>
    public const byte EntryAllocationBitmap = 0x81;

    /// <summary>Up-case Table (critical primary).</summary>
    public const byte EntryUpcaseTable = 0x82;

    /// <summary>Volume Label (critical primary).</summary>
    public const byte EntryVolumeLabel = 0x83;

    /// <summary>File (critical primary).</summary>
    public const byte EntryFile = 0x85;

    /// <summary>Volume GUID (benign primary).</summary>
    public const byte EntryVolumeGuid = 0xA0;

    /// <summary>TexFAT Padding (benign primary).</summary>
    public const byte EntryTexFatPadding = 0xA1;

    /// <summary>Stream Extension (critical secondary).</summary>
    public const byte EntryStreamExtension = 0xC0;

    /// <summary>File Name (critical secondary).</summary>
    public const byte EntryFileName = 0xC1;

    /// <summary>Windows CE ACL (benign secondary).</summary>
    public const byte EntryWinCeAcl = 0xC2;

    /// <summary>Marks a directory entry as deleted (bit 7 cleared).</summary>
    public const byte EntryDeletedMask = 0x7F;

    // ---- File attribute bits (spec 6.3.1) ----

    /// <summary>Read-only attribute bit.</summary>
    public const ushort AttrReadOnly = 0x0001;

    /// <summary>Hidden attribute bit.</summary>
    public const ushort AttrHidden = 0x0002;

    /// <summary>System attribute bit.</summary>
    public const ushort AttrSystem = 0x0004;

    /// <summary>Directory attribute bit.</summary>
    public const ushort AttrDirectory = 0x0010;

    /// <summary>Archive attribute bit.</summary>
    public const ushort AttrArchive = 0x0020;

    // ---- Stream extension flags (spec 7.1.2) ----

    /// <summary>AllocationPossible secondary flag.</summary>
    public const byte StreamFlagAllocationPossible = 0x01;

    /// <summary>NoFatChain secondary flag (clusters are contiguous).</summary>
    public const byte StreamFlagNoFatChain = 0x02;

    // ---- Volume flags (spec 3.1) ----

    /// <summary>ActiveFat volume flag (0 = first FAT).</summary>
    public const ushort VolumeFlagActiveFat = 0x0001;

    /// <summary>VolumeDirty volume flag.</summary>
    public const ushort VolumeFlagVolumeDirty = 0x0002;

    /// <summary>MediaFailure volume flag.</summary>
    public const ushort VolumeFlagMediaFailure = 0x0004;

    /// <summary>ClearToZero volume flag.</summary>
    public const ushort VolumeFlagClearToZero = 0x0008;

    /// <summary>PercentInUse "not available" value.</summary>
    public const byte PercentInUseUnknown = 0xFF;

    /// <summary>Maximum number of UTF-16 characters in a file name.</summary>
    public const int MaxNameChars = 255;

    /// <summary>Maximum number of characters in a volume label.</summary>
    public const int MaxLabelChars = 11;

    /// <summary>Bytes per directory entry.</summary>
    public const int EntrySize = 32;

    /// <summary>Maximum number of secondary entries per file set (255).</summary>
    public const int MaxSecondaryCount = 255;

    /// <summary>Sector size of the main/backup boot regions (fixed by spec).</summary>
    public const int BootRegionSectors = 12;
}

/// <summary>
/// Byte-level checksum and hash algorithms from the exFAT specification.
/// All routines work on raw buffers so they can be shared between the
/// driver, the formatter, and fsck.
/// </summary>
public static unsafe class ExFatChecksum
{
    /// <summary>
    /// Boot region checksum (spec 3.4): 32-bit rotate-right-add over
    /// <paramref name="sectorCount"/> sectors starting at
    /// <paramref name="data"/>, skipping VolumeFlags (offsets 106-107)
    /// and PercentInUse (offset 112) in the first sector.
    /// </summary>
    /// <param name="data">Sector buffer (at least sectorCount * sectorSize bytes).</param>
    /// <param name="sectorCount">Number of sectors to include (11 for a boot region).</param>
    /// <param name="sectorSize">Sector size in bytes.</param>
    public static uint ComputeBootChecksum(byte* data, int sectorCount, int sectorSize)
    {
        uint checksum = 0;
        int total = sectorCount * sectorSize;
        for (int i = 0; i < total; i++)
        {
            if (i == 106 || i == 107 || i == 112)
                continue;
            checksum = (checksum << 31) | (checksum >> 1);
            checksum += data[i];
        }
        return checksum;
    }

    /// <summary>
    /// Directory entry set checksum (spec 6.3.4): 16-bit rotate-right-add
    /// over all entries of the set; the SetChecksum field (bytes 2-3 of
    /// the first entry) is skipped.
    /// </summary>
    /// <param name="entries">Entry set bytes (32 bytes per entry).</param>
    /// <param name="entryCount">Number of 32-byte entries in the set.</param>
    public static ushort ComputeSetChecksum(byte* entries, int entryCount)
    {
        ushort checksum = 0;
        int total = entryCount * ExFatConst.EntrySize;
        for (int i = 0; i < total; i++)
        {
            if (i == 2 || i == 3)
                continue;
            checksum = (ushort)(((checksum << 15) | (checksum >> 1)) + entries[i]);
        }
        return checksum;
    }

    /// <summary>
    /// File name hash (spec 7.2.5): 16-bit rotate-right-add over the
    /// up-cased name, low byte first for each UTF-16 code unit.
    /// </summary>
    /// <param name="upcasedName">Up-cased name (UTF-16 code units).</param>
    public static ushort ComputeNameHash(string upcasedName)
    {
        ushort hash = 0;
        for (int i = 0; i < upcasedName.Length; i++)
        {
            ushort ch = (ushort)upcasedName[i];
            hash = (ushort)(((hash << 15) | (hash >> 1)) + (byte)(ch & 0xFF));
            hash = (ushort)(((hash << 15) | (hash >> 1)) + (byte)(ch >> 8));
        }
        return hash;
    }

    /// <summary>
    /// Up-case table checksum (spec 7.2.4): 32-bit rotate-right-add over
    /// the table data bytes.
    /// </summary>
    public static uint ComputeUpcaseChecksum(byte[] tableData, int length)
    {
        uint checksum = 0;
        for (int i = 0; i < length; i++)
        {
            checksum = (checksum << 31) | (checksum >> 1);
            checksum += tableData[i];
        }
        return checksum;
    }

    /// <summary>
    /// Up-case table checksum over a raw buffer.
    /// </summary>
    public static uint ComputeUpcaseChecksum(byte* data, int length)
    {
        uint checksum = 0;
        for (int i = 0; i < length; i++)
        {
            checksum = (checksum << 31) | (checksum >> 1);
            checksum += data[i];
        }
        return checksum;
    }
}

/// <summary>exFAT timestamp packing/unpacking (spec 7.4).</summary>
public static class ExFatTime
{
    /// <summary>
    /// Packs a broken-down UTC time into the exFAT 32-bit timestamp
    /// format (date high, time low, 2-second resolution).
    /// </summary>
    /// <param name="year">Full year (1980-2107).</param>
    /// <param name="month">Month (1-12).</param>
    /// <param name="day">Day (1-31).</param>
    /// <param name="hour">Hour (0-23).</param>
    /// <param name="minute">Minute (0-59).</param>
    /// <param name="second">Second (0-59; stored /2).</param>
    public static uint Pack(int year, int month, int day, int hour, int minute, int second)
    {
        if (year < 1980) year = 1980;
        if (year > 2107) year = 2107;
        if (month < 1) month = 1;
        if (month > 12) month = 12;
        if (day < 1) day = 1;
        if (day > 31) day = 31;
        if (hour < 0) hour = 0;
        if (hour > 23) hour = 23;
        if (minute < 0) minute = 0;
        if (minute > 59) minute = 59;
        if (second < 0) second = 0;
        if (second > 59) second = 59;

        uint date = (uint)(((year - 1980) << 9) | (month << 5) | day);
        uint time = (uint)((hour << 11) | (minute << 5) | (second / 2));
        return (date << 16) | time;
    }

    /// <summary>
    /// Converts a packed exFAT timestamp to UTC seconds since the Unix
    /// epoch (treating the stored time as UTC; see the driver docs).
    /// </summary>
    /// <param name="timestamp">Packed 32-bit exFAT timestamp.</param>
    public static long ToEpochSeconds(uint timestamp)
    {
        int time = (int)(timestamp & 0xFFFF);
        int date = (int)(timestamp >> 16);
        int second = (time & 0x1F) * 2;
        int minute = (time >> 5) & 0x3F;
        int hour = (time >> 11) & 0x1F;
        int day = date & 0x1F;
        int month = (date >> 5) & 0x0F;
        int year = 1980 + ((date >> 9) & 0x7F);
        return DaysFromCivil(year, month, day) * 86400L + hour * 3600L + minute * 60L + second;
    }

    /// <summary>
    /// Produces a packed exFAT timestamp from UTC seconds since the Unix
    /// epoch. Values before 1980 clamp to the exFAT epoch.
    /// </summary>
    /// <param name="epochSeconds">UTC seconds since 1970-01-01.</param>
    public static uint FromEpochSeconds(long epochSeconds)
    {
        if (epochSeconds < 315532800L)   // 1980-01-01
            return Pack(1980, 1, 1, 0, 0, 0);

        long days = epochSeconds / 86400L;
        long rem = epochSeconds % 86400L;
        int hour = (int)(rem / 3600L);
        int minute = (int)((rem % 3600L) / 60L);
        int second = (int)(rem % 60L);

        // Civil-from-days (Howard Hinnant's algorithm, days since 1970).
        long z = days + 719468;
        long era = (z >= 0 ? z : z - 146096) / 146097;
        long doe = z - era * 146097;
        long yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        long y = yoe + era * 400;
        long doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        long mp = (5 * doy + 2) / 153;
        long d = doy - (153 * mp + 2) / 5 + 1;
        long m = mp + (mp < 10 ? 3 : -9);
        long year = y + (m <= 2 ? 1 : 0);
        return Pack((int)year, (int)m, (int)d, hour, minute, second);
    }

    /// <summary>Days since the Unix epoch from a civil date (proleptic Gregorian).</summary>
    private static long DaysFromCivil(int year, int month, int day)
    {
        if (month <= 2)
        {
            year--;
            month += 12;
        }
        long era = (year >= 0 ? year : year - 399) / 400;
        long yoe = year - era * 400;
        long doy = (153 * (month - 3) + 2) / 5 + day - 1;
        long doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146097 + doe - 719468;
    }

    /// <summary>
    /// Current wall-clock time as UTC seconds since the Unix epoch,
    /// read from the kernel RTC through a small helper (the Tier-0 JIT
    /// is not trusted with out-parameters in large frames). Host tests
    /// can pin the value through <see cref="ExFatClock.OverrideNowEpoch"/>.
    /// </summary>
    public static long NowEpoch()
    {
        if (ExFatClock.OverrideNowEpoch != 0)
            return ExFatClock.OverrideNowEpoch;
        int[] clock = ReadClock();
        return EpochFromParts(clock[0], clock[1], clock[2], clock[3], clock[4], clock[5]);
    }

    /// <summary>Reads the RTC into a six-element array {y,mo,d,h,mi,s}.</summary>
    private static int[] ReadClock()
    {
        var result = new int[6];
        int y, mo, d, h, mi, s;
        Kernel.SysInfo.GetWallClock(out y, out mo, out d, out h, out mi, out s);
        if (y < 1980 || y > 2107 || mo < 1 || mo > 12 || d < 1 || d > 31 ||
            h < 0 || h > 23 || mi < 0 || mi > 59 || s < 0 || s > 59)
        {
            y = 2026;
            mo = 1;
            d = 1;
            h = 0;
            mi = 0;
            s = 0;
        }
        result[0] = y;
        result[1] = mo;
        result[2] = d;
        result[3] = h;
        result[4] = mi;
        result[5] = s;
        return result;
    }

    /// <summary>UTC epoch seconds from broken-down UTC components.</summary>
    /// <param name="year">Full year.</param>
    /// <param name="month">Month (1-12).</param>
    /// <param name="day">Day (1-31).</param>
    /// <param name="hour">Hour.</param>
    /// <param name="minute">Minute.</param>
    /// <param name="second">Second.</param>
    public static long EpochFromParts(int year, int month, int day, int hour, int minute, int second)
    {
        return DaysFromCivil(year, month, day) * 86400L + hour * 3600L + minute * 60L + second;
    }
}

/// <summary>Byte-order helpers for exFAT little-endian structures.</summary>
public static class ExFatBytes
{
    /// <summary>Reads a little-endian 16-bit value.</summary>
    public static ushort Read16(byte[] b, int off) => (ushort)(b[off] | (b[off + 1] << 8));

    /// <summary>Reads a little-endian 32-bit value.</summary>
    public static uint Read32(byte[] b, int off) =>
        (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));

    /// <summary>Reads a little-endian 64-bit value.</summary>
    public static ulong Read64(byte[] b, int off) =>
        Read32(b, off) | ((ulong)Read32(b, off + 4) << 32);

    /// <summary>Writes a little-endian 16-bit value.</summary>
    public static void Write16(byte[] b, int off, ushort v)
    {
        b[off] = (byte)v;
        b[off + 1] = (byte)(v >> 8);
    }

    /// <summary>Writes a little-endian 32-bit value.</summary>
    public static void Write32(byte[] b, int off, uint v)
    {
        b[off] = (byte)v;
        b[off + 1] = (byte)(v >> 8);
        b[off + 2] = (byte)(v >> 16);
        b[off + 3] = (byte)(v >> 24);
    }

    /// <summary>Writes a little-endian 64-bit value.</summary>
    public static void Write64(byte[] b, int off, ulong v)
    {
        Write32(b, off, (uint)v);
        Write32(b, off + 4, (uint)(v >> 32));
    }

    /// <summary>Reads a little-endian 32-bit value from a raw pointer.</summary>
    public static unsafe uint Read32FromPtr(byte* b, int off) =>
        (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));

    /// <summary>Writes a little-endian 32-bit value to a raw pointer.</summary>
    public static unsafe void Write32ToPtr(byte* b, int off, uint v)
    {
        b[off] = (byte)v;
        b[off + 1] = (byte)(v >> 8);
        b[off + 2] = (byte)(v >> 16);
        b[off + 3] = (byte)(v >> 24);
    }
}
