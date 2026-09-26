// NeutrinoOS kernel - USB HID class driver (Phase 9 Task 1).
//
// Boot-protocol keyboard and mouse support. The boot protocol report is
// polled from the interrupt IN endpoint (one outstanding async transfer;
// completion checked from UsbStack.Poll). Synthesized keys are fed into
// the CAL line discipline as ANSI bytes, exactly like the PS/2 keyboard
// (keyboard input becomes the active CAL input source).

using System;
using ProtonOS.Platform;
using ProtonOS.Usb.Xhci;

namespace ProtonOS.Usb;

/// <summary>Class-driver dispatch for enumerated devices.</summary>
public static unsafe class UsbClasses
{
    private static void UsbLog(string m) => UsbTrace.Log(m);

    public static void Bind(UsbDevice device)
    {
        for (int i = 0; i < device.Config.InterfaceCount; i++)
        {
            byte cls = device.Config.Interfaces[i].InterfaceClass;
            byte sub = device.Config.Interfaces[i].InterfaceSubClass;
            byte proto = device.Config.Interfaces[i].InterfaceProtocol;

            if (cls == UsbClassCode.Hid)
            {
                if (UsbHid.Bind(device, i, sub, proto))
                    return;
            }
            else if (cls == UsbClassCode.MassStorage)
            {
                if (UsbStorage.Bind(device, i, sub, proto))
                    return;
            }
            else if (cls == UsbClassCode.Cdc)
            {
                if (UsbSerial.Bind(device, i, sub, proto))
                    return;
            }
            else if (cls == UsbClassCode.Hub)
            {
                device.DriverKind = UsbDriverKind.Hub;
                device.Status = "hub (downstream ports not enabled yet)";
                UsbLog("hub device bound (downstream enumeration documented as follow-up)");
                return;
            }
        }

        device.Status = "no class driver";
        UsbLog("device " + DeviceName(device) + ": no class driver");
    }

    public static void Unbind(UsbDevice device)
    {
        switch (device.DriverKind)
        {
            case UsbDriverKind.HidKeyboard:
            case UsbDriverKind.HidMouse:
                UsbHid.Unbind(device);
                break;
            case UsbDriverKind.MassStorage:
                UsbStorage.Unbind(device);
                break;
            case UsbDriverKind.CdcAcm:
                UsbSerial.Unbind(device);
                break;
        }
        device.DriverKind = UsbDriverKind.None;
    }

    public static string DeviceName(UsbDevice device)
    {
        return "usb" + (device.Port + 1).ToString();
    }
}

/// <summary>USB HID class driver (boot protocol).</summary>
public static unsafe class UsbHid
{
    private static void UsbLog(string m) => UsbTrace.Log(m);

    private const int MaxHid = 4;

    private sealed class HidEntry
    {
        public UsbDevice Device = null!;
        public byte Dci;
        public UsbDma Report;
        public int ReportLength;
        public bool IsKeyboard;
        public bool Pending;
        public byte* PrevKeys = null!;
        public bool PrevValid;
    }

    private static HidEntry?[] _entries = new HidEntry?[MaxHid];
    private static int _entryCount;

    /// <summary>True when a boot keyboard is bound (acceptance probe).</summary>
    public static bool KeyboardPresent { get; private set; }

    /// <summary>Total keys delivered to the console (diagnostics).</summary>
    public static uint KeysDelivered { get; private set; }

    public static bool Bind(UsbDevice device, int interfaceIndex, byte subclass, byte protocol)
    {
        bool keyboard = protocol == 1;
        bool mouse = protocol == 2;
        if (!keyboard && !mouse)
        {
            // Not boot protocol; accept anyway when an interrupt IN ep exists.
            keyboard = true;
        }
        _ = subclass;

        UsbEndpointDescriptor ep;
        byte dci;
        if (!device.TryGetEndpoint(interfaceIndex, 3 /* interrupt */, true, out ep, out dci))
        {
            UsbLog("HID device has no interrupt IN endpoint");
            return false;
        }

        if (_entryCount >= MaxHid)
            return false;

        var entry = new HidEntry();
        entry.Device = device;
        entry.Dci = dci;
        entry.IsKeyboard = keyboard;
        entry.ReportLength = keyboard ? 8 : 4;
        entry.Report = UsbDma.Allocate(64);
        entry.PrevKeys = (byte*)Memory.HeapAllocator.Alloc(8);
        entry.Pending = false;
        entry.PrevValid = false;
        for (int i = 0; i < 6; i++)
            entry.PrevKeys[i] = 0;

        if (entry.Report.Physical == 0)
            return false;

        // Best-effort: place the device in boot protocol, idle 0.
        SetProtocol(device, interfaceIndex, 0);
        SetIdle(device, interfaceIndex, 0);

        // Kick the first polled interrupt transfer.
        entry.Pending = device.Controller.SubmitDataAsync(
            device.SlotId, dci, entry.Report.Physical, entry.ReportLength, true);

        _entries[_entryCount++] = entry;
        device.DriverKind = keyboard ? UsbDriverKind.HidKeyboard : UsbDriverKind.HidMouse;
        device.Status = keyboard ? "HID boot keyboard" : "HID boot mouse";
        if (keyboard)
            KeyboardPresent = true;

        UsbLog("HID " + (keyboard ? "keyboard" : "mouse") + " bound on "
            + UsbClasses.DeviceName(device) + " ep=0x" + ep.EndpointAddress.ToString("X2", null));
        return true;
    }

    public static void Unbind(UsbDevice device)
    {
        for (int i = 0; i < _entryCount; i++)
        {
            var e = _entries[i];
            if (e == null || e.Device != device)
                continue;
            if (e.IsKeyboard)
                KeyboardPresent = false;
            UsbDma report = e.Report;
            UsbDma.Free(ref report);
            for (int j = i; j < _entryCount - 1; j++)
                _entries[j] = _entries[j + 1];
            _entryCount--;
            return;
        }
    }

    /// <summary>Polled drain: check completed interrupt transfers, translate.</summary>
    public static void Poll()
    {
        for (int i = 0; i < _entryCount; i++)
        {
            var e = _entries[i];
            if (e == null || !e.Pending)
                continue;

            XhciTransferResult r;
            if (!e.Device.Controller.TryTakeTransfer(e.Device.SlotId, e.Dci, e.ReportLength, out r))
                continue;

            e.Pending = false;

            if (r.Ok && r.BytesTransferred > 0)
            {
                if (e.IsKeyboard)
                    ProcessKeyboardReport(e);
                else
                    ProcessMouseReport(e);
            }

            // Re-arm the interrupt endpoint.
            if (e.Pending == false)
                e.Pending = e.Device.Controller.SubmitDataAsync(
                    e.Device.SlotId, e.Dci, e.Report.Physical, e.ReportLength, true);
        }
    }

    private static void ProcessKeyboardReport(HidEntry e)
    {
        byte* r = e.Report.Virtual;
        byte mods = r[0];
        bool shift = (mods & 0x22) != 0;

        for (int i = 2; i < 8; i++)
        {
            byte usage = r[i];
            if (usage == 0)
                continue;
            if (!e.PrevValid || !ContainsKey(e.PrevKeys, usage))
                EmitKey(usage, shift);
        }

        for (int i = 0; i < 6; i++)
            e.PrevKeys[i] = r[2 + i];
        e.PrevValid = true;
    }

    private static bool ContainsKey(byte* keys, byte usage)
    {
        for (int i = 0; i < 6; i++)
            if (keys[i] == usage)
                return true;
        return false;
    }

    private static void ProcessMouseReport(HidEntry e)
    {
        byte* r = e.Report.Virtual;
        byte buttons = r[0];
        sbyte dx = (sbyte)r[1];
        sbyte dy = (sbyte)r[2];
        _ = buttons;
        _ = dx;
        _ = dy;
        // Movement/buttons are surfaced to the console later (mouse cursor
        // support); the boot keyboard path is the Phase 9 acceptance target.
    }

    private static void EmitKey(byte usage, bool shift)
    {
        byte b = MapUsage(usage, shift);
        if (b == 0)
            return;
        ConsoleAbstractionLayer.FeedKeyboardByte(b);
        KeysDelivered++;
    }

    /// <summary>HID keyboard usage id -> ANSI byte (boot protocol, US layout).</summary>
    private static byte MapUsage(byte usage, bool shift)
    {
        // Letters a-z.
        if (usage >= 0x04 && usage <= 0x1D)
        {
            byte c = (byte)('a' + (usage - 0x04));
            if (shift)
                c = (byte)(c - 32);
            return c;
        }

        switch (usage)
        {
            case 0x1E: return shift ? (byte)'!' : (byte)'1';
            case 0x1F: return shift ? (byte)'@' : (byte)'2';
            case 0x20: return shift ? (byte)'#' : (byte)'3';
            case 0x21: return shift ? (byte)'$' : (byte)'4';
            case 0x22: return shift ? (byte)'%' : (byte)'5';
            case 0x23: return shift ? (byte)'^' : (byte)'6';
            case 0x24: return shift ? (byte)'&' : (byte)'7';
            case 0x25: return shift ? (byte)'*' : (byte)'8';
            case 0x26: return shift ? (byte)'(' : (byte)'9';
            case 0x27: return shift ? (byte)')' : (byte)'0';
            case 0x28: return 0x0D;             // Enter (CR, serial convention)
            case 0x29: return 0x1B;             // Escape
            case 0x2A: return 0x7F;             // Backspace (DEL, serial convention)
            case 0x2B: return 0x09;             // Tab
            case 0x2C: return (byte)' ';
            case 0x2D: return shift ? (byte)'_' : (byte)'-';
            case 0x2E: return shift ? (byte)'+' : (byte)'=';
            case 0x2F: return shift ? (byte)'{' : (byte)'[';
            case 0x30: return shift ? (byte)'}' : (byte)']';
            case 0x31: return shift ? (byte)'|' : (byte)'\\';
            case 0x32: return shift ? (byte)'"' : (byte)'\'';
            case 0x33: return shift ? (byte)'~' : (byte)'`';
            case 0x34: return shift ? (byte)'<' : (byte)',';
            case 0x35: return shift ? (byte)'>' : (byte)'.';
            case 0x36: return shift ? (byte)'?' : (byte)'/';
            // Arrow keys: ANSI ESC sequences (same as the PS/2 path).
            case 0x4F: return 0;                // (arrow encoding handled below)
            default:
                return MapArrow(usage, shift, 0);
        }
    }

    private static byte MapArrow(byte usage, bool shift, byte unused)
    {
        _ = shift;
        _ = unused;
        // Arrows map to multi-byte sequences; feed them directly here.
        {
            byte mid = 0;
            byte fin = 0;
            switch (usage)
            {
                case 0x52: mid = (byte)'A'; fin = 0; break;   // up
                case 0x51: mid = (byte)'B'; fin = 0; break;   // down
                case 0x50: mid = (byte)'D'; fin = 0; break;   // left
                case 0x4F: mid = (byte)'C'; fin = 0; break;   // right
                default: return 0;
            }
            ConsoleAbstractionLayer.FeedKeyboardByte(0x1B);
            ConsoleAbstractionLayer.FeedKeyboardByte((byte)'[');
            ConsoleAbstractionLayer.FeedKeyboardByte(mid);
            _ = fin;
            return 0;
        }
    }

    private static void SetProtocol(UsbDevice device, int interfaceIndex, byte protocol)
    {
        var setup = new UsbSetupPacket(0x21, 0x0B /* SET_PROTOCOL */, protocol,
            (ushort)interfaceIndex, 0);
        device.Controller.ControlTransfer(device.SlotId, setup, 0, 0);
    }

    private static void SetIdle(UsbDevice device, int interfaceIndex, byte duration)
    {
        var setup = new UsbSetupPacket(0x21, 0x0A /* SET_IDLE */, duration,
            (ushort)interfaceIndex, 0);
        device.Controller.ControlTransfer(device.SlotId, setup, 0, 0);
    }
}
