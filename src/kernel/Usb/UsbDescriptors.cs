// NeutrinoOS kernel - USB descriptors and protocol structures (Phase 9 Task 1).
//
// USB 2.0/3.x chapter 9 definitions shared by the xHCI controller driver,
// the device enumeration code and the class drivers. All structures are
// packed with the exact wire layout (little-endian).

using System.Runtime.InteropServices;

namespace ProtonOS.Usb;

/// <summary>USB standard descriptor types (bDescriptorType).</summary>
public static class UsbDescType
{
    public const byte Device = 1;
    public const byte Configuration = 2;
    public const byte String = 3;
    public const byte Interface = 4;
    public const byte Endpoint = 5;
    public const byte DeviceQualifier = 6;
    public const byte OtherSpeedConfiguration = 7;
    public const byte InterfacePower = 8;
    public const byte Bos = 15;
}

/// <summary>USB class codes (bDeviceClass / bInterfaceClass).</summary>
public static class UsbClassCode
{
    public const byte PerInterface = 0x00;
    public const byte Cdc = 0x02;
    public const byte Hid = 0x03;
    public const byte MassStorage = 0x08;
    public const byte Hub = 0x09;
    public const byte CdcData = 0x0A;
}

/// <summary>bRequest standard values.</summary>
public static class UsbRequestCode
{
    public const byte GetStatus = 0;
    public const byte ClearFeature = 1;
    public const byte SetFeature = 3;
    public const byte SetAddress = 5;
    public const byte GetDescriptor = 6;
    public const byte SetConfiguration = 9;
    public const byte SetInterface = 11;
}

/// <summary>bmRequestType bit fields.</summary>
public static class UsbRequestType
{
    public const byte DirDeviceToHost = 0x80;
    public const byte DirHostToDevice = 0x00;
    public const byte TypeStandard = 0x00;
    public const byte TypeClass = 0x20;
    public const byte TypeVendor = 0x40;
    public const byte RecipientDevice = 0x00;
    public const byte RecipientInterface = 0x01;
    public const byte RecipientEndpoint = 0x02;
}

/// <summary>USB 8-byte control transfer setup packet.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct UsbSetupPacket
{
    public byte RequestType;
    public byte Request;
    public ushort Value;
    public ushort Index;
    public ushort Length;

    public UsbSetupPacket(byte requestType, byte request, ushort value, ushort index, ushort length)
    {
        RequestType = requestType;
        Request = request;
        Value = value;
        Index = index;
        Length = length;
    }
}

/// <summary>Standard device descriptor (18 bytes; full 3.0 form has 18).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct UsbDeviceDescriptor
{
    public byte Length;
    public byte DescriptorType;
    public ushort BcdUsb;
    public byte DeviceClass;
    public byte DeviceSubClass;
    public byte DeviceProtocol;
    public byte MaxPacketSize0;
    public ushort IdVendor;
    public ushort IdProduct;
    public ushort BcdDevice;
    public byte ManufacturerIndex;
    public byte ProductIndex;
    public byte SerialIndex;
    public byte NumConfigurations;
}

/// <summary>Configuration descriptor header (9 bytes; total follows).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct UsbConfigDescriptor
{
    public byte Length;
    public byte DescriptorType;
    public ushort TotalLength;
    public byte NumInterfaces;
    public byte ConfigurationValue;
    public byte ConfigurationIndex;
    public byte Attributes;
    public byte MaxPower;

    /// <summary>USB 3.x bmAttributes: this is a USB 3.0 device.</summary>
    public bool IsUsb3 => (Attributes & 0x80) != 0;
}

/// <summary>Interface descriptor (9 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct UsbInterfaceDescriptor
{
    public byte Length;
    public byte DescriptorType;
    public byte InterfaceNumber;
    public byte AlternateSetting;
    public byte NumEndpoints;
    public byte InterfaceClass;
    public byte InterfaceSubClass;
    public byte InterfaceProtocol;
    public byte InterfaceIndex;
}

/// <summary>Endpoint descriptor (7 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct UsbEndpointDescriptor
{
    public byte Length;
    public byte DescriptorType;
    public byte EndpointAddress;
    public byte Attributes;           // bits 1:0 transfer type
    public ushort MaxPacketSize;      // bits 10:0 size, 12:11 transactions/burst
    public byte Interval;

    public byte Number => (byte)(EndpointAddress & 0x0F);
    public bool IsIn => (EndpointAddress & 0x80) != 0;
    public byte TransferType => (byte)(Attributes & 0x03);
    public bool IsControl => TransferType == 0;
    public bool IsIsoch => TransferType == 1;
    public bool IsBulk => TransferType == 2;
    public bool IsInterrupt => TransferType == 3;
    public ushort PacketSize => (ushort)(MaxPacketSize & 0x07FF);
}

/// <summary>
/// A parsed configuration: the configuration descriptor plus its
/// interface/endpoint tree, copied into kernel memory. Endpoints are
/// kept in a flat array (bflat kernel mode cannot codegen 2D arrays).
/// </summary>
public unsafe sealed class UsbConfiguration
{
    public const int MaxInterfaces = 8;
    public const int MaxEndpointsPerInterface = 8;

    public UsbConfigDescriptor Descriptor;
    public int InterfaceCount;
    public UsbInterfaceDescriptor[] Interfaces = new UsbInterfaceDescriptor[MaxInterfaces];
    public int[] EndpointCounts = new int[MaxInterfaces];
    public UsbEndpointDescriptor[] Endpoints = new UsbEndpointDescriptor[MaxInterfaces * MaxEndpointsPerInterface];

    public byte ConfigurationValue => Descriptor.ConfigurationValue;
    public ushort TotalLength => Descriptor.TotalLength;

    public UsbEndpointDescriptor GetEndpoint(int interfaceIndex, int endpointIndex)
    {
        return Endpoints[interfaceIndex * MaxEndpointsPerInterface + endpointIndex];
    }

    public void SetEndpoint(int interfaceIndex, int endpointIndex, UsbEndpointDescriptor ep)
    {
        Endpoints[interfaceIndex * MaxEndpointsPerInterface + endpointIndex] = ep;
    }
}

/// <summary>One bus device address (1-127) — allocated by the controller.</summary>
public static class UsbAddress
{
    public const byte MaxAddress = 127;
}
