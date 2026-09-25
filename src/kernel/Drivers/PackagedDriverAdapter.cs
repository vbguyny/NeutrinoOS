// ProtonOS Kernel - packaged driver adapter (Phase 8, driver framework).
//
// Binds a JIT-loaded driver instance to the driver manager without AOT
// code ever calling an interface method ON the JIT object: AOT->JIT
// interface dispatch on JIT-built MethodTables is not reliable in this
// runtime (the kernel's IDriver MethodTable is not found in the JIT MT's
// interface map; an interface call lands on the wrong slot - observed
// returning another driver's Name).
//
// Every call goes through a per-method thunk instead: the driver's own
// methods are JIT-compiled and called as unmanaged function pointers with
// the instance pointer as the first argument - the same direct-call
// pattern the kernel uses for AhciEntry helpers. The reverse direction
// (driver -> kernel, e.g. IDriverServices.Log) stays normal dispatch: the
// driver holds a reference to the kernel's services object, and JIT->AOT
// interface calls on kernel MethodTables are the proven working direction.

using System;
using NeutrinoOS.Drivers;
using ProtonOS.Platform;

namespace ProtonOS.Drivers;

/// <summary>Kernel-side IDriver that forwards to a JIT'd driver instance.</summary>
public sealed unsafe class PackagedDriverAdapter : IDriver
{
    private nint _instance;

    // Thunks (void* + cast at call time - the kernel's proven shape).
    private void* _getName;
    private void* _getVersion;
    private void* _getAbiMajor;
    private void* _getAbiMinor;
    private void* _initialize;
    private void* _match;
    private void* _probe;
    private void* _start;
    private void* _stop;

    // Device-marshal thunks (ABI DeviceInfoMarshal, compiled from the /lib
    // copy): build a driver-world DeviceInfo copy per call so every field
    // read inside the driver uses its own layout (see DeviceMarshal.cs).
    private void* _marshalCreate;
    private void* _marshalMatch;

    private string _name;
    private string _version;

    public string Name => _name;

    public string Version => _version;

    public int AbiMajor
    {
        get
        {
            if (_getAbiMajor == null)
                return 0;
            return ((delegate* unmanaged<nint, int>)_getAbiMajor)(_instance);
        }
    }

    public int AbiMinor
    {
        get
        {
            if (_getAbiMinor == null)
                return 0;
            return ((delegate* unmanaged<nint, int>)_getAbiMinor)(_instance);
        }
    }

    /// <summary>Wire the instance and thunks; caches Name/Version.</summary>
    internal void Bind(nint instance, void* getName, void* getVersion,
                       void* getAbiMajor, void* getAbiMinor, void* initialize,
                       void* match, void* probe, void* start, void* stop)
    {
        _instance = instance;
        _getName = getName;
        _getVersion = getVersion;
        _getAbiMajor = getAbiMajor;
        _getAbiMinor = getAbiMinor;
        _initialize = initialize;
        _match = match;
        _probe = probe;
        _start = start;
        _stop = stop;

        _name = RawToString(((delegate* unmanaged<nint, nint>)getName)(instance));
        _version = RawToString(((delegate* unmanaged<nint, nint>)getVersion)(instance));
    }

    /// <summary>Wire the device-marshal factories (from the ABI /lib copy).</summary>
    internal void BindMarshal(void* create, void* withMatch)
    {
        _marshalCreate = create;
        _marshalMatch = withMatch;
    }

    /// <summary>
    /// Build a driver-world DeviceInfo copy for one call: base fields via
    /// DeviceInfoMarshal.Create, match fields via DeviceInfoMarshal.WithMatch
    /// (both JIT-compiled in the ABI copy). Returns 0 when unavailable.
    /// </summary>
    private nint MakeJitDevice(DeviceInfo d)
    {
        if (_marshalCreate == null || _marshalMatch == null)
            return 0;

        nint pathPtr = 0;
        if (d.Path != null)
        {
            object pathObj = d.Path;
            pathPtr = System.Runtime.CompilerServices.Unsafe.As<object, nint>(ref pathObj);
        }
        nint busPtr = 0;
        if (d.Bus != null)
        {
            object busObj = d.Bus;
            busPtr = System.Runtime.CompilerServices.Unsafe.As<object, nint>(ref busObj);
        }

        nint shell = ((delegate* unmanaged<int, int, nint, nint, nint>)_marshalCreate)(
            d.Id, d.ParentId, pathPtr, busPtr);
        if (shell == 0)
            return 0;

        ulong vendorDeviceClass = ((ulong)d.VendorId << 48)
            | ((ulong)d.DeviceId << 32)
            | ((ulong)(int)d.Class << 24);
        ulong addressClassCode = ((ulong)d.ClassCode << 32) | d.Address;

        return ((delegate* unmanaged<nint, ulong, ulong, nint>)_marshalMatch)(
            shell, vendorDeviceClass, addressClassCode);
    }

    public void Initialize(IDriverServices services)
    {
        if (_initialize == null)
            return;
        object svcObj = services;
        nint svc = System.Runtime.CompilerServices.Unsafe.As<object, nint>(ref svcObj);
        ((delegate* unmanaged<nint, nint, void>)_initialize)(_instance, svc);
    }

    public bool Match(DeviceInfo device)
    {
        if (_match == null)
            return false;

        nint jitDevice = MakeJitDevice(device);
        if (jitDevice == 0)
        {
            DebugConsole.Write("[drv] adapter.Match: device marshal unavailable for ");
            DebugConsole.Write(device.Path);
            DebugConsole.WriteLine();
            return false;
        }

        byte result = ((delegate* unmanaged<nint, nint, byte>)_match)(_instance, jitDevice);

        DebugConsole.Write("[drv] adapter.Match ");
        DebugConsole.Write(device.Path);
        DebugConsole.Write(" -> ");
        DebugConsole.WriteDecimal((uint)result);
        DebugConsole.WriteLine();
        return result != 0;
    }

    public bool Probe(DeviceInfo device)
    {
        if (_probe == null)
            return false;
        nint jitDevice = MakeJitDevice(device);
        if (jitDevice == 0)
            return false;
        return ((delegate* unmanaged<nint, nint, byte>)_probe)(_instance, jitDevice) != 0;
    }

    public bool Start(DeviceInfo device)
    {
        if (_start == null)
            return false;
        nint jitDevice = MakeJitDevice(device);
        if (jitDevice == 0)
            return false;
        return ((delegate* unmanaged<nint, nint, byte>)_start)(_instance, jitDevice) != 0;
    }

    public void Stop(DeviceInfo device)
    {
        if (_stop == null)
            return;
        nint jitDevice = MakeJitDevice(device);
        if (jitDevice == 0)
            return;
        ((delegate* unmanaged<nint, nint, void>)_stop)(_instance, jitDevice);
    }

    private static nint RefOf(object obj)
    {
        return System.Runtime.CompilerServices.Unsafe.As<object, nint>(ref obj);
    }

    private static string RawToString(nint raw)
    {
        if (raw == 0)
            return "?";
        return System.Runtime.CompilerServices.Unsafe.As<nint, string>(ref raw);
    }
}
