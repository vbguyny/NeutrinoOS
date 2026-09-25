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

        // TEMP diagnostics: what does the adapter hand to the driver?
        DebugConsole.Write("[drv] adapter.Match dev=0x");
        DebugConsole.WriteHex((ulong)RefOf(device));
        DebugConsole.Write(" did=0x");
        DebugConsole.WriteHex(device.DeviceId);
        DebugConsole.Write(" bus=");
        DebugConsole.Write(device.Bus);
        DebugConsole.WriteLine();

        byte result = ((delegate* unmanaged<nint, nint, byte>)_match)(_instance, RefOf(device));

        DebugConsole.Write("[drv] adapter.Match -> ");
        DebugConsole.WriteDecimal((uint)result);
        DebugConsole.WriteLine();
        return result != 0;
    }

    public bool Probe(DeviceInfo device)
    {
        if (_probe == null)
            return false;
        return ((delegate* unmanaged<nint, nint, byte>)_probe)(_instance, RefOf(device)) != 0;
    }

    public bool Start(DeviceInfo device)
    {
        if (_start == null)
            return false;
        return ((delegate* unmanaged<nint, nint, byte>)_start)(_instance, RefOf(device)) != 0;
    }

    public void Stop(DeviceInfo device)
    {
        if (_stop == null)
            return;
        ((delegate* unmanaged<nint, nint, void>)_stop)(_instance, RefOf(device));
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
