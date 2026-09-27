// ProtonOS NVMe Entry Point (Phase 9 Task 5).
//
// Static entry point designed for kernel JIT compilation, mirroring the
// AHCI entry: Probe (PCI class match) + Bind (controller bring-up) plus
// a boot-time self-test (IDENTIFY, read LBA0, optional write/read/flush
// verify when the boot volume contains a 'nvme-write-test' flag file).
using System;
using System.IO;
using ProtonOS.DDK.Drivers;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Platform;

namespace ProtonOS.Drivers.Storage.Nvme;

/// <summary>Static entry point for the NVMe driver.</summary>
public static unsafe class NvmeEntry
{
    private static NvmeController? _controller;

    /// <summary>Controller bound (null when no NVMe device present).</summary>
    public static bool IsBound => _controller != null && _controller.IsInitialized;

    /// <summary>Namespace 1 size in sectors.</summary>
    public static ulong SectorCount => _controller?.NamespaceSectors ?? 0;

    /// <summary>Namespace 1 logical sector size.</summary>
    public static int SectorSize => _controller?.SectorSize ?? 0;

    // ---- Phase 10: block device registry accessors ----------------------
    // Uniform JIT shape shared with AHCI and the kernel registry (see
    // AhciEntry). The NVMe namespace is device index 0.

    /// <summary>Namespaces exposed to the registry (0 or 1).</summary>
    public static int BlockDeviceCount() => SectorCount > 0 ? 1 : 0;

    /// <summary>Sector count of namespace <paramref name="index"/>.</summary>
    public static ulong BlockDeviceSectorCount(int index) => index == 0 ? SectorCount : 0;

    /// <summary>Sector size of namespace <paramref name="index"/>.</summary>
    public static uint BlockDeviceSectorSize(int index) => index == 0 ? (uint)SectorSize : 0;

    /// <summary>Reads sectors from namespace 0 (blocks read or negative).</summary>
    public static int BlockDeviceRead(int index, ulong startBlock, uint blockCount, byte* buffer)
    {
        if (index != 0 || buffer == null || blockCount == 0)
            return -1;
        if (startBlock + blockCount > SectorCount)
            return -2;

        // Chunk transfers: bounded work per submission.
        uint done = 0;
        while (done < blockCount)
        {
            uint chunk = blockCount - done;
            if (chunk > 128)
                chunk = 128;
            if (!ReadSectors(startBlock + done, (int)chunk, buffer + done * 512))
                return (int)done;
            done += chunk;
        }
        return (int)done;
    }

    /// <summary>Writes sectors to namespace 0 (blocks written or negative).</summary>
    public static int BlockDeviceWrite(int index, ulong startBlock, uint blockCount, byte* buffer)
    {
        if (index != 0 || buffer == null || blockCount == 0)
            return -1;
        if (startBlock + blockCount > SectorCount)
            return -2;

        uint done = 0;
        while (done < blockCount)
        {
            uint chunk = blockCount - done;
            if (chunk > 128)
                chunk = 128;
            if (!WriteSectors(startBlock + done, (int)chunk, buffer + done * 512))
                return (int)done;
            done += chunk;
        }
        return (int)done;
    }

    /// <summary>Flushes namespace 0 (0 on success, negative on error).</summary>
    public static int BlockDeviceFlush(int index)
    {
        if (index != 0)
            return -1;
        return Flush() ? 0 : -1;
    }

    /// <summary>
    /// Match NVMe controllers: PCI class 0x01 (mass storage), subclass
    /// 0x08 (NVM), programming interface 0x02 (NVMe). A few known device
    /// ids are matched as a fallback for quirky firmware.
    /// </summary>
    public static bool Probe(ushort vendorId, ushort deviceId, byte classCode, byte subclassCode, byte progIf)
    {
        if (classCode == 0x01 && subclassCode == 0x08 && progIf == 0x02)
            return true;

        if (vendorId == 0x1B36 && deviceId == 0x0010)   // QEMU NVMe
            return true;
        if (vendorId == 0x8086 && deviceId == 0x5845)   // Intel (VMD direct)
            return true;

        return false;
    }

    /// <summary>Bind the driver to a PCI device.</summary>
    public static bool Bind(byte bus, byte device, byte function)
    {
        var pciDevice = new PciDeviceInfo();
        pciDevice.Address = new PciAddress(bus, device, function);

        uint vendorDevice = PCI.ReadConfig32(bus, device, function, PCI.PCI_VENDOR_ID);
        pciDevice.VendorId = (ushort)(vendorDevice & 0xFFFF);
        pciDevice.DeviceId = (ushort)(vendorDevice >> 16);

        uint classReg = PCI.ReadConfig32(bus, device, function, PCI.PCI_REVISION_ID);
        pciDevice.RevisionId = (byte)(classReg & 0xFF);
        pciDevice.ProgIf = (byte)((classReg >> 8) & 0xFF);
        pciDevice.SubclassCode = (byte)((classReg >> 16) & 0xFF);
        pciDevice.ClassCode = (byte)((classReg >> 24) & 0xFF);

        ReadBars(bus, device, function, pciDevice);

        var controller = new NvmeController();
        if (!controller.Initialize(pciDevice))
        {
            Debug.WriteLine("[NVMe] controller init failed");
            return false;
        }
        _controller = controller;

        RunSelfTest(controller);
        return true;
    }

    /// <summary>
    /// Boot-time verification: read LBA0 (expects the test image's
    /// signature bytes when present) and, when the boot volume carries a
    /// 'nvme-write-test' flag, a full write/flush/read/verify cycle on
    /// the last LBA.
    /// </summary>
    private static void RunSelfTest(NvmeController controller)
    {
        Debug.Write("[NVMe] model=");
        Debug.WriteLine(controller.Model);
        Debug.Write("[NVMe] serial=");
        Debug.WriteLine(controller.Serial);

        // Read LBA0 into the controller's data page and report the
        // first bytes (the test image starts with 'NEUTRINOS-OS').
        byte* page = controller.DataBuffer;
        if (controller.ReadSectors(0, 1, page))
        {
            var tag = new char[8];
            for (int i = 0; i < 8; i++)
                tag[i] = (char)page[i];
            Debug.Write("[NVMe] lba0=");
            Debug.Write(new string(tag));
            Debug.Write(" ... ");
            if (page[0] == (byte)'N' && page[1] == (byte)'E' && page[2] == (byte)'U')
                Debug.WriteLine("signature OK");
            else
                Debug.WriteLine("(no signature)");
        }
        else
        {
            Debug.WriteLine("[NVMe] read lba0 FAILED");
        }

        // Optional destructive test (guarded by a flag file on the boot
        // volume, e.g. /nvme-write-test).
        if (!File.Exists("/nvme-write-test"))
            return;
        if (controller.NamespaceSectors == 0)
            return;

        ulong last = controller.NamespaceSectors - 1;
        int sector = controller.SectorSize;
        for (int i = 0; i < sector; i++)
            page[i] = 0;
        page[0] = 0x4E;   // 'N'
        page[1] = 0x56;   // 'V'
        page[2] = 0x4D;   // 'M'
        page[3] = 0x45;   // 'E'
        page[sector - 1] = 0xAA;

        bool ok = controller.WriteSectors(last, 1, page);
        if (ok)
            ok = controller.Flush();
        if (ok)
        {
            for (int i = 0; i < sector; i++)
                page[i] = 0;
            ok = controller.ReadSectors(last, 1, page);
        }
        if (ok)
        {
            ok = page[0] == 0x4E && page[1] == 0x56 && page[2] == 0x4D &&
                 page[3] == 0x45 && page[sector - 1] == 0xAA;
        }
        Debug.Write("[NVMe] write test ");
        Debug.WriteLine(ok ? "PASS" : "FAIL");
    }

    /// <summary>Read from namespace 1 (up to one page per call).</summary>
    public static bool ReadSectors(ulong lba, int count, byte* dest) =>
        _controller != null && _controller.ReadSectors(lba, count, dest);

    /// <summary>Write to namespace 1 (up to one page per call).</summary>
    public static bool WriteSectors(ulong lba, int count, byte* src) =>
        _controller != null && _controller.WriteSectors(lba, count, src);

    /// <summary>Flush volatile write cache.</summary>
    public static bool Flush() => _controller != null && _controller.Flush();

    /// <summary>Read BAR information from PCI config space.</summary>
    private static void ReadBars(byte bus, byte device, byte function, PciDeviceInfo pciDevice)
    {
        int i = 0;
        while (i < 6)
        {
            ushort barOffset = (ushort)(PCI.PCI_BAR0 + (i * 4));
            uint barValue = PCI.ReadConfig32(bus, device, function, barOffset);

            bool isIo = (barValue & 1) != 0;
            bool is64Bit = !isIo && ((barValue >> 1) & 3) == 2;
            bool isPrefetchable = !isIo && ((barValue >> 3) & 1) != 0;

            ulong baseAddress;
            if (isIo)
            {
                baseAddress = barValue & 0xFFFFFFFC;
            }
            else if (is64Bit)
            {
                uint highValue = PCI.ReadConfig32(bus, device, function, (ushort)(barOffset + 4));
                baseAddress = (barValue & 0xFFFFFFF0) | ((ulong)highValue << 32);
            }
            else
            {
                baseAddress = barValue & 0xFFFFFFF0;
            }

            uint sizeMask;
            if (baseAddress != 0)
            {
                PCI.WriteConfig32(bus, device, function, barOffset, 0xFFFFFFFF);
                uint sizeRead = PCI.ReadConfig32(bus, device, function, barOffset);
                PCI.WriteConfig32(bus, device, function, barOffset, barValue);
                if (isIo)
                    sizeMask = sizeRead & 0xFFFFFFFC;
                else
                    sizeMask = sizeRead & 0xFFFFFFF0;
                sizeMask = ~sizeMask + 1;
            }
            else
            {
                sizeMask = 0;
            }

            PciBar bar;
            bar.Index = i;
            bar.BaseAddress = baseAddress;
            bar.Size = sizeMask;
            bar.IsIO = isIo;
            bar.Is64Bit = is64Bit;
            bar.IsPrefetchable = isPrefetchable;
            pciDevice.Bars[i] = bar;

            // 64-bit BARs consume two slots.
            if (is64Bit)
            {
                PciBar upper;
                upper.Index = i + 1;
                upper.BaseAddress = 0;
                upper.Size = 0;
                upper.IsIO = false;
                upper.Is64Bit = true;
                upper.IsPrefetchable = false;
                pciDevice.Bars[i + 1] = upper;
                i += 2;
                continue;
            }
            i++;
        }
    }
}
