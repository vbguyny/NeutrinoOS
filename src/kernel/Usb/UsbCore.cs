// NeutrinoOS kernel - USB core stack (Phase 9 Task 1).
//
// Device enumeration on top of the xHCI controller: port reset, slot
// address assignment, descriptor reads (device/configuration/interface/
// endpoint), SET_CONFIGURATION and class-driver dispatch. The stack is
// polled: UsbStack.Poll() drains controller events and detects root-port
// connect/disconnect changes (called from ShellMain.IdlePump, the same
// place the Phase 8 PCIe hot-plug scan runs).

using System;
using ProtonOS.Platform;
using ProtonOS.Usb.Xhci;

namespace ProtonOS.Usb;

/// <summary>Class-driver kinds bound to a device.</summary>
public static class UsbDriverKind
{
    public const int None = 0;
    public const int HidKeyboard = 1;
    public const int HidMouse = 2;
    public const int MassStorage = 3;
    public const int CdcAcm = 4;
    public const int Hub = 5;
}

/// <summary>An enumerated USB device on the bus.</summary>
public sealed unsafe class UsbDevice
{
    public XhciController Controller;
    public byte SlotId;
    public int Port;                      // 0-based root port
    public UsbSpeed Speed;
    public byte Ep0MaxPacket;
    public UsbDeviceDescriptor Descriptor;
    public UsbConfiguration Config;
    public bool Configured;
    public int DriverKind = UsbDriverKind.None;
    public string Status = "enumerated";

    // Scratch DMA window owned by the device (descriptor reads, class use).
    public UsbDma Scratch;

    public ushort VendorId => Descriptor.IdVendor;
    public ushort ProductId => Descriptor.IdProduct;
    public byte DeviceClass => Descriptor.DeviceClass;

    /// <summary>Find an endpoint by interface index and direction/type.</summary>
    public bool TryGetEndpoint(int interfaceIndex, byte transferType, bool dirIn,
        out UsbEndpointDescriptor ep, out byte dci)
    {
        ep = default;
        dci = 0;
        if (interfaceIndex < 0 || interfaceIndex >= Config.InterfaceCount)
            return false;
        int n = Config.EndpointCounts[interfaceIndex];
        for (int i = 0; i < n; i++)
        {
            var e = Config.GetEndpoint(interfaceIndex, i);
            if ((e.Attributes & 0x03) == transferType && e.IsIn == dirIn)
            {
                ep = e;
                dci = (byte)(e.Number * 2 + (e.IsIn ? 1 : 0));
                return true;
            }
        }
        return false;
    }
}

/// <summary>Shared USB log line helper.</summary>
public static class UsbTrace
{
    public static void Log(string message)
    {
        DebugConsole.Write("[usb] ");
        DebugConsole.WriteLine(message);
    }
}

/// <summary>The USB stack: controller registration, enumeration, hot-plug.</summary>
public static unsafe class UsbStack
{
    public const int MaxDevices = 16;

    private static XhciController? _controller;
    private static UsbDevice?[] _devices = new UsbDevice?[MaxDevices];
    private static int _deviceCount;
    private static ulong _lastConnectMask;
    private static bool _initLogged;
    private static int _enumFailures;

    public static bool ControllerPresent => _controller != null;
    public static int DeviceCount => _deviceCount;

    public static XhciController? Controller => _controller;

    public static UsbDevice? GetDevice(int index)
    {
        if (index < 0 || index >= _deviceCount)
            return null;
        return _devices[index];
    }

    /// <summary>Attach a controller (after Init) and enumerate its ports.</summary>
    public static void RegisterController(XhciController controller)
    {
        _controller = controller;

        // Nothing has been enumerated yet: start from "all disconnected"
        // so the first Poll() brings up every currently-connected device.
        _lastConnectMask = 0;

        Log("controller registered (" + controller.MaxPorts.ToString() + " root ports)");
    }

    /// <summary>Polled entry point (IdlePump + transfer waits).</summary>
    public static unsafe void Poll()
    {
        var c = _controller;
        if (c == null)
            return;

        c.PollEvents();

        ulong connected = c.BuildConnectedMask();

        ulong changed = connected ^ _lastConnectMask;
        if (changed != 0)
            Log("poll: connected=0x" + ((uint)connected).ToString("X8", null)
                + " changed=0x" + ((uint)changed).ToString("X8", null));
        if (changed == 0)
            return;

        for (int p = 0; p < c.MaxPorts; p++)
        {
            ulong bit = 1UL << p;
            if ((changed & bit) == 0)
                continue;
            if ((connected & bit) != 0)
                ConnectDevice(p);
            else
                DisconnectDevice(p);
        }
        _lastConnectMask = connected;
    }

    /// <summary>One-time kick: enumerate what is already plugged in.</summary>
    public static void EnumerateAll()
    {
        Poll();
    }

    private static void ConnectDevice(int port)
    {
        var c = _controller!;
        c.AckPortChange(port);

        XhciStatus st = c.ResetPort(port, out UsbSpeed speed);
        if (st != XhciStatus.Ok)
        {
            Log("port " + (port + 1).ToString() + ": reset failed (" + st.ToString() + ")");
            _enumFailures++;
            return;
        }

        var device = new UsbDevice();
        device.Controller = c;
        device.Port = port;
        device.Speed = speed;

        st = c.EnableSlot(out byte slotId);
        if (st != XhciStatus.Ok)
        {
            Log("port " + (port + 1).ToString() + ": enable slot failed (" + st.ToString() + ")");
            _enumFailures++;
            return;
        }
        device.SlotId = slotId;

        byte slotSpeed = XhciController.SpeedToSlotValue(speed);
        ushort mps0 = XhciController.DefaultMaxPacket0(speed);

        st = c.AddressDevice(slotId, (byte)(port + 1), slotSpeed, mps0, false);
        if (st != XhciStatus.Ok)
        {
            Log("port " + (port + 1).ToString() + ": address device failed (" + st.ToString() + ")");
            c.DisableSlot(slotId);
            _enumFailures++;
            return;
        }

        device.Scratch = UsbDma.Allocate(4096);
        if (device.Scratch.Physical == 0)
        {
            c.DisableSlot(slotId);
            _enumFailures++;
            return;
        }
        device.Ep0MaxPacket = (byte)mps0;

        // First 8 bytes of the device descriptor.
        if (!ReadDescriptor8(c, device))
        {
            Log("port " + (port + 1).ToString() + ": descriptor read failed");
            Cleanup(device);
            _enumFailures++;
            return;
        }

        byte realMps = device.Scratch.Virtual[7];
        if (realMps != 0 && realMps != device.Ep0MaxPacket)
        {
            ushort newMps = realMps;
            if (newMps >= 8 && newMps <= 64 && (newMps & (newMps - 1)) == 0)
            {
                c.EvaluateContextMps(slotId, (byte)(port + 1), slotSpeed, newMps);
                device.Ep0MaxPacket = realMps;
            }
        }

        // Full device descriptor.
        if (!ReadDescriptor18(c, device))
        {
            Log("port " + (port + 1).ToString() + ": device descriptor failed");
            Cleanup(device);
            _enumFailures++;
            return;
        }

        // Configuration descriptor (header then full body).
        if (!ReadConfiguration(c, device))
        {
            Log("port " + (port + 1).ToString() + ": configuration descriptor failed");
            Cleanup(device);
            _enumFailures++;
            return;
        }

        Log("port " + (port + 1).ToString()
            + ": " + SpeedName(speed)
            + " device " + Hex4(device.VendorId) + ":" + Hex4(device.ProductId)
            + " cfg=" + device.Config.ConfigurationValue.ToString()
            + " ifaces=" + device.Config.InterfaceCount.ToString());

        // SET_CONFIGURATION + configure endpoints.
        if (!SetConfiguration(c, device))
        {
            Log("port " + (port + 1).ToString() + ": SET_CONFIGURATION failed");
            Cleanup(device);
            _enumFailures++;
            return;
        }

        if (_deviceCount < MaxDevices)
            _devices[_deviceCount++] = device;

        UsbClasses.Bind(device);
        device.Status = "configured";
    }

    private static void DisconnectDevice(int port)
    {
        var c = _controller!;
        c.AckPortChange(port);

        UsbDevice? dev = null;
        int index = -1;
        for (int i = 0; i < _deviceCount; i++)
        {
            if (_devices[i] != null && _devices[i]!.Port == port)
            {
                dev = _devices[i];
                index = i;
                break;
            }
        }

        if (dev == null)
        {
            Log("port " + (port + 1).ToString() + ": disconnected (no device)");
            return;
        }

        UsbClasses.Unbind(dev);
        Cleanup(dev);

        for (int i = index; i < _deviceCount - 1; i++)
            _devices[i] = _devices[i + 1];
        _deviceCount--;

        Log("port " + (port + 1).ToString() + ": device removed");
    }

    private static void Cleanup(UsbDevice device)
    {
        device.Controller.DisableSlot(device.SlotId);
        UsbDma scratch = device.Scratch;
        UsbDma.Free(ref scratch);
        device.Scratch = scratch;
    }

    // ========================================================================
    // Descriptor reads (via the EP0 bounce buffer)
    // ========================================================================

    private static bool ReadDescriptor8(XhciController c, UsbDevice device)
    {
        var setup = new UsbSetupPacket(0x80, UsbRequestCode.GetDescriptor,
            UsbDescType.Device << 8, 0, 8);
        var r = c.ControlTransfer(device.SlotId, setup, device.Scratch.Physical, 8);
        if (!r.Ok || r.BytesTransferred < 8)
        {
            UsbTrace.Log("descriptor8: st=" + ((int)r.Status).ToString()
                + " code=" + r.CompletionCode.ToString()
                + " bytes=" + r.BytesTransferred.ToString());
            return false;
        }
        return true;
    }

    private static bool ReadDescriptor18(XhciController c, UsbDevice device)
    {
        var setup = new UsbSetupPacket(0x80, UsbRequestCode.GetDescriptor,
            UsbDescType.Device << 8, 0, 18);
        var r = c.ControlTransfer(device.SlotId, setup, device.Scratch.Physical, 18);
        if (!r.Ok || r.BytesTransferred < 18)
            return false;
        byte* src = device.Scratch.Virtual;
        fixed (UsbDeviceDescriptor* dst = &device.Descriptor)
        {
            byte* d = (byte*)dst;
            for (int i = 0; i < 18; i++)
                d[i] = src[i];
        }
        return true;
    }

    private static bool ReadConfiguration(XhciController c, UsbDevice device)
    {
        // Read the 9-byte header first to learn the total length.
        var setup = new UsbSetupPacket(0x80, UsbRequestCode.GetDescriptor,
            UsbDescType.Configuration << 8, 0, 9);
        var r = c.ControlTransfer(device.SlotId, setup, device.Scratch.Physical, 9);
        if (!r.Ok || r.BytesTransferred < 9)
            return false;

        byte* src = device.Scratch.Virtual;
        ushort total = (ushort)(src[2] | (src[3] << 8));
        if (total < 9)
            return false;
        if (total > 1024)
            total = 1024;

        // Full configuration blob.
        setup = new UsbSetupPacket(0x80, UsbRequestCode.GetDescriptor,
            UsbDescType.Configuration << 8, 0, total);
        r = c.ControlTransfer(device.SlotId, setup, device.Scratch.Physical, total);
        if (!r.Ok || r.BytesTransferred < 9)
            return false;
        int got = r.BytesTransferred;

        var cfg = new UsbConfiguration();
        byte* p = src;
        // Configuration header.
        cfg.Descriptor.Length = p[0];
        cfg.Descriptor.DescriptorType = p[1];
        cfg.Descriptor.TotalLength = (ushort)(p[2] | (p[3] << 8));
        cfg.Descriptor.NumInterfaces = p[4];
        cfg.Descriptor.ConfigurationValue = p[5];
        cfg.Descriptor.ConfigurationIndex = p[6];
        cfg.Descriptor.Attributes = p[7];
        cfg.Descriptor.MaxPower = p[8];

        int offset = cfg.Descriptor.Length;
        int iface = -1;
        while (offset + 2 <= got)
        {
            byte len = p[offset];
            byte type = p[offset + 1];
            if (len == 0)
                break;

            if (type == UsbDescType.Interface && len >= 9)
            {
                if (iface + 1 < UsbConfiguration.MaxInterfaces)
                {
                    iface++;
                    var id = new UsbInterfaceDescriptor();
                    id.Length = p[offset];
                    id.DescriptorType = p[offset + 1];
                    id.InterfaceNumber = p[offset + 2];
                    id.AlternateSetting = p[offset + 3];
                    id.NumEndpoints = p[offset + 4];
                    id.InterfaceClass = p[offset + 5];
                    id.InterfaceSubClass = p[offset + 6];
                    id.InterfaceProtocol = p[offset + 7];
                    id.InterfaceIndex = p[offset + 8];
                    cfg.Interfaces[iface] = id;
                    cfg.EndpointCounts[iface] = 0;
                    cfg.InterfaceCount = iface + 1;
                }
            }
            else if (type == UsbDescType.Endpoint && len >= 7 && iface >= 0)
            {
                int n = cfg.EndpointCounts[iface];
                if (n < UsbConfiguration.MaxEndpointsPerInterface)
                {
                    var ep = new UsbEndpointDescriptor();
                    ep.Length = p[offset];
                    ep.DescriptorType = p[offset + 1];
                    ep.EndpointAddress = p[offset + 2];
                    ep.Attributes = p[offset + 3];
                    ep.MaxPacketSize = (ushort)(p[offset + 4] | (p[offset + 5] << 8));
                    ep.Interval = p[offset + 6];
                    cfg.SetEndpoint(iface, n, ep);
                    cfg.EndpointCounts[iface] = n + 1;
                }
            }

            offset += len;
        }

        device.Config = cfg;
        return cfg.InterfaceCount > 0;
    }

    private static bool SetConfiguration(XhciController c, UsbDevice device)
    {
        byte* eps = stackalloc byte[32];
        byte* epNums = stackalloc byte[32];
        bool* epIns = stackalloc bool[32];
        ushort* epMps = stackalloc ushort[32];
        byte* epIntervals = stackalloc byte[32];
        int count = 0;

        for (int i = 0; i < device.Config.InterfaceCount && count < 30; i++)
        {
            int n = device.Config.EndpointCounts[i];
            for (int e = 0; e < n && count < 30; e++)
            {
                var ep = device.Config.GetEndpoint(i, e);
                byte type;
                if (ep.IsBulk)
                    type = (byte)(ep.IsIn ? 6 : 2);
                else if (ep.IsInterrupt)
                    type = (byte)(ep.IsIn ? 7 : 3);
                else if (ep.IsControl)
                    continue;
                else
                    continue;   // isochronous: not supported yet
                eps[count] = type;
                epNums[count] = ep.Number;
                epIns[count] = ep.IsIn;
                epMps[count] = (ushort)(ep.PacketSize == 0 ? 64 : ep.PacketSize);
                epIntervals[count] = (byte)(ep.Interval == 0 ? 16 : ep.Interval);
                count++;
            }
        }

        var setup = new UsbSetupPacket(0x00, UsbRequestCode.SetConfiguration,
            device.Config.ConfigurationValue, 0, 0);
        var r = c.ControlTransfer(device.SlotId, setup, 0, 0);
        if (!r.Ok)
            return false;

        byte slotSpeed = XhciController.SpeedToSlotValue(device.Speed);
        var st = c.ConfigureEndpoints(device.SlotId, slotSpeed, (byte)(device.Port + 1),
            device.Config.ConfigurationValue, eps, epNums, epIns, epMps, epIntervals, count);
        if (st != XhciStatus.Ok)
            return false;

        device.Configured = true;
        return true;
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    public static string SpeedName(UsbSpeed speed)
    {
        switch (speed)
        {
            case UsbSpeed.Low: return "low";
            case UsbSpeed.Full: return "full";
            case UsbSpeed.High: return "high";
            case UsbSpeed.Super: return "super";
            case UsbSpeed.SuperPlus: return "super+";
            default: return "?";
        }
    }

    private static string Hex4(ushort v)
    {
        string s = "";
        for (int i = 12; i >= 0; i -= 4)
        {
            int nibble = (v >> i) & 0xF;
            s += (char)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);
        }
        return s;
    }

    private static void Log(string message)
    {
        DebugConsole.Write("[usb] ");
        DebugConsole.WriteLine(message);
        _ = _initLogged;
    }

    /// <summary>Test hook: number of enumeration failures (diagnostics).</summary>
    public static int EnumerationFailures => _enumFailures;
}
