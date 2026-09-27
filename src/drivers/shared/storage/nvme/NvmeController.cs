// ProtonOS NVMe host controller driver (Phase 9 Task 5).
//
// Minimal but complete NVMe 1.x implementation: controller reset, admin
// submission/completion queues, IDENTIFY (controller + namespace 1),
// one I/O queue pair, and the NVM command set (READ/WRITE/FLUSH) with
// 4 KiB PRP pages. Registers live in BAR0 (MMIO); queues and data
// buffers come from the contiguous page allocator.
using System;
using ProtonOS.DDK.Drivers;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Platform;

namespace ProtonOS.Drivers.Storage.Nvme;

/// <summary>NVMe controller instance bound to one PCI function.</summary>
public unsafe class NvmeController
{
    private PciDeviceInfo _pciDevice;
    private byte* _regs;

    // Doorbell stride (bytes between doorbells).
    private uint _dstrd;

    // Admin queues.
    private byte* _adminSq;
    private ulong _adminSqPhys;
    private byte* _adminCq;
    private ulong _adminCqPhys;
    private ushort _adminSqTail;
    private ushort _adminCqHead;
    private bool _adminPhase;

    // I/O queues (qid 1).
    private byte* _ioSq;
    private ulong _ioSqPhys;
    private byte* _ioCq;
    private ulong _ioCqPhys;
    private ushort _ioSqTail;
    private ushort _ioCqHead;
    private bool _ioPhase;

    // Data buffer (one page).
    private byte* _dataBuf;
    private ulong _dataBufPhys;

    private ushort _cid;

    // Namespace info.
    public ulong NamespaceSectors { get; private set; }
    public int SectorSize { get; private set; }
    public string Model { get; private set; } = "";
    public string Serial { get; private set; } = "";
    public bool IsInitialized { get; private set; }

    public bool Initialize(PciDeviceInfo pciDevice)
    {
        _pciDevice = pciDevice;

        PCI.EnableBusMaster(pciDevice.Address);
        PCI.EnableMemorySpace(pciDevice.Address);

        var bar0 = pciDevice.Bars[0];
        if (!bar0.IsValid || bar0.IsIO)
        {
            Debug.WriteLine("[NVMe] BAR0 invalid or I/O");
            return false;
        }
        // 64-bit BARs can sit above 4GB (QEMU's NVMe controller does), so
        // the physmap does not cover them. Map the register window through
        // MapMMIO like the virtio driver does for its BARs.
        _regs = (byte*)Memory.MapMMIO(bar0.BaseAddress, bar0.Size);
        Debug.Write("[NVMe] BAR0 at ");
        Debug.WriteHex(bar0.BaseAddress);
        Debug.Write(" size ");
        Debug.WriteHex(bar0.Size);
        Debug.WriteLine(" (mapped)");

        ulong cap = Read64(NvmeReg.CAP);
        int mqes = (int)(cap & 0xFFFF);            // queue entries - 1
        _dstrd = (uint)((cap >> 32) & 0xF);
        Debug.Write("[NVMe] CAP mqes=");
        Debug.WriteDecimal(mqes + 1);
        Debug.Write(" dstrd=");
        Debug.WriteDecimal((int)_dstrd);
        Debug.WriteLine();

        if (!DisableController())
        {
            Debug.WriteLine("[NVMe] controller reset failed");
            return false;
        }
        // Queue registers must be configured while the controller is
        // disabled: QEMU (and real devices) latch AQA/ASQ/ACQ on the CC.EN
        // 0->1 transition.
        if (!SetupAdminQueues())
        {
            Debug.WriteLine("[NVMe] admin queue setup failed");
            return false;
        }
        if (!EnableController())
        {
            Debug.WriteLine("[NVMe] controller enable failed");
            return false;
        }

        _dataBufPhys = Memory.AllocatePages(1);
        _dataBuf = (byte*)Memory.PhysToVirt(_dataBufPhys);

        if (!IdentifyController())
            return false;
        if (!IdentifyNamespace(1))
            return false;
        if (!SetupIoQueues())
            return false;

        IsInitialized = true;
        Debug.Write("[NVMe] ready: ");
        Debug.WriteLine(Model);
        Debug.Write("[NVMe] sectors=");
        Debug.WriteDecimal((int)NamespaceSectors);
        Debug.Write(" sector_size=");
        Debug.WriteDecimal(SectorSize);
        Debug.WriteLine();
        return true;
    }

    // ==================== controller bring-up ====================

    private bool DisableController()
    {
        uint cc = Read32(NvmeReg.CC);
        cc &= ~NvmeCc.EN;
        Write32(NvmeReg.CC, cc);

        ulong deadline = Timer.GetUptimeMilliseconds() + 2000;
        while ((Read32(NvmeReg.CSTS) & NvmeCsts.RDY) != 0)
        {
            if (Timer.GetUptimeMilliseconds() > deadline)
            {
                Debug.WriteLine("[NVMe] RDY did not clear");
                return false;
            }
        }
        return true;
    }

    private bool EnableController()
    {
        // Enable: NVM command set, 4K pages, 64B SQ entries, 16B CQ.
        uint cc = NvmeCc.EN | NvmeCc.CSS_NVM | NvmeCc.MPS_4K | NvmeCc.AMS_RR |
             NvmeCc.IOSQES_64 | NvmeCc.IOCQES_16;
        Write32(NvmeReg.CC, cc);

        ulong deadline = Timer.GetUptimeMilliseconds() + 3000;
        while ((Read32(NvmeReg.CSTS) & NvmeCsts.RDY) == 0)
        {
            if (Timer.GetUptimeMilliseconds() > deadline)
            {
                Debug.WriteLine("[NVMe] RDY did not set");
                return false;
            }
        }
        return true;
    }

    private bool SetupAdminQueues()
    {
        _adminSqPhys = Memory.AllocatePages(1);
        _adminCqPhys = Memory.AllocatePages(1);
        _adminSq = (byte*)Memory.PhysToVirt(_adminSqPhys);
        _adminCq = (byte*)Memory.PhysToVirt(_adminCqPhys);

        for (int i = 0; i < NvmePage.SIZE; i++)
        {
            _adminSq[i] = 0;
            _adminCq[i] = 0;
        }

        // AQA: (cq entries-1) << 16 | (sq entries-1)
        uint aqa = (uint)((NvmeQueue.ADMIN_ENTRIES - 1) << 16) |
                   (uint)(NvmeQueue.ADMIN_ENTRIES - 1);
        Write32(NvmeReg.AQA, aqa);
        Write64(NvmeReg.ASQ, _adminSqPhys);
        Write64(NvmeReg.ACQ, _adminCqPhys);
        _adminSqTail = 0;
        _adminCqHead = 0;
        _adminPhase = true;
        return true;
    }

    private bool SetupIoQueues()
    {
        _ioSqPhys = Memory.AllocatePages(1);
        _ioCqPhys = Memory.AllocatePages(1);
        _ioSq = (byte*)Memory.PhysToVirt(_ioSqPhys);
        _ioCq = (byte*)Memory.PhysToVirt(_ioCqPhys);
        for (int i = 0; i < NvmePage.SIZE; i++)
        {
            _ioSq[i] = 0;
            _ioCq[i] = 0;
        }

        // Create I/O completion queue (admin opcode 0x05):
        // cdw10 = qid | (size-1)<<16; cdw11 = PC(bit0)=1, IEN=0, IV=0
        // (bits 31:16 are the interrupt vector, not a queue id).
        var cmd = stackalloc byte[64];
        ZeroSqe(cmd);
        cmd[0] = NvmeAdminOp.CREATE_CQ;
        SetDw10(cmd, (uint)NvmeQueue.IO_QID | (uint)((NvmeQueue.IO_ENTRIES - 1) << 16));
        SetDw11(cmd, 1u);   // physically contiguous, polling (no interrupts)
        SetPrp1(cmd, _ioCqPhys);
        if (!AdminSubmitAndWait(cmd))
        {
            Debug.WriteLine("[NVMe] create io cq failed");
            return false;
        }

        // Create I/O submission queue (admin opcode 0x01):
        // cdw10 = qid | (size-1)<<16; cdw11 = PC(1) | CQID<<16
        ZeroSqe(cmd);
        cmd[0] = NvmeAdminOp.CREATE_SQ;
        SetDw10(cmd, (uint)NvmeQueue.IO_QID | (uint)((NvmeQueue.IO_ENTRIES - 1) << 16));
        SetDw11(cmd, 1u | ((uint)NvmeQueue.IO_QID << 16));
        SetPrp1(cmd, _ioSqPhys);
        if (!AdminSubmitAndWait(cmd))
        {
            Debug.WriteLine("[NVMe] create io sq failed");
            return false;
        }

        _ioSqTail = 0;
        _ioCqHead = 0;
        _ioPhase = true;
        return true;
    }

    // ==================== identify ====================

    private bool IdentifyController()
    {
        for (int i = 0; i < NvmePage.SIZE; i++)
            _dataBuf[i] = 0;

        var cmd = stackalloc byte[64];
        ZeroSqe(cmd);
        cmd[0] = NvmeAdminOp.IDENTIFY;
        SetDw10(cmd, NvmeIdentify.CONTROLLER);
        SetPrp1(cmd, _dataBufPhys);
        if (!AdminSubmitAndWait(cmd))
        {
            Debug.WriteLine("[NVMe] identify controller failed");
            return false;
        }

        // Model number at offset 24 (40 bytes), serial at 4 (20 bytes).
        Model = AsciiString(_dataBuf + 24, 40);
        Serial = AsciiString(_dataBuf + 4, 20);
        return true;
    }

    private bool IdentifyNamespace(int nsid)
    {
        for (int i = 0; i < NvmePage.SIZE; i++)
            _dataBuf[i] = 0;

        var cmd = stackalloc byte[64];
        ZeroSqe(cmd);
        cmd[0] = NvmeAdminOp.IDENTIFY;
        SetNsid(cmd, (uint)nsid);
        SetDw10(cmd, NvmeIdentify.NAMESPACE);
        SetPrp1(cmd, _dataBufPhys);
        if (!AdminSubmitAndWait(cmd))
        {
            Debug.WriteLine("[NVMe] identify namespace failed");
            return false;
        }

        ulong nsze = ReadU64(_dataBuf + 0);
        if (nsze == 0)
        {
            Debug.Write("[NVMe] namespace ");
            Debug.WriteDecimal(nsid);
            Debug.WriteLine(" absent");
            return false;
        }

        // LBA format: FLBAS (byte 26, low nibble) selects LBAF entry at
        // offset 128; LBADS (bits 16..23) = log2(sector size).
        int flbas = _dataBuf[26] & 0x0F;
        byte lbads = *(_dataBuf + 128 + flbas * 4 + 2);
        int sector = 1 << lbads;

        NamespaceSectors = nsze;
        SectorSize = sector;
        return true;
    }

    // ==================== NVM commands ====================

    /// <summary>Read/write one page (up to 4 KiB) at an LBA.</summary>
    public bool ReadSectors(ulong lba, int sectorCount, byte* dest)
    {
        if (!IsInitialized)
            return false;
        var cmd = stackalloc byte[64];
        ZeroSqe(cmd);
        cmd[0] = NvmeNvmOp.READ;
        SetNsid(cmd, 1);
        SetPrp1(cmd, _dataBufPhys);
        SetDw10(cmd, (uint)(lba & 0xFFFFFFFF));
        SetDw11(cmd, (uint)(lba >> 32));
        SetDw12(cmd, (uint)(sectorCount - 1));
        if (!IoSubmitAndWait(cmd))
            return false;
        int bytes = sectorCount * SectorSize;
        for (int i = 0; i < bytes; i++)
            dest[i] = _dataBuf[i];
        return true;
    }

    public bool WriteSectors(ulong lba, int sectorCount, byte* src)
    {
        if (!IsInitialized)
            return false;
        int bytes = sectorCount * SectorSize;
        for (int i = 0; i < bytes; i++)
            _dataBuf[i] = src[i];

        var cmd = stackalloc byte[64];
        ZeroSqe(cmd);
        cmd[0] = NvmeNvmOp.WRITE;
        SetNsid(cmd, 1);
        SetPrp1(cmd, _dataBufPhys);
        SetDw10(cmd, (uint)(lba & 0xFFFFFFFF));
        SetDw11(cmd, (uint)(lba >> 32));
        SetDw12(cmd, (uint)(sectorCount - 1));
        return IoSubmitAndWait(cmd);
    }

    public bool Flush()
    {
        if (!IsInitialized)
            return false;
        var cmd = stackalloc byte[64];
        ZeroSqe(cmd);
        cmd[0] = NvmeNvmOp.FLUSH;
        SetNsid(cmd, 1);
        return IoSubmitAndWait(cmd);
    }

    /// <summary>Raw accessor used by the self-test.</summary>
    public byte* DataBuffer => _dataBuf;

    // ==================== queue mechanics ====================

    private bool AdminSubmitAndWait(byte* cmd)
    {
        return SubmitAndWait(cmd, NvmeQueue.ADMIN_QID);
    }

    private bool IoSubmitAndWait(byte* cmd)
    {
        return SubmitAndWait(cmd, NvmeQueue.IO_QID);
    }

    private bool SubmitAndWait(byte* cmd, int qid)
    {
        // Copy the command into the SQ slot.
        byte* sq = qid == NvmeQueue.ADMIN_QID ? _adminSq : _ioSq;
        int entries = qid == NvmeQueue.ADMIN_QID ?
            NvmeQueue.ADMIN_ENTRIES : NvmeQueue.IO_ENTRIES;
        ushort tail = qid == NvmeQueue.ADMIN_QID ? _adminSqTail : _ioSqTail;

        _cid++;
        cmd[2] = (byte)_cid;
        cmd[3] = (byte)(_cid >> 8);

        int slot = tail % entries;
        byte* dst = sq + slot * 64;
        for (int i = 0; i < 64; i++)
            dst[i] = cmd[i];

        // Ring the SQ doorbell.
        tail = (ushort)((tail + 1) % entries);
        if (qid == NvmeQueue.ADMIN_QID)
            _adminSqTail = tail;
        else
            _ioSqTail = tail;
        RingDoorbell(qid * 2, tail);

        // Poll the CQ for our command id.
        byte* cq = qid == NvmeQueue.ADMIN_QID ? _adminCq : _ioCq;
        ulong deadline = Timer.GetUptimeMilliseconds() + 3000;
        while (true)
        {
            if (Timer.GetUptimeMilliseconds() > deadline)
            {
                Debug.WriteLine("[NVMe] command timeout");
                return false;
            }
            ushort head = qid == NvmeQueue.ADMIN_QID ? _adminCqHead : _ioCqHead;
            byte* cqe = cq + (head % entries) * 16;
            byte status = cqe[14];
            bool phase = (status & 0x01) != 0;
            bool expected = qid == NvmeQueue.ADMIN_QID ? _adminPhase : _ioPhase;
            if (phase == expected)
            {
                ushort cid = (ushort)(cqe[12] | (cqe[13] << 8));
                if (cid == _cid)
                {
                    byte code = (byte)(status >> 1);
                    if (qid == NvmeQueue.ADMIN_QID)
                        _adminCqHead = (ushort)((head + 1) % entries);
                    else
                        _ioCqHead = (ushort)((head + 1) % entries);
                    RingDoorbell(qid * 2 + 1, (ushort)((head + 1) % entries));
                    if (code != 0)
                    {
                        Debug.Write("[NVMe] command status 0x");
                        Debug.WriteDecimal(code);
                        Debug.WriteLine();
                        return false;
                    }
                    return true;
                }
                // Consume unrelated completions to advance.
                if (qid == NvmeQueue.ADMIN_QID)
                {
                    _adminCqHead = (ushort)((head + 1) % entries);
                    if (_adminCqHead == 0)
                        _adminPhase = !_adminPhase;
                }
                else
                {
                    _ioCqHead = (ushort)((head + 1) % entries);
                    if (_ioCqHead == 0)
                        _ioPhase = !_ioPhase;
                }
                RingDoorbell(qid * 2 + 1, qid == NvmeQueue.ADMIN_QID ? _adminCqHead : _ioCqHead);
            }
        }
    }

    private void RingDoorbell(int index, ushort value)
    {
        uint stride = 4u << (int)_dstrd;
        Write32(0x1000 + index * (int)stride, value);
    }

    // ==================== SQE field helpers ====================

    private static void ZeroSqe(byte* cmd)
    {
        for (int i = 0; i < 64; i++)
            cmd[i] = 0;
    }

    private static void SetNsid(byte* cmd, uint nsid)
    {
        cmd[4] = (byte)nsid;
        cmd[5] = (byte)(nsid >> 8);
        cmd[6] = (byte)(nsid >> 16);
        cmd[7] = (byte)(nsid >> 24);
    }

    private static void SetPrp1(byte* cmd, ulong addr)
    {
        WriteU64(cmd + 24, addr);
    }

    private static void SetDw10(byte* cmd, uint value)
    {
        cmd[40] = (byte)value;
        cmd[41] = (byte)(value >> 8);
        cmd[42] = (byte)(value >> 16);
        cmd[43] = (byte)(value >> 24);
    }

    private static void SetDw11(byte* cmd, uint value)
    {
        cmd[44] = (byte)value;
        cmd[45] = (byte)(value >> 8);
        cmd[46] = (byte)(value >> 16);
        cmd[47] = (byte)(value >> 24);
    }

    private static void SetDw12(byte* cmd, uint value)
    {
        cmd[48] = (byte)value;
        cmd[49] = (byte)(value >> 8);
        cmd[50] = (byte)(value >> 16);
        cmd[51] = (byte)(value >> 24);
    }

    private static void WriteU64(byte* p, ulong value)
    {
        for (int i = 0; i < 8; i++)
            p[i] = (byte)(value >> (8 * i));
    }

    private static ulong ReadU64(byte* p)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++)
            v |= (ulong)p[i] << (8 * i);
        return v;
    }

    private static string AsciiString(byte* p, int max)
    {
        // IDENTIFY fields are space padded, not NUL terminated; stop at a
        // NUL if present, then trim trailing spaces.
        int n = max;
        for (int i = 0; i < max; i++)
        {
            if (p[i] == 0)
            {
                n = i;
                break;
            }
        }
        while (n > 0 && p[n - 1] == ' ')
            n--;
        if (n == 0)
            return "";
        var chars = new char[n];
        for (int i = 0; i < n; i++)
            chars[i] = (char)p[i];
        return new string(chars);
    }

    // ==================== MMIO ====================

    private uint Read32(int off) => *(uint*)(_regs + off);
    private void Write32(int off, uint value) => *(uint*)(_regs + off) = value;
    private ulong Read64(int off) => *(ulong*)(_regs + off);
    private void Write64(int off, ulong value) => *(ulong*)(_regs + off) = value;
}
