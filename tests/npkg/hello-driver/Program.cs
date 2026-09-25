// NeutrinoOS Phase 8 npkg test fixture: hello-driver payload.
// A real NeutrinoOS.Drivers.IDriver: the kernel driver-package loader finds
// the static Create() factory in this assembly, JIT-compiles and calls it,
// validates the IDriver ABI and matches the instance against the device tree
// (it binds to the virtual test device platform/vtest0, vid 0xFFFF did 0x1601).
// Tier-0-JIT-safe: simple strings, no generics, no LINQ.

using System;
using NeutrinoOS.Drivers;

public sealed class HelloLedDriver : IDriver
{
    private IDriverServices _services;

    public string Name => "hello-driver";

    public string Version => "1.0.0";

    public int AbiMajor => DriverAbi.Major;

    public int AbiMinor => DriverAbi.Minor;

    /// <summary>Kernel driver-package loader convention: static factory.</summary>
    public static IDriver Create()
    {
        return new HelloLedDriver();
    }

    public void Initialize(IDriverServices services)
    {
        _services = services;
    }

    public bool Match(DeviceInfo device)
    {
        // Bisect: match on the numeric id only (string comparison suspended
        // while diagnosing an OpEquality fault in the packaged-driver path).
        return device.DeviceId == 0x1601;
    }

    public bool Probe(DeviceInfo device)
    {
        return true;
    }

    public bool Start(DeviceInfo device)
    {
        if (_services == null)
            return false;
        _services.Log(DriverLogLevel.Info, "hello-driver started on " + device.Path);
        return true;
    }

    public void Stop(DeviceInfo device)
    {
        _ = device;
    }
}

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("hello-driver ok");
        return 0;
    }
}
