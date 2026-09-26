// NeutrinoOS kernel - USB CDC-ACM serial driver (Phase 9 Task 1).
//
// Communications Device Class Abstract Control Model: line-coding setup
// (SET_LINE_CODING / SET_CONTROL_LINE_STATE), bulk data endpoints and the
// interrupt notification endpoint. A bound adapter exposes byte-level
// serial I/O (UsbSerial.Write / polled RX ring); the /dev/ttyUSB0 VFS
// node wires onto this in the storage/device-node milestone (see
// docs/PHASE9-USB.md).

using System;
using ProtonOS.Usb.Xhci;

namespace ProtonOS.Usb;

/// <summary>A bound CDC-ACM adapter.</summary>
public sealed unsafe class UsbSerialPort
{
    public UsbDevice Device = null!;
    public int ControlInterface;
    public byte DataInterface;
    public byte BulkInDci;
    public byte BulkOutDci;
    public byte NotifyDci;
    public bool HasNotify;
    public ushort BulkInMps = 64;
    public ushort BulkOutMps = 64;

    public UsbDma Rx;
    public UsbDma Tx;
    public bool RxPending;
    public int RxLength = 64;

    // Polled receive ring (fed by UsbSerial.Poll).
    public const int RingSize = 1024;
    public byte* Ring = null!;
    public int RingHead;
    public int RingTail;

    public bool Ready;
    public string Name = "ttyUSB0";
}

/// <summary>USB CDC-ACM serial driver.</summary>
public static unsafe class UsbSerial
{
    private static void UsbLog(string m) => UsbTrace.Log(m);

    private const int MaxPorts = 4;
    private static UsbSerialPort?[] _ports = new UsbSerialPort?[MaxPorts];
    private static int _portCount;

    public static int PortCount => _portCount;

    public static UsbSerialPort? GetPort(int index)
    {
        if (index < 0 || index >= _portCount)
            return null;
        return _ports[index];
    }

    public static bool Bind(UsbDevice device, int interfaceIndex, byte subclass, byte protocol)
    {
        // CDC ACM control interface: class 0x02, subclass 0x02 (ACM).
        if (subclass != 0x02)
            return false;
        _ = protocol;

        // Locate the CDC data interface (class 0x0A).
        int dataIface = -1;
        for (int i = 0; i < device.Config.InterfaceCount; i++)
            if (device.Config.Interfaces[i].InterfaceClass == UsbClassCode.CdcData)
            {
                dataIface = i;
                break;
            }
        if (dataIface < 0)
            return false;

        UsbEndpointDescriptor epIn, epOut;
        byte dciIn, dciOut;
        if (!device.TryGetEndpoint(dataIface, 2, true, out epIn, out dciIn)
            || !device.TryGetEndpoint(dataIface, 2, false, out epOut, out dciOut))
            return false;

        if (_portCount >= MaxPorts)
            return false;

        var port = new UsbSerialPort();
        port.Device = device;
        port.ControlInterface = interfaceIndex;
        port.DataInterface = (byte)dataIface;
        port.BulkInDci = dciIn;
        port.BulkOutDci = dciOut;
        port.BulkInMps = epIn.PacketSize == 0 ? (ushort)64 : epIn.PacketSize;
        port.BulkOutMps = epOut.PacketSize == 0 ? (ushort)64 : epOut.PacketSize;

        UsbEndpointDescriptor notifEp;
        byte notifDci;
        if (device.TryGetEndpoint(interfaceIndex, 3, true, out notifEp, out notifDci))
        {
            port.HasNotify = true;
            port.NotifyDci = notifDci;
        }

        port.Rx = UsbDma.Allocate(128);
        port.Tx = UsbDma.Allocate(4096);
        port.Ring = (byte*)Memory.HeapAllocator.Alloc(UsbSerialPort.RingSize);
        if (port.Rx.Physical == 0 || port.Tx.Physical == 0 || port.Ring == null)
            return false;
        port.RingHead = 0;
        port.RingTail = 0;

        _ports[_portCount] = port;
        port.Name = "ttyUSB" + _portCount.ToString();

        // Line coding: 115200 8N1, then DTR|RTS.
        SetLineCoding(port);
        SetControlLineState(port, 0x03);

        // Arm the first RX transfer.
        port.RxPending = device.Controller.SubmitDataAsync(
            device.SlotId, port.BulkInDci, port.Rx.Physical, port.RxLength, true);

        port.Ready = true;
        device.DriverKind = UsbDriverKind.CdcAcm;
        device.Status = "CDC-ACM (" + port.Name + ")";

        UsbLog("CDC-ACM bound as " + port.Name + " on " + UsbClasses.DeviceName(device));
        if (_portCount == 0)
            UsbLog("ttyUSB0 device node wiring documented in docs/PHASE9-USB.md");
        _portCount++;
        return true;
    }

    public static void Unbind(UsbDevice device)
    {
        for (int i = 0; i < _portCount; i++)
        {
            var p = _ports[i];
            if (p == null || p.Device != device)
                continue;
            UsbDma rx = p.Rx;
            UsbDma.Free(ref rx);
            p.Ready = false;
            for (int j = i; j < _portCount - 1; j++)
                _ports[j] = _ports[j + 1];
            _portCount--;
            return;
        }
    }

    /// <summary>Polled drain of the adapter's bulk IN endpoint.</summary>
    public static void Poll()
    {
        for (int i = 0; i < _portCount; i++)
        {
            var p = _ports[i];
            if (p == null || !p.Ready)
                continue;

            if (p.RxPending)
            {
                XhciTransferResult r;
                if (p.Device.Controller.TryTakeTransfer(p.Device.SlotId, p.BulkInDci,
                        p.RxLength, out r))
                {
                    p.RxPending = false;
                    if (r.Ok && r.BytesTransferred > 0)
                        PushRing(p, r.BytesTransferred);
                }
            }

            if (!p.RxPending)
                p.RxPending = p.Device.Controller.SubmitDataAsync(
                    p.Device.SlotId, p.BulkInDci, p.Rx.Physical, p.RxLength, true);
        }
    }

    private static void PushRing(UsbSerialPort p, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int next = (p.RingHead + 1) % UsbSerialPort.RingSize;
            if (next == p.RingTail)
                break;   // full: drop
            p.Ring[p.RingHead] = p.Rx.Virtual[i];
            p.RingHead = next;
        }
    }

    /// <summary>Bytes available to read.</summary>
    public static int Available(UsbSerialPort p)
    {
        int n = p.RingHead - p.RingTail;
        if (n < 0)
            n += UsbSerialPort.RingSize;
        return n;
    }

    /// <summary>Read one byte (or -1 when empty).</summary>
    public static int ReadByte(UsbSerialPort p)
    {
        if (p.RingHead == p.RingTail)
            return -1;
        byte b = p.Ring[p.RingTail];
        p.RingTail = (p.RingTail + 1) % UsbSerialPort.RingSize;
        return b;
    }

    /// <summary>Write a buffer through the bulk OUT endpoint (chunked).</summary>
    public static bool Write(UsbSerialPort p, byte* data, int length)
    {
        if (!p.Ready || length <= 0 || p.Tx.Physical == 0)
            return false;
        int remaining = length;
        int off = 0;
        while (remaining > 0)
        {
            int chunk = remaining > 4096 ? 4096 : remaining;
            for (int i = 0; i < chunk; i++)
                p.Tx.Virtual[i] = data[off + i];
            var r = p.Device.Controller.DataTransfer(p.Device.SlotId, p.BulkOutDci,
                p.Tx.Physical, chunk, false);
            if (!r.Ok)
                return false;
            off += chunk;
            remaining -= chunk;
        }
        return true;
    }

    // ========================================================================
    // CDC class requests
    // ========================================================================

    private static void SetLineCoding(UsbSerialPort p)
    {
        byte* d = p.Device.Scratch.Virtual;
        // 115200 bps little-endian, 1 stop bit, no parity, 8 data bits.
        d[0] = 0x00; d[1] = 0xC2; d[2] = 0x01; d[3] = 0x00;
        d[4] = 0;    // stop bits: 1
        d[5] = 0;    // parity: none
        d[6] = 8;    // data bits

        var setup = new UsbSetupPacket(0x21, 0x20 /* SET_LINE_CODING */, 0,
            (ushort)p.ControlInterface, 7);
        p.Device.Controller.ControlTransfer(p.Device.SlotId, setup, p.Device.Scratch.Physical, 7);
    }

    private static void SetControlLineState(UsbSerialPort p, ushort state)
    {
        var setup = new UsbSetupPacket(0x21, 0x22 /* SET_CONTROL_LINE_STATE */, state,
            (ushort)p.ControlInterface, 0);
        p.Device.Controller.ControlTransfer(p.Device.SlotId, setup, 0, 0);
    }
}
