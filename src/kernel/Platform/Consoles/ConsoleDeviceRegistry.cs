// NeutrinoOS kernel - console device registry
//
// Minimal name -> console device mapping so the serial console can be
// addressed as /dev/ttyS0 (Phase 2). A full VFS-backed /dev tree is
// deferred to a later phase; this registry is intentionally tiny.

namespace ProtonOS.Platform;

/// <summary>
/// Registry of named console devices (e.g. "/dev/ttyS0"). Used by the
/// console layer and the console file-descriptor stub syscall.
/// </summary>
public static class ConsoleDeviceRegistry
{
    private const int MaxDevices = 4;

    // NOTE: fixed fields instead of arrays - kernel-compiled code must
    // avoid array type tokens (no LdTokenHelpers in the kernel runtime).
    private static string? _name0, _name1, _name2, _name3;
    private static IConsoleDevice? _dev0, _dev1, _dev2, _dev3;
    private static int _count;

    private static string? NameAt(int index)
    {
        switch (index)
        {
            case 0: return _name0;
            case 1: return _name1;
            case 2: return _name2;
            case 3: return _name3;
            default: return null;
        }
    }

    private static IConsoleDevice? DeviceAt(int index)
    {
        switch (index)
        {
            case 0: return _dev0;
            case 1: return _dev1;
            case 2: return _dev2;
            case 3: return _dev3;
            default: return null;
        }
    }

    /// <summary>Registers a device under a path-like name ("/dev/ttyS0").</summary>
    public static bool Register(string name, IConsoleDevice device)
    {
        if (_count >= MaxDevices)
            return false;

        switch (_count)
        {
            case 0: _name0 = name; _dev0 = device; break;
            case 1: _name1 = name; _dev1 = device; break;
            case 2: _name2 = name; _dev2 = device; break;
            case 3: _name3 = name; _dev3 = device; break;
        }
        _count++;
        return true;
    }

    /// <summary>Finds a registered device by name; null when absent.</summary>
    public static IConsoleDevice? Find(string name)
    {
        for (int i = 0; i < _count; i++)
        {
            if (NameAt(i) == name)
                return DeviceAt(i);
        }
        return null;
    }

    /// <summary>The registered device at the given index, or null.</summary>
    public static IConsoleDevice? GetAt(int index)
        => index >= 0 && index < _count ? DeviceAt(index) : null;

    /// <summary>The registered name at the given index, or null.</summary>
    public static string? GetNameAt(int index)
        => index >= 0 && index < _count ? NameAt(index) : null;

    /// <summary>Number of registered devices.</summary>
    public static int Count => _count;
}
