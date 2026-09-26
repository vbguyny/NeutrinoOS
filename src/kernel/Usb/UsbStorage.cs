// NeutrinoOS kernel - USB mass storage class driver (Phase 9 Task 1).
//
// Bulk-Only Transport (BOT) with a small SCSI transparent command set:
// INQUIRY, READ CAPACITY(10), READ(10), WRITE(10). A bound drive exposes
// sector-level block I/O (UsbStorage.BlockRead/BlockWrite) plus a device
// summary; filesystem/VFS integration mounts it like any other block
// device (see docs/PHASE9-USB.md for the /dev/sda wiring).

using System;
using ProtonOS.Usb.Xhci;

namespace ProtonOS.Usb;

/// <summary>A bound USB mass storage drive.</summary>
public sealed unsafe class UsbDisk
{
    public UsbDevice Device = null!;
    public byte BulkOutDci;
    public byte BulkInDci;
    public byte BulkOutNumber;
    public byte BulkInNumber;
    public ushort BulkOutMps = 512;
    public ushort BulkInMps = 512;

    // BOT transfer buffers.
    public UsbDma Cbw;         // 31-byte command block wrapper
    public UsbDma Csw;         // 13-byte command status wrapper
    public UsbDma Data;        // data stage bounce window (64 KiB)

    public uint BlockCount;    // in 512-byte sectors
    public uint BlockSize = 512;
    public uint Tag = 1;
    public bool Ready;

    public string Vendor = "";
    public string Product = "";
}

/// <summary>USB mass storage (BOT + SCSI) driver.</summary>
public static unsafe class UsbStorage
{
    private static void UsbLog(string m) => UsbTrace.Log(m);

    private const int MaxDisks = 4;
    private static UsbDisk?[] _disks = new UsbDisk?[MaxDisks];
    private static int _diskCount;

    public static int DiskCount => _diskCount;

    public static UsbDisk? GetDisk(int index)
    {
        if (index < 0 || index >= _diskCount)
            return null;
        return _disks[index];
    }

    public static bool Bind(UsbDevice device, int interfaceIndex, byte subclass, byte protocol)
    {
        _ = subclass;
        if (protocol != 0x50)
        {
            UsbLog("mass storage: unsupported transport protocol 0x"
                + protocol.ToString("X2", null));
            return false;
        }

        UsbEndpointDescriptor epIn, epOut;
        byte dciIn, dciOut;
        if (!device.TryGetEndpoint(interfaceIndex, 2 /* bulk */, true, out epIn, out dciIn)
            || !device.TryGetEndpoint(interfaceIndex, 2 /* bulk */, false, out epOut, out dciOut))
        {
            UsbLog("mass storage: missing bulk endpoints");
            return false;
        }

        if (_diskCount >= MaxDisks)
            return false;

        var disk = new UsbDisk();
        disk.Device = device;
        disk.BulkInDci = dciIn;
        disk.BulkOutDci = dciOut;
        disk.BulkInNumber = epIn.Number;
        disk.BulkOutNumber = epOut.Number;
        disk.BulkInMps = epIn.PacketSize == 0 ? (ushort)512 : epIn.PacketSize;
        disk.BulkOutMps = epOut.PacketSize == 0 ? (ushort)512 : epOut.PacketSize;

        disk.Cbw = UsbDma.Allocate(64);
        disk.Csw = UsbDma.Allocate(64);
        disk.Data = UsbDma.Allocate(64 * 1024);
        if (disk.Cbw.Physical == 0 || disk.Csw.Physical == 0 || disk.Data.Physical == 0)
            return false;

        _disks[_diskCount++] = disk;
        device.DriverKind = UsbDriverKind.MassStorage;
        device.Status = "mass storage (BOT)";

        ScsiInquiry(disk);
        if (ScsiReadCapacity(disk))
        {
            disk.Ready = true;
            UsbLog("disk " + DiskName(disk) + ": " + disk.Vendor + " " + disk.Product
                + " sectors=" + disk.BlockCount.ToString());
        }
        else
        {
            UsbLog("disk " + DiskName(disk) + ": READ CAPACITY failed");
        }
        return true;
    }

    public static void Unbind(UsbDevice device)
    {
        for (int i = 0; i < _diskCount; i++)
        {
            var d = _disks[i];
            if (d == null || d.Device != device)
                continue;
            UsbDma t;
            t = d.Cbw; UsbDma.Free(ref t);
            t = d.Csw; UsbDma.Free(ref t);
            t = d.Data; UsbDma.Free(ref t);
            d.Ready = false;
            for (int j = i; j < _diskCount - 1; j++)
                _disks[j] = _disks[j + 1];
            _diskCount--;
            return;
        }
    }

    public static string DiskName(UsbDisk disk)
    {
        for (int i = 0; i < _diskCount; i++)
            if (_disks[i] == disk)
                return "sda" + (i == 0 ? "" : i.ToString());
        return "sda";
    }

    // ========================================================================
    // BOT + SCSI
    // ========================================================================

    private static bool BotCommand(UsbDisk disk, byte* cdb, int cdbLen, int dataLen, bool dataIn)
    {
        // CBW
        byte* cbw = disk.Cbw.Virtual;
        for (int i = 0; i < 31; i++)
            cbw[i] = 0;
        cbw[0] = 0x55;               // dCBWSignature USBC
        cbw[1] = 0x53;
        cbw[2] = 0x42;
        cbw[3] = 0x43;
        uint tag = disk.Tag++;
        Write32(cbw, 4, tag);
        Write32(cbw, 8, (uint)dataLen);
        cbw[12] = dataIn ? (byte)0x80 : (byte)0x00;
        cbw[13] = 0;                 // LUN
        cbw[14] = (byte)cdbLen;
        for (int i = 0; i < cdbLen; i++)
            cbw[15 + i] = cdb[i];

        var r = disk.Device.Controller.DataTransfer(disk.Device.SlotId, disk.BulkOutDci,
            disk.Cbw.Physical, 31, false);
        if (!r.Ok)
        {
            UsbLog("BOT CBW failed (" + r.Status.ToString() + ")");
            disk.Device.Controller.ResetBulkEndpoints(disk.Device.SlotId, disk.BulkInDci, disk.BulkOutDci);
            return false;
        }

        // Data stage.
        if (dataLen > 0)
        {
            int remaining = dataLen;
            ulong phys = disk.Data.Physical;
            while (remaining > 0)
            {
                int chunk = remaining > 32 * 1024 ? 32 * 1024 : remaining;
                r = disk.Device.Controller.DataTransfer(disk.Device.SlotId,
                    dataIn ? disk.BulkInDci : disk.BulkOutDci, phys, chunk, dataIn);
                if (!r.Ok)
                {
                    UsbLog("BOT data stage failed (" + r.Status.ToString() + ")");
                    disk.Device.Controller.ResetBulkEndpoints(disk.Device.SlotId, disk.BulkInDci, disk.BulkOutDci);
                    return false;
                }
                phys += (ulong)chunk;
                remaining -= chunk;
            }
        }

        // CSW
        r = disk.Device.Controller.DataTransfer(disk.Device.SlotId, disk.BulkInDci,
            disk.Csw.Physical, 13, true);
        if (!r.Ok)
        {
            UsbLog("BOT CSW failed (" + r.Status.ToString() + ")");
            disk.Device.Controller.ResetBulkEndpoints(disk.Device.SlotId, disk.BulkInDci, disk.BulkOutDci);
            return false;
        }

        byte* csw = disk.Csw.Virtual;
        // CSW signature 0x53425355 -> bytes 55 53 42 53 ("USBS").
        bool sig = csw[0] == 0x55 && csw[1] == 0x53 && csw[2] == 0x42 && csw[3] == 0x53;
        uint cswTag = Read32(csw, 4);
        byte status = csw[12];
        if (!sig || cswTag != tag)
        {
            UsbLog("BOT CSW mismatch");
            disk.Device.Controller.ResetBulkEndpoints(disk.Device.SlotId, disk.BulkInDci, disk.BulkOutDci);
            return false;
        }
        if (status != 0)
        {
            UsbLog("BOT command status " + status.ToString() + " (CSW residue handling deferred)");
            return false;
        }
        return true;
    }

    /// <summary>SCSI INQUIRY (standard data, 36 bytes).</summary>
    public static bool ScsiInquiry(UsbDisk disk)
    {
        byte* cdb = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            cdb[i] = 0;
        cdb[0] = 0x12;       // INQUIRY
        cdb[4] = 36;

        if (!BotCommand(disk, cdb, 6, 36, true))
            return false;

        byte* data = disk.Data.Virtual;
        disk.Vendor = Ascii(data + 8, 8);
        disk.Product = Ascii(data + 16, 16);
        return true;
    }

    private static string Ascii(byte* p, int len)
    {
        string s = "";
        for (int i = 0; i < len; i++)
        {
            byte c = p[i];
            if (c >= 32 && c < 127)
                s += (char)c;
        }
        // Trim trailing spaces.
        while (s.Length > 0 && s[s.Length - 1] == ' ')
            s = s.Substring(0, s.Length - 1);
        return s;
    }

    /// <summary>SCSI READ CAPACITY(10).</summary>
    public static bool ScsiReadCapacity(UsbDisk disk)
    {
        byte* cdb = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            cdb[i] = 0;
        cdb[0] = 0x25;       // READ CAPACITY(10)

        if (!BotCommand(disk, cdb, 10, 8, true))
            return false;

        byte* d = disk.Data.Virtual;
        uint lastLba = Read32(d, 0);
        uint blockLen = Read32(d, 4);
        disk.BlockCount = lastLba + 1;
        disk.BlockSize = blockLen == 0 ? 512 : blockLen;
        return true;
    }

    /// <summary>
    /// Read `sectorCount` 512-byte sectors starting at `lba` into the
    /// caller's buffer (up to 32 KiB per call).
    /// </summary>
    public static bool BlockRead(UsbDisk disk, uint lba, int sectorCount, byte* dest)
    {
        if (!disk.Ready || sectorCount <= 0 || sectorCount > 64)
            return false;
        int bytes = sectorCount * 512;

        byte* cdb = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            cdb[i] = 0;
        cdb[0] = 0x28;       // READ(10)
        // LBA is big-endian in SCSI CDBs.
        cdb[2] = (byte)(lba >> 24);
        cdb[3] = (byte)(lba >> 16);
        cdb[4] = (byte)(lba >> 8);
        cdb[5] = (byte)lba;
        cdb[7] = (byte)(sectorCount >> 8);
        cdb[8] = (byte)(sectorCount & 0xFF);

        if (!BotCommand(disk, cdb, 10, bytes, true))
            return false;

        byte* src = disk.Data.Virtual;
        for (int i = 0; i < bytes; i++)
            dest[i] = src[i];
        return true;
    }

    /// <summary>Write 512-byte sectors (up to 32 KiB per call).</summary>
    public static bool BlockWrite(UsbDisk disk, uint lba, int sectorCount, byte* src)
    {
        if (!disk.Ready || sectorCount <= 0 || sectorCount > 64)
            return false;
        int bytes = sectorCount * 512;

        byte* dst = disk.Data.Virtual;
        for (int i = 0; i < bytes; i++)
            dst[i] = src[i];

        byte* cdb = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            cdb[i] = 0;
        cdb[0] = 0x2A;       // WRITE(10)
        cdb[2] = (byte)(lba >> 24);
        cdb[3] = (byte)(lba >> 16);
        cdb[4] = (byte)(lba >> 8);
        cdb[5] = (byte)lba;
        cdb[7] = (byte)(sectorCount >> 8);
        cdb[8] = (byte)(sectorCount & 0xFF);

        return BotCommand(disk, cdb, 10, bytes, false);
    }

    private static void Write32(byte* p, int off, uint v)
    {
        p[off] = (byte)v;
        p[off + 1] = (byte)(v >> 8);
        p[off + 2] = (byte)(v >> 16);
        p[off + 3] = (byte)(v >> 24);
    }

    private static uint Read32(byte* p, int off)
    {
        return (uint)p[off] | ((uint)p[off + 1] << 8)
             | ((uint)p[off + 2] << 16) | ((uint)p[off + 3] << 24);
    }
}
