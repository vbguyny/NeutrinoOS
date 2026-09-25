// ProtonOS Kernel - driver manager.
//
// Registers driver instances, matches them against the device tree
// (Match -> Probe -> Start lifecycle from the NeutrinoOS.Drivers ABI) and
// gates registration on ABI compatibility. Drivers are registered either as
// kernel built-ins (ported drivers) or by driver hosts.

using System;
using NeutrinoOS.Drivers;
using ProtonOS.Platform;

namespace ProtonOS.Drivers;

/// <summary>A driver registered with the manager.</summary>
public sealed class DriverRegistration
{
    public IDriver Driver;
    public DeviceInfo BoundDevice;
    public bool Started;
}

/// <summary>Matches drivers to devices and tracks bindings.</summary>
public static class DriverManager
{
    private const int MaxDrivers = 32;

    private static readonly DriverRegistration[] _drivers = new DriverRegistration[MaxDrivers];
    private static int _driverCount;
    private static KernelDeviceTree _tree;
    private static bool _initialized;

    /// <summary>Number of registered drivers.</summary>
    public static int DriverCount => _driverCount;

    /// <summary>Initialize the manager with the device tree.</summary>
    public static void Initialize(KernelDeviceTree tree)
    {
        _tree = tree;
        _driverCount = 0;
        _initialized = true;
    }

    /// <summary>Get a registration by index (for status reporting).</summary>
    public static DriverRegistration GetRegistration(int index)
    {
        if (index < 0 || index >= _driverCount)
            return null;
        return _drivers[index];
    }

    /// <summary>
    /// Register a driver. Rejects ABI-incompatible drivers (major mismatch)
    /// and drivers built against a newer minor version.
    /// </summary>
    public static bool Register(IDriver driver)
    {
        if (!_initialized || driver == null)
            return false;
        if (_driverCount >= MaxDrivers)
            return false;

        if (!DriverAbi.IsCompatible(driver.AbiMajor, driver.AbiMinor))
        {
            DebugConsole.Write("[drv] refusing '");
            DebugConsole.Write(driver.Name);
            DebugConsole.Write("': ABI ");
            DebugConsole.WriteDecimal((uint)driver.AbiMajor);
            DebugConsole.Write(".");
            DebugConsole.WriteDecimal((uint)driver.AbiMinor);
            DebugConsole.Write(" incompatible with kernel ABI ");
            DebugConsole.WriteLine(DriverAbi.VersionString);
            return false;
        }

        DriverRegistration reg = new DriverRegistration();
        reg.Driver = driver;
        reg.BoundDevice = null;
        reg.Started = false;
        _drivers[_driverCount] = reg;
        _driverCount++;

        // The manager acts as the driver's host: inject the kernel
        // services implementation before any lifecycle call.
        driver.Initialize(KernelDriverServices.Instance);
        return true;
    }

    /// <summary>
    /// Match every unbound device against registered drivers using the
    /// Match -> Probe -> Start lifecycle. Returns the number of devices
    /// successfully started.
    /// </summary>
    public static int MatchAll()
    {
        if (!_initialized || _tree == null)
            return 0;

        int started = 0;
        for (int i = 1; i < _tree.Count; i++)   // skip the root
        {
            DeviceInfo device = _tree.GetAt(i);
            if (device.Status == DeviceStatus.Started)
                continue;

            for (int d = 0; d < _driverCount; d++)
            {
                DriverRegistration reg = _drivers[d];
                if (reg.Started)
                    continue;   // single bind per driver instance for now

                if (!reg.Driver.Match(device))
                    continue;

                device.Status = DeviceStatus.Matched;
                if (!reg.Driver.Probe(device))
                {
                    device.Status = DeviceStatus.Discovered;
                    continue;
                }

                if (reg.Driver.Start(device))
                {
                    reg.BoundDevice = device;
                    reg.Started = true;
                    device.Status = DeviceStatus.Started;
                    DebugConsole.Write("[drv] bound '");
                    DebugConsole.Write(reg.Driver.Name);
                    DebugConsole.Write("' to ");
                    DebugConsole.WriteLine(device.Path);
                    started++;
                }
                else
                {
                    device.Status = DeviceStatus.Failed;
                }
                break;   // first matching driver wins (or fails) for this device
            }
        }
        return started;
    }

    /// <summary>Stop every started driver (shutdown path).</summary>
    public static void StopAll()
    {
        for (int d = 0; d < _driverCount; d++)
        {
            DriverRegistration reg = _drivers[d];
            if (reg.Started && reg.BoundDevice != null)
            {
                reg.Driver.Stop(reg.BoundDevice);
                reg.Started = false;
            }
        }
    }
}
