// NeutrinoOS kernel - xHCI controller implementation (Phase 9 Task 1).
// See XhciTypes.cs for the register/TRB definitions and the polling note.

using System;
using ProtonOS.Arch;
using ProtonOS.Memory;
using ProtonOS.Platform;

namespace ProtonOS.Usb.Xhci;

/// <summary>Per-endpoint transfer ring state.</summary>
public unsafe struct XhciEpRing
{
    public UsbDma Dma;
    public uint EnqueueIndex;    // 0..RingTrbs-2 (last slot is the link TRB)
    public bool Cycle;           // producer cycle state
    public bool Valid;
    public byte EpType;          // xHCI EP type (2/6 bulk, 3/7 interrupt)
    public byte MaxPacketSizeHi; // MPS >> 8 for slot 0
    public ushort MaxPacketSize;
    public byte Interval;
}

/// <summary>Per-slot device state.</summary>
public unsafe struct XhciSlot
{
    public bool Enabled;
    public byte Speed;
    public byte RootPort;        // 1-based
    public byte SlotState;       // 0=disabled,2=default,3=addressed,4=configured
    public XhciEpRing* Rings;    // 32 entries (index = DCI)
}

/// <summary>
/// xHCI host controller. One instance per PCI function. All operations are
/// synchronous: submit -> program doorbell -> poll the event ring.
/// </summary>
public sealed unsafe class XhciController
{
    public const int RingTrbs = 64;          // per-ring TRB count (last = link)
    public const int EventRingTrbs = 256;
    public const int MaxSlotsSupported = 64;
    public const int MaxPortsSupported = 64;
    private const int DciCount = 32;

    // MMIO
    private byte* _bar;
    private uint _opBase;      // BAR0 + CAPLENGTH
    private uint _rtBase;      // BAR0 + RTSOFF
    private uint _dbBase;      // BAR0 + DBOFF

    // Capabilities
    public int MaxSlots;
    public int MaxPorts;
    public ushort HciVersion;
    private uint _contextSize;     // 32 or 64
    private uint _scratchpadCount;

    // DMA structures
    private UsbDma _cmdRing;
    private UsbDma _evtRing;
    private UsbDma _erst;
    private UsbDma _dcbaa;
    private UsbDma _inputCtx;
    private UsbDma _scratchpadArray;
    private UsbDma _scratchpages;

    // Command ring producer state
    private uint _cmdEnqueue;
    private bool _cmdCycle = true;

    // Event ring consumer state
    private uint _evtIndex;
    private bool _evtCycle = true;
    private bool _evtError;

    // Command completion state
    private bool _cmdDone;
    private XhciTrb _cmdCompletion;
    private ulong _cmdPendingPhys;

    // Transfer completion queue (completions carry slot/endpoint ids).
    private const int CompletionQueueSize = 32;
    private XhciXferCompletion[] _completions = new XhciXferCompletion[CompletionQueueSize];
    private int _completionCount;

    // Ports with pending status-change notifications (bit per 1-based port)
    public ulong PortChangePending;

    // Slots
    private XhciSlot* _slots;
    private bool _slotsAllocated;

    public bool Initialized { get; private set; }
    public byte NextAddress = 1;

    // ========================================================================
    // Register accessors
    // ========================================================================

    // MMIO reads carry a CPU.MemoryBarrier() on purpose: without the call
    // the bflat code generator has been observed to fold sequential loads
    // through the same base pointer into one load (stale-cache behaviour).
    // The barrier is a call, so each read is emitted and stays ordered.
    public byte* Bar => _bar;

    public uint Read32(uint off) { uint v = *(uint*)(_bar + off); CPU.MemoryBarrier(); return v; }
    public void Write32(uint off, uint v) => *(uint*)(_bar + off) = v;
    public ulong Read64(uint off) { ulong v = *(ulong*)(_bar + off); CPU.MemoryBarrier(); return v; }
    public void Write64(uint off, ulong v) => *(ulong*)(_bar + off) = v;

    public uint OpRead(uint off) { uint v = *(uint*)(_bar + _opBase + off); CPU.MemoryBarrier(); return v; }
    public void OpWrite(uint off, uint v) => *(uint*)(_bar + _opBase + off) = v;
    public ulong OpRead64(uint off) { ulong v = *(ulong*)(_bar + _opBase + off); CPU.MemoryBarrier(); return v; }
    public void OpWrite64(uint off, ulong v) => *(ulong*)(_bar + _opBase + off) = v;

    public uint RtRead(uint off) { uint v = *(uint*)(_bar + _rtBase + off); CPU.MemoryBarrier(); return v; }
    public void RtWrite(uint off, uint v) => *(uint*)(_bar + _rtBase + off) = v;
    public ulong RtRead64(uint off) { ulong v = *(ulong*)(_bar + _rtBase + off); CPU.MemoryBarrier(); return v; }
    public void RtWrite64(uint off, ulong v) => *(ulong*)(_bar + _rtBase + off) = v;

    public void RingDoorbell(uint slot, uint target)
    {
        *(uint*)(_bar + _dbBase + 4 * slot) = target;
    }

    public uint PortSc(int port)
    {
        uint v = *(uint*)(_bar + _opBase + XhciReg.PORTSC_BASE + (uint)(0x10 * port));
        CPU.MemoryBarrier();
        return v;
    }
    public void SetPortSc(int port, uint value) => *(uint*)(_bar + _opBase + XhciReg.PORTSC_BASE + (uint)(0x10 * port)) = value;

    public uint ContextStride => _contextSize;

    // ========================================================================
    // Time helpers (tick-based with a spin cap fallback)
    // ========================================================================

    private static ulong NowMs()
    {
        // Prefer HPET: the APIC tick counter only advances once the
        // scheduler's timer is up, which is not guaranteed while boot-time
        // drivers (usb) run. HPET runs from early boot.
        if (HPET.IsInitialized)
            return HPET.TicksToNanoseconds(HPET.ReadCounter()) / 1_000_000UL;
        ulong freq = ProtonOS.Arch.Arch.GetTimerFrequency();
        if (freq == 0)
            return 0;
        return ProtonOS.Arch.Arch.GetTickCount() * 1000 / freq;
    }

    /// <summary>Poll until predicate true or timeout; returns final predicate.</summary>
    private bool WaitUntil(Func<bool> predicate, uint timeoutMs)
    {
        ulong start = NowMs();
        uint spin = 0;
        uint checks = 0;
        while (true)
        {
            PollEvents();
            if (predicate())
                return true;
            ProtonOS.Arch.CPU.Pause();
            spin++;
            if (spin >= 100_000u)
            {
                spin = 0;
                checks++;
                ulong now = NowMs();
                if (now != 0)
                {
                    if (now >= start && (now - start) >= timeoutMs)
                        return predicate();
                }
                // Absolute backstop: never spin forever even without a clock.
                if (checks >= 20_000u)
                    return predicate();
            }
        }
    }

    // ========================================================================
    // Initialization
    // ========================================================================

    /// <summary>
    /// Bring the controller up: reset, rings, scratchpad, run.
    /// `bar` is the mapped BAR0 virtual base.
    /// </summary>
    public XhciStatus Init(byte* bar)
    {
        _bar = bar;

        // Capability registers. Decode caplen/version from the first dword:
        // small unaligned loads through the raw pointer proved unreliable
        // in this code generator, so everything derives from 32-bit reads.
        uint capDword = Read32(0);
        uint caplen = capDword & 0xFFu;
        HciVersion = (ushort)(capDword >> 16);
        uint hcsparams1 = Read32(XhciReg.HCSPARAMS1);
        MaxSlots = (int)(hcsparams1 & 0xFF);
        MaxPorts = (int)(hcsparams1 >> 24);
        uint hcsparams2 = Read32(XhciReg.HCSPARAMS2);
        _scratchpadCount = ((hcsparams2 >> 27) & 0x1F) | ((hcsparams2 >> 21) & 0x1E);
        uint hccparams1 = Read32(XhciReg.HCCPARAMS1);
        bool csz = (hccparams1 & 0x4) != 0;
        _contextSize = csz ? 64u : 32u;

        if (MaxSlots < 1 || MaxSlots > MaxSlotsSupported)
            MaxSlots = MaxSlots < 1 ? 1 : MaxSlotsSupported;
        if (MaxPorts < 1 || MaxPorts > MaxPortsSupported)
            MaxPorts = MaxPorts < 1 ? 1 : MaxPortsSupported;

        _opBase = caplen;
        _rtBase = Read32(XhciReg.RTSOFF);
        _dbBase = Read32(XhciReg.DBOFF);

        Log("xHCI v" + ((HciVersion >> 8) & 0xFF).ToString() + "." + (HciVersion & 0xFF).ToString()
            + " slots=" + MaxSlots.ToString() + " ports=" + MaxPorts.ToString()
            + " ctx=" + _contextSize.ToString() + " scratchpad=" + _scratchpadCount.ToString()
            + " op=0x" + _opBase.ToString("X", null)
            + " rt=0x" + _rtBase.ToString("X", null)
            + " db=0x" + _dbBase.ToString("X", null));

        // Wait for controller not ready to clear.
        if (!WaitUntil(() => (OpRead(XhciReg.USBSTS) & (1u << 11)) == 0, 500))
        {
            Log("controller not ready (CNR) - hardware fault");
            return XhciStatus.HardwareFault;
        }

        // Reset.
        OpWrite(XhciReg.USBCMD, 1u << 1);    // HCRST
        if (!WaitUntil(() => (OpRead(XhciReg.USBCMD) & (1u << 1)) == 0
                          && (OpRead(XhciReg.USBSTS) & (1u << 11)) == 0, 2000))
        {
            Log("host controller reset did not complete");
            return XhciStatus.HardwareFault;
        }

        // Halt before programming (clear RS).
        OpWrite(XhciReg.USBCMD, 0);
        if (!WaitUntil(() => (OpRead(XhciReg.USBSTS) & 1) != 0, 500))
        {
            // HCH must become 1 when halted; continue defensively.
        }

        // Allocate the structural DMA pages.
        _cmdRing = UsbDma.Allocate(4096);
        _evtRing = UsbDma.Allocate(4096);
        _erst = UsbDma.Allocate(4096);
        _dcbaa = UsbDma.Allocate(4096);
        _inputCtx = UsbDma.Allocate(4096);
        if (_cmdRing.Physical == 0 || _evtRing.Physical == 0 || _erst.Physical == 0
            || _dcbaa.Physical == 0 || _inputCtx.Physical == 0)
        {
            Log("DMA allocation failed");
            return XhciStatus.HardwareFault;
        }

        // Scratchpad buffers (if the controller needs them).
        if (_scratchpadCount > 0)
        {
            uint arrayBytes = ((_scratchpadCount * 8) + 63) / 64 * 64;
            _scratchpadArray = UsbDma.Allocate(arrayBytes);
            _scratchpages = UsbDma.Allocate(_scratchpadCount * 4096);
            if (_scratchpadArray.Physical == 0 || _scratchpages.Physical == 0)
            {
                Log("scratchpad allocation failed");
                return XhciStatus.HardwareFault;
            }
            ulong* arr = (ulong*)_scratchpadArray.Virtual;
            for (uint i = 0; i < _scratchpadCount; i++)
                arr[i] = _scratchpages.Physical + i * 4096UL;
            ((ulong*)_dcbaa.Virtual)[0] = _scratchpadArray.Physical;
        }

        // Device context base address array.
        OpWrite64(XhciReg.DCBAAP, _dcbaa.Physical);

        // Slots: enable up to CONFIG value.
        int enabledSlots = MaxSlots;
        OpWrite(XhciReg.CONFIG, (uint)(enabledSlots & 0xFF));

        // Command ring (ring cycle state starts at 1).
        _cmdEnqueue = 0;
        _cmdCycle = true;
        WriteLinkTrb(_cmdRing, RingTrbs - 1, true);
        OpWrite64(XhciReg.CRCR, _cmdRing.Physical | 1UL);

        // Event ring: one segment described by ERST[0].
        // Entry layout: qword0 = segment base, dword at +8 = segment size
        // in TRBs (16..4096), dword at +12 reserved.
        _evtIndex = 0;
        _evtCycle = true;
        ulong* erst = (ulong*)_erst.Virtual;
        erst[0] = _evtRing.Physical;              // ring segment base
        erst[1] = EventRingTrbs;                  // segment size (dword at +8)
        RtWrite(XhciReg.ERSTSZ, 1);
        RtWrite64(XhciReg.ERSTBA, _erst.Physical);
        RtWrite64(XhciReg.ERDP, _evtRing.Physical);   // segment 0, no EHB
        RtWrite(XhciReg.IMAN, 0x3);                   // IE=0 (polled), clear IP
        RtWrite(XhciReg.IMOD, 0);

        // Allocate the slot table.
        _slots = (XhciSlot*)Memory.HeapAllocator.Alloc((ulong)(sizeof(XhciSlot) * (MaxSlots + 1)));
        if (_slots == null)
        {
            Log("slot table allocation failed");
            return XhciStatus.HardwareFault;
        }
        for (int i = 0; i <= MaxSlots; i++)
        {
            _slots[i].Enabled = false;
            _slots[i].Rings = null;
        }
        _slotsAllocated = true;

        // Power the ports and run.
        for (int p = 0; p < MaxPorts; p++)
        {
            uint sc = PortSc(p);
            if ((sc & XhciPortsc.PP) == 0)
                SetPortSc(p, (sc & ~(XhciPortsc.CSC | XhciPortsc.PEC | XhciPortsc.PRC
                        | XhciPortsc.WRC | XhciPortsc.PLC | XhciPortsc.CEC)) | XhciPortsc.PP);
        }

        OpWrite(XhciReg.USBCMD, 1);   // RS, no interrupts (polled)
        if (!WaitUntil(() => (OpRead(XhciReg.USBSTS) & 1) == 0, 1000))
        {
            Log("controller did not start running");
            return XhciStatus.HardwareFault;
        }

        Initialized = true;
        Log("xHCI running");
        return XhciStatus.Ok;
    }

    private void WriteLinkTrb(UsbDma ring, uint index, bool cycle)
    {
        XhciTrb* trb = (XhciTrb*)(ring.Virtual + index * 16);
        trb->D0 = (uint)ring.Physical;
        trb->D1 = (uint)(ring.Physical >> 32);
        trb->D2 = 0;
        uint d3 = XhciTrbType.Link << 10;
        d3 |= 1u << 1;        // TC: toggle cycle when the HC follows the link
        if (cycle)
            d3 |= 1;
        trb->D3 = d3;
    }

    private void Log(string message)
    {
        DebugConsole.Write("[usb-xhci] ");
        DebugConsole.WriteLine(message);
    }

    // ========================================================================
    // Command ring
    // ========================================================================

    private XhciTrb* EnqueueCommand(uint type, ulong param, uint extraD3)
    {
        XhciTrb* trb = (XhciTrb*)(_cmdRing.Virtual + _cmdEnqueue * 16);
        trb->D0 = (uint)param;
        trb->D1 = (uint)(param >> 32);
        trb->D2 = 0;
        uint d3 = (type & 0x3F) << 10;
        d3 |= extraD3 & ~0x3FFu;             // keep slot id etc., drop type/cycle bits
        if (_cmdCycle)
            d3 |= 1;
        trb->D3 = d3;

        ulong phys = _cmdRing.Physical + _cmdEnqueue * 16UL;
        _cmdEnqueue++;
        if (_cmdEnqueue == RingTrbs - 1)
        {
            WriteLinkTrb(_cmdRing, RingTrbs - 1, _cmdCycle);
            _cmdEnqueue = 0;
            _cmdCycle = !_cmdCycle;
        }
        _cmdPendingPhys = phys;
        return trb;
    }

    private XhciStatus RunCommand(uint type, ulong param, uint d3Extra, out uint slotId)
    {
        slotId = 0;
        _cmdDone = false;
        EnqueueCommand(type, param, d3Extra);
        RingDoorbell(0, 0);

        if (!WaitUntil(() => _cmdDone, 5000))
        {
            Log("command timeout (type=" + ((int)type).ToString() + ")");
            return XhciStatus.Timeout;
        }

        byte code = _cmdCompletion.CompletionCode;
        slotId = (_cmdCompletion.D3 >> 24) & 0xFF;
        if (code == XhciCompletion.Success)
            return XhciStatus.Ok;
        if (code == XhciCompletion.NoSlotsAvailable)
            return XhciStatus.NoSlot;
        if (code == XhciCompletion.ContextStateError)
            return XhciStatus.ContextError;
        Log("command " + ((int)type).ToString() + " failed, completion " + ((int)code).ToString());
        return XhciStatus.TrbError;
    }

    // ========================================================================
    // Event ring
    // ========================================================================

    /// <summary>Drain the event ring; safe to call at any time (idempotent).</summary>
    public void PollEvents()
    {
        if (_evtRing.Virtual == null)
            return;

        uint drained = 0;
        while (drained < 2 * EventRingTrbs)
        {
            XhciTrb* trb = (XhciTrb*)(_evtRing.Virtual + _evtIndex * 16);
            if (trb->Cycle != (_evtCycle ? 1u : 0u))
                break;

            uint type = trb->Type;
            if (type == XhciTrbType.CommandCompletionEvent)
            {
                if (trb->Param == _cmdPendingPhys || _cmdPendingPhys == 0)
                {
                    _cmdCompletion = *trb;
                    _cmdDone = true;
                }
            }
            else if (type == XhciTrbType.TransferEvent)
            {
                if (_completionCount < CompletionQueueSize)
                {
                    var c = new XhciXferCompletion();
                    c.SlotId = (byte)trb->SlotId;
                    c.Dci = (byte)((trb->D3 >> 16) & 0x1F);
                    c.Code = (byte)(trb->D2 >> 24);
                    c.Transferred = (int)(trb->D2 & 0xFFFFFF);
                    _completions[_completionCount++] = c;
                }
            }
            else if (type == XhciTrbType.PortStatusChangeEvent)
            {
                uint port = (trb->D2 >> 24) & 0xFF;
                if (port >= 1 && port <= 64)
                    PortChangePending |= 1UL << (int)port;
            }
            else if (type == 2 /* Device Notification */)
            {
                // Ignore (no hubs decoded yet).
            }

            _evtIndex++;
            if (_evtIndex == EventRingTrbs)
            {
                _evtIndex = 0;
                _evtCycle = !_evtCycle;
            }
            drained++;
        }

        // Advance ERDP and clear EHB.
        RtWrite64(XhciReg.ERDP, _evtRing.Physical + _evtIndex * 16UL + 0x8);
    }

    // ========================================================================
    // Port management
    // ========================================================================

    public bool PortConnected(int port) => (PortSc(port) & XhciPortsc.CCS) != 0;

    /// <summary>Snapshot of CCS bits for every root port (bit p = port p).</summary>
    public ulong BuildConnectedMask()
    {
        ulong m = 0;
        for (int p = 0; p < MaxPorts; p++)
        {
            uint sc = PortSc(p);
            if ((sc & XhciPortsc.CCS) != 0)
                m |= 1UL << p;
        }
        return m;
    }

    public UsbSpeed PortSpeed(int port)
    {
        uint speed = (PortSc(port) & XhciPortsc.Speed) >> (int)XhciPortsc.SPEED_SHIFT;
        return (UsbSpeed)(speed & 0xF);
    }

    /// <summary>Reset a port and wait for it to come up enabled.</summary>
    public XhciStatus ResetPort(int port, out UsbSpeed speed)
    {
        speed = UsbSpeed.Unknown;
        uint sc = PortSc(port);
        if ((sc & XhciPortsc.CCS) == 0)
            return XhciStatus.NotConnected;

        // Clear change bits first (RW1C), keep PP, no PR.
        uint clear = sc | XhciPortsc.CSC | XhciPortsc.PEC | XhciPortsc.PRC
                   | XhciPortsc.WRC | XhciPortsc.PLC | XhciPortsc.CEC;
        clear &= ~XhciPortsc.PR;
        SetPortSc(port, clear);

        // Assert reset (keep PP; clear RW1C bits).
        sc = PortSc(port);
        uint v = sc & ~(XhciPortsc.CSC | XhciPortsc.PEC | XhciPortsc.PRC
                        | XhciPortsc.WRC | XhciPortsc.PLC | XhciPortsc.CEC);
        SetPortSc(port, v | XhciPortsc.PR);

        // Wait for reset-complete.
        PollEvents();
        if (!WaitUntil(() => (PortSc(port) & XhciPortsc.PRC) != 0, 500))
        {
            SetPortSc(port, PortSc(port) & ~XhciPortsc.PR);
            Log("port " + port.ToString() + " reset timed out");
            return XhciStatus.Timeout;
        }

        // Acknowledge PRC (and CSC which can set again on re-connect).
        sc = PortSc(port);
        SetPortSc(port, sc | XhciPortsc.PRC | XhciPortsc.CSC);

        // Wait for enabled (PED). Some controllers need a moment.
        WaitUntil(() => (PortSc(port) & XhciPortsc.PED) != 0, 500);

        sc = PortSc(port);
        speed = (UsbSpeed)((sc & XhciPortsc.Speed) >> (int)XhciPortsc.SPEED_SHIFT);
        if ((sc & XhciPortsc.PED) == 0 && speed == UsbSpeed.Unknown)
            return XhciStatus.NotConnected;
        return XhciStatus.Ok;
    }

    /// <summary>Acknowledge and clear the stored port change bits.</summary>
    public void AckPortChange(int port)
    {
        uint sc = PortSc(port);
        SetPortSc(port, sc | XhciPortsc.CSC | XhciPortsc.PEC | XhciPortsc.PRC
                     | XhciPortsc.WRC | XhciPortsc.PLC | XhciPortsc.CEC);
        if (port >= 1 && port <= 64)
            PortChangePending &= ~(1UL << port);
    }

    // ========================================================================
    // Slots and endpoints
    // ========================================================================

    public XhciStatus EnableSlot(out byte slotId)
    {
        uint s = 0;
        var st = RunCommand(XhciTrbType.EnableSlotCmd, 0, 0, out s);
        slotId = (byte)s;
        if (st != XhciStatus.Ok || slotId == 0 || slotId > MaxSlots)
            return st == XhciStatus.Ok ? XhciStatus.NoSlot : st;

        EnsureSlotRings(slotId);
        _slots[slotId].Enabled = true;
        _slots[slotId].SlotState = 0;
        return XhciStatus.Ok;
    }

    private void EnsureSlotRings(byte slotId)
    {
        if (_slots[slotId].Rings != null)
            return;
        XhciEpRing* rings = (XhciEpRing*)Memory.HeapAllocator.Alloc((ulong)(sizeof(XhciEpRing) * DciCount));
        for (int i = 0; i < DciCount; i++)
            rings[i].Valid = false;
        _slots[slotId].Rings = rings;
    }

    public byte SlotState(byte slotId) => _slots[slotId].SlotState;

    /// <summary>Reset (free) an endpoint ring so it can be re-used.</summary>
    public XhciStatus ResetEndpointRing(byte slotId, byte dci)
    {
        var ring = &_slots[slotId].Rings[dci];
        if (!ring->Valid)
            return XhciStatus.Ok;
        UsbDma dma = ring->Dma;
        UsbDma.Free(ref dma);
        ring->Valid = false;
        ring->Dma = dma;
        uint s;
        return RunCommand(XhciTrbType.StopEndpointCmd, 0, ((uint)dci << 16) | ((uint)slotId << 24), out s);
    }

    private XhciEpRing* GetOrCreateRing(byte slotId, byte dci, byte epType, ushort mps, byte interval)
    {
        var ring = &_slots[slotId].Rings[dci];
        if (ring->Valid)
            return ring;
        ring->Dma = UsbDma.Allocate(4096);
        if (ring->Dma.Physical == 0)
            return null;
        ring->EnqueueIndex = 0;
        ring->Cycle = true;
        ring->Valid = true;
        ring->EpType = epType;
        ring->MaxPacketSize = mps;
        ring->Interval = interval;
        WriteLinkTrb(ring->Dma, RingTrbs - 1, true);
        return ring;
    }

    // ========================================================================
    // Input context builders (slot + endpoint contexts)
    // ========================================================================

    private byte* InputCtxBase => _inputCtx.Virtual;

    private void ClearInputCtx()
    {
        for (uint i = 0; i < 2048; i++)
            _inputCtx.Virtual[i] = 0;
    }

    private uint* IccDwords => (uint*)InputCtxBase;

    private byte* SlotCtxPtr => InputCtxBase + _contextSize;

    private byte* EpCtxPtr(uint dci) => InputCtxBase + _contextSize * (1 + dci);

    private void SetInputAdd(uint mask) => IccDwords[1] = mask;
    private void SetInputDrop(uint mask) => IccDwords[0] = mask;
    private void SetInputConfigValue(byte value) => IccDwords[2] = (uint)value << 8;

    private void WriteSlotCtx(byte slotId, byte speed, byte rootPort, bool hub, byte contextEntries)
    {
        uint* sc = (uint*)SlotCtxPtr;
        // d0: route 0 | speed | context entries
        sc[0] = ((uint)speed << 20) | 0;
        if (hub)
            sc[0] |= 1u << 26;
        sc[0] |= (uint)(contextEntries & 0x1F) << 27;
        // d1: max exit latency [15:0] | root hub port number [23:16]
        sc[1] = (uint)rootPort << 16;
        // d2: TT fields + interrupter target (0)
        sc[2] = 0;
        sc[3] = 0;
        _ = slotId;
    }

    private void WriteEpCtx(uint dci, byte epType, ushort mps, byte interval, ulong ringPhys, bool interruptLike)
    {
        uint* ep = (uint*)EpCtxPtr(dci);
        // d0: interval [23:16] (for interrupt endpoints)
        ep[0] = (uint)interval << 16;
        // d1: CErr=3 (bits 2:1) | EP Type (bits 5:3) | MaxPacketSize [31:16]
        ep[1] = (3u << 1) | ((uint)epType << 3) | ((uint)mps << 16);
        // d2/d3: TR dequeue pointer with DCS=1 (bit 0)
        ep[2] = ((uint)ringPhys & 0xFFFFFFF0u) | 1u;
        ep[3] = (uint)(ringPhys >> 32);
        // d4: average TRB length
        ep[4] = interruptLike ? 64u : (dci == 1 ? 8u : 1024u);
    }

    /// <summary>
    /// Address a device on a root port (Assign new slot's address).
    /// maxPacket0: EP0 max packet size (8 for low/full-speed guess, else from speed).
    /// </summary>
    public XhciStatus AddressDevice(byte slotId, byte rootPort, byte speed, ushort maxPacket0, bool bsr)
    {
        var ring = GetOrCreateRing(slotId, 1, 4 /* control */, maxPacket0, 0);
        if (ring == null)
            return XhciStatus.HardwareFault;

        ClearInputCtx();
        WriteSlotCtx(slotId, speed, rootPort, false, 1);
        WriteEpCtx(1, 4 /* control EP */, maxPacket0, 0, ring->Dma.Physical, false);
        SetInputAdd(1 | (1u << 1));
        SetInputDrop(0);

        uint d3 = ((uint)slotId << 24);
        if (bsr)
            d3 |= 1u << 9;
        uint s;
        var st = RunCommand(XhciTrbType.AddressDeviceCmd, _inputCtx.Physical, d3, out s);
        if (st == XhciStatus.Ok)
            _slots[slotId].SlotState = 3;
        return st;
    }

    /// <summary>Update EP0 max packet size after reading the device descriptor.</summary>
    public XhciStatus EvaluateContextMps(byte slotId, byte rootPort, byte speed, ushort mps)
    {
        var ring = &_slots[slotId].Rings[1];
        if (!ring->Valid)
            return XhciStatus.ContextError;

        ClearInputCtx();
        WriteSlotCtx(slotId, speed, rootPort, false, 1);
        WriteEpCtx(1, 4, mps, 0, ring->Dma.Physical, false);
        SetInputAdd(1u << 1);   // add EP0 only
        SetInputDrop(0);
        ring->MaxPacketSize = mps;

        uint s;
        return RunCommand(XhciTrbType.EvaluateContextCmd, _inputCtx.Physical, (uint)slotId << 24, out s);
    }

    /// <summary>Program the interfaces/endpoints of a configuration.</summary>
    public XhciStatus ConfigureEndpoints(byte slotId, byte speed, byte rootPort, byte configValue,
        byte* epTypes, byte* epNumbers, bool* epIsIn, ushort* epMps, byte* epIntervals, int epCount)
    {
        ClearInputCtx();
        uint addMask = 1u;   // slot context

        byte maxDci = 1;
        for (int i = 0; i < epCount; i++)
        {
            byte dci = (byte)(epNumbers[i] * 2 + (epIsIn[i] ? 1 : 0));
            if (dci < 2)
                continue;
            var ring = GetOrCreateRing(slotId, dci, epTypes[i], epMps[i], epIntervals[i]);
            if (ring == null)
                return XhciStatus.HardwareFault;
            WriteEpCtx(dci, epTypes[i], epMps[i], epIntervals[i], ring->Dma.Physical,
                epTypes[i] == 3 || epTypes[i] == 7);
            addMask |= 1u << dci;
            if (dci > maxDci)
                maxDci = dci;
        }

        WriteSlotCtx(slotId, speed, rootPort, false, (byte)(maxDci + 1));
        SetInputAdd(addMask);
        SetInputDrop(0);
        SetInputConfigValue(configValue);

        uint s;
        var st = RunCommand(XhciTrbType.ConfigureEndpointCmd, _inputCtx.Physical, (uint)slotId << 24, out s);
        if (st == XhciStatus.Ok)
            _slots[slotId].SlotState = 4;
        return st;
    }

    public XhciStatus DisableSlot(byte slotId)
    {
        uint s;
        var st = RunCommand(XhciTrbType.DisableSlotCmd, 0, (uint)slotId << 24, out s);
        if (_slots[slotId].Rings != null)
        {
            for (int i = 0; i < DciCount; i++)
            {
                var ring = &_slots[slotId].Rings[i];
                if (ring->Valid)
                {
                    UsbDma dma = ring->Dma;
                    UsbDma.Free(ref dma);
                    ring->Valid = false;
                }
            }
        }
        _slots[slotId].Enabled = false;
        _slots[slotId].SlotState = 0;
        return st;
    }

    // ========================================================================
    // Transfers
    // ========================================================================

    private XhciTrb* EnqueueTrb(XhciEpRing* ring, ulong param, uint status, uint type,
        bool ioc, bool idt, bool ch, bool dirIn)
    {
        XhciTrb* trb = (XhciTrb*)(ring->Dma.Virtual + ring->EnqueueIndex * 16);
        trb->D0 = (uint)param;
        trb->D1 = (uint)(param >> 32);
        trb->D2 = status;
        uint d3 = (type & 0x3F) << 10;
        if (ch)
            d3 |= 1u << 4;
        if (ioc)
            d3 |= 1u << 5;
        if (idt)
            d3 |= 1u << 6;
        if (dirIn)
            d3 |= 1u << 16;
        if (ring->Cycle)
            d3 |= 1;
        trb->D3 = d3;

        ring->EnqueueIndex++;
        if (ring->EnqueueIndex == RingTrbs - 1)
        {
            WriteLinkTrb(ring->Dma, RingTrbs - 1, ring->Cycle);
            ring->EnqueueIndex = 0;
            ring->Cycle = !ring->Cycle;
        }
        return trb;
    }

    /// <summary>
    /// Submit chunked data TRBs for a physically-contiguous buffer.
    /// Chunks stay page-aligned so no TRB data span crosses a 64KiB boundary.
    /// </summary>
    private void EnqueueDataChunks(XhciEpRing* ring, ulong phys, int length, bool dirIn, bool type3)
    {
        int remaining = length;
        ulong addr = phys;
        while (remaining > 0)
        {
            int chunk = 4096 - (int)(addr & 0xFFF);
            if (chunk > remaining)
                chunk = remaining;
            bool last = (chunk == remaining);
            bool ch = !last;      // chain all but the last
            bool ioc = last;
            // For control data stage the last TRB still needs CH=0; for
            // control transfers IOC belongs on the status stage only.
            if (type3)
                EnqueueTrb(ring, addr, (uint)chunk, XhciTrbType.DataStage, false, false, ch, dirIn);
            else
                EnqueueTrb(ring, addr, (uint)chunk, XhciTrbType.Normal, ioc, false, ch, dirIn);
            addr += (ulong)chunk;
            remaining -= chunk;
        }
    }

    /// <summary>True when a completion for (slot, dci) is queued.</summary>
    public bool HasCompletion(byte slotId, byte dci)
    {
        for (int i = 0; i < _completionCount; i++)
            if (_completions[i].SlotId == slotId && _completions[i].Dci == dci)
                return true;
        return false;
    }

    /// <summary>Remove and return the first queued completion for (slot, dci).</summary>
    public bool TakeCompletion(byte slotId, byte dci, out XhciXferCompletion completion)
    {
        for (int i = 0; i < _completionCount; i++)
        {
            if (_completions[i].SlotId == slotId && _completions[i].Dci == dci)
            {
                completion = _completions[i];
                for (int j = i; j < _completionCount - 1; j++)
                    _completions[j] = _completions[j + 1];
                _completionCount--;
                return true;
            }
        }
        completion = default;
        return false;
    }

    private static XhciStatus MapCompletionCode(byte code)
    {
        if (code == XhciCompletion.Success)
            return XhciStatus.Ok;
        if (code == XhciCompletion.ShortPacket)
            return XhciStatus.ShortPacket;
        if (code == XhciCompletion.StallError)
            return XhciStatus.Stall;
        if (code == XhciCompletion.BabbleDetected)
            return XhciStatus.Babble;
        if (code == XhciCompletion.TrbError)
            return XhciStatus.TrbError;
        return XhciStatus.TrbError;
    }

    private XhciStatus WaitTransfer(byte slotId, byte dci, uint timeoutMs, int expectedLength,
        bool isControl, out int bytesTransferred, out byte code)
    {
        bytesTransferred = 0;
        code = 0;
        if (!WaitUntil(() => HasCompletion(slotId, dci), timeoutMs))
            return XhciStatus.Timeout;

        XhciXferCompletion c;
        if (!TakeCompletion(slotId, dci, out c))
            return XhciStatus.Timeout;
        bytesTransferred = expectedLength - c.Transferred;
        if (bytesTransferred < 0)
            bytesTransferred = 0;
        code = c.Code;
        _ = isControl;
        return MapCompletionCode(c.Code);
    }

    /// <summary>
    /// Control transfer (slot-addressed, EP0). The data buffer must live in
    /// DMA memory; pass the physical address.
    /// </summary>
    public XhciTransferResult ControlTransfer(byte slotId, in UsbSetupPacket setup,
        ulong dataPhys, int length)
    {
        var result = new XhciTransferResult();
        var ring = &_slots[slotId].Rings[1];
        if (!ring->Valid)
        {
            result.Status = XhciStatus.ContextError;
            return result;
        }

        bool dirIn = (setup.RequestType & 0x80) != 0;
        // TRT: 0 = no data stage, 2 = IN data, 3 = OUT data
        uint trt = length == 0 ? 0u : (dirIn ? 2u : 3u);

        // Setup stage (IDT=1, no IOC).
        XhciTrb* setupTrb = (XhciTrb*)(ring->Dma.Virtual + ring->EnqueueIndex * 16);
        EnqueueTrb(ring, 0, 8, XhciTrbType.SetupStage, false, true, false, false);
        setupTrb->D0 = setup.RequestType | ((uint)setup.Request << 8)
                     | ((uint)setup.Value << 16);
        setupTrb->D1 = setup.Index | ((uint)setup.Length << 16);

        // Fix TRT bits [17:16] of the setup TRB.
        uint d3 = setupTrb->D3 & ~(3u << 16);
        d3 |= trt << 16;
        setupTrb->D3 = d3;

        // Data stage.
        if (length > 0)
            EnqueueDataChunks(ring, dataPhys, length, dirIn, true);

        // Status stage (direction is inverted, IOC=1).
        EnqueueTrb(ring, 0, 0, XhciTrbType.StatusStage, true, false, false, !dirIn);

        RingDoorbell(slotId, 1);

        var st = WaitTransfer(slotId, 1, 3000, length, true, out int transferred, out byte code);
        result.Status = st;
        result.BytesTransferred = transferred;
        result.CompletionCode = code;

        // On failure, reset the endpoint so the ring can be reused.
        if (st == XhciStatus.Stall || st == XhciStatus.TrbError)
            RecoverEndpoint(slotId, 1);
        return result;
    }

    /// <summary>Bulk or interrupt transfer on a slot endpoint ring.</summary>
    public XhciTransferResult DataTransfer(byte slotId, byte dci, ulong dataPhys, int length, bool dirIn)
    {
        var result = new XhciTransferResult();
        var ring = &_slots[slotId].Rings[dci];
        if (!ring->Valid)
        {
            result.Status = XhciStatus.ContextError;
            return result;
        }

        EnqueueDataChunks(ring, dataPhys, length, dirIn, false);
        RingDoorbell(slotId, dci);

        var st = WaitTransfer(slotId, dci, 5000, length, false, out int transferred, out byte code);
        result.Status = st;
        result.BytesTransferred = transferred;
        result.CompletionCode = code;
        if (st == XhciStatus.Stall || st == XhciStatus.TrbError || st == XhciStatus.Babble)
            RecoverEndpoint(slotId, (byte)dci);
        return result;
    }

    /// <summary>
    /// Submit an interrupt/bulk transfer without waiting. The completion is
    /// consumed later with <see cref="TryTakeTransfer"/> (used by the polled
    /// HID interrupt endpoints).
    /// </summary>
    public bool SubmitDataAsync(byte slotId, byte dci, ulong dataPhys, int length, bool dirIn)
    {
        var ring = &_slots[slotId].Rings[dci];
        if (!ring->Valid)
            return false;
        EnqueueDataChunks(ring, dataPhys, length, dirIn, false);
        RingDoorbell(slotId, dci);
        return true;
    }

    /// <summary>Non-blocking completion check for an async transfer.</summary>
    public bool TryTakeTransfer(byte slotId, byte dci, int expectedLength, out XhciTransferResult result)
    {
        result = new XhciTransferResult();
        PollEvents();
        XhciXferCompletion c;
        if (!TakeCompletion(slotId, dci, out c))
            return false;
        result.BytesTransferred = expectedLength - c.Transferred;
        if (result.BytesTransferred < 0)
            result.BytesTransferred = 0;
        result.CompletionCode = c.Code;
        result.Status = MapCompletionCode(c.Code);
        return true;
    }

    private void RecoverEndpoint(byte slotId, byte dci)
    {
        uint s;
        RunCommand(XhciTrbType.ResetEndpointCmd, 0, ((uint)dci << 16) | ((uint)slotId << 24), out s);

        // Move the endpoint's TR dequeue pointer to the next empty slot so
        // the stopped ring can be re-used.
        var ring = &_slots[slotId].Rings[dci];
        if (ring->Valid)
        {
            ulong next = ring->Dma.Physical + ring->EnqueueIndex * 16UL;
            uint d3 = ((uint)slotId << 24) | ((uint)dci << 16);
            if (ring->Cycle)
                d3 |= 1u;    // DCS matches the producer cycle state
            RunCommand(XhciTrbType.SetTrDequeueCmd, next, d3, out s);
        }
    }

    /// <summary>Recover both bulk endpoints after a BOT failure.</summary>
    public void ResetBulkEndpoints(byte slotId, byte dciIn, byte dciOut)
    {
        RecoverEndpoint(slotId, dciIn);
        RecoverEndpoint(slotId, dciOut);
    }

    /// <summary>Speed encoding used in slot contexts.</summary>
    public static byte SpeedToSlotValue(UsbSpeed speed)
    {
        switch (speed)
        {
            case UsbSpeed.Low: return 2;
            case UsbSpeed.Full: return 1;
            case UsbSpeed.High: return 3;
            case UsbSpeed.Super: return 4;
            case UsbSpeed.SuperPlus: return 5;
            default: return 1;
        }
    }

    /// <summary>Default EP0 max packet for the speed (before the descriptor).</summary>
    public static ushort DefaultMaxPacket0(UsbSpeed speed)
    {
        switch (speed)
        {
            case UsbSpeed.Low: return 8;
            case UsbSpeed.Full: return 8;    // 8 works for the first descriptor read
            case UsbSpeed.High: return 64;
            default: return 64;              // SuperSpeed
        }
    }
}
