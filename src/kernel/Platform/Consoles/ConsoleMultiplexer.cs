// NeutrinoOS kernel - Console Abstraction Layer: ConsoleMultiplexer
//
// Routes output to every registered console device (so boot logs can go to
// both the serial console and, in a later phase, VGA text mode) and routes
// input from a single designated active-input device.

using System;

namespace ProtonOS.Platform;

/// <summary>
/// Multiplexes console output across registered devices and reads input
/// from the designated active device. Phase 2 registers exactly one device
/// (ttyS0). The VGA text device is deferred to Phase 3 and must NOT be
/// registered here.
/// </summary>
public sealed class ConsoleMultiplexer
{
    private const int MaxDevices = 4;

    // NOTE: fixed fields instead of an array - kernel-compiled code must
    // avoid array type tokens (no LdTokenHelpers in the kernel runtime).
    private IConsoleDevice? _device0;
    private IConsoleDevice? _device1;
    private IConsoleDevice? _device2;
    private IConsoleDevice? _device3;
    private int _deviceCount;

    private IConsoleDevice? DeviceAt(int index)
    {
        switch (index)
        {
            case 0: return _device0;
            case 1: return _device1;
            case 2: return _device2;
            case 3: return _device3;
            default: return null;
        }
    }

    private void SetDeviceAt(int index, IConsoleDevice? device)
    {
        switch (index)
        {
            case 0: _device0 = device; break;
            case 1: _device1 = device; break;
            case 2: _device2 = device; break;
            case 3: _device3 = device; break;
        }
    }

    /// <summary>The device that supplies input (default: first registered).</summary>
    public IConsoleDevice? ActiveInput { get; private set; }

    /// <summary>Number of registered devices.</summary>
    public int DeviceCount => _deviceCount;

    /// <summary>Gets the registered device at the given index.</summary>
    public IConsoleDevice? GetDevice(int index)
        => index >= 0 && index < _deviceCount ? DeviceAt(index) : null;

    /// <summary>
    /// Registers a console device. Output is fanned out to all registered
    /// devices; the first registered device becomes the active input.
    /// </summary>
    public bool Register(IConsoleDevice device)
    {
        if (device == null || _deviceCount >= MaxDevices)
            return false;

        SetDeviceAt(_deviceCount, device);
        _deviceCount++;
        if (ActiveInput == null)
            ActiveInput = device;
        return true;
    }

    /// <summary>Unregisters a console device.</summary>
    public bool Unregister(IConsoleDevice device)
    {
        for (int i = 0; i < _deviceCount; i++)
        {
            if (ReferenceEquals(DeviceAt(i), device))
            {
                for (int j = i; j < _deviceCount - 1; j++)
                    SetDeviceAt(j, DeviceAt(j + 1));
                _deviceCount--;
                SetDeviceAt(_deviceCount, null);

                if (ReferenceEquals(ActiveInput, device))
                    ActiveInput = _deviceCount > 0 ? DeviceAt(0) : null;
                return true;
            }
        }
        return false;
    }

    /// <summary>Sets the device used for input.</summary>
    public void SetActiveInput(IConsoleDevice device)
    {
        ActiveInput = device;
    }

    // ==================== Output fan-out ====================

    /// <summary>Writes a single character to all registered devices.</summary>
    public void Write(char c)
    {
        for (int i = 0; i < _deviceCount; i++)
            DeviceAt(i)!.Write(c);
    }

    /// <summary>Writes a span of characters to all registered devices.</summary>
    public void Write(ReadOnlySpan<char> s)
    {
        for (int i = 0; i < _deviceCount; i++)
            DeviceAt(i)!.Write(s);
    }

    /// <summary>Writes a string to all registered devices.</summary>
    public void Write(string s)
    {
        for (int i = 0; i < _deviceCount; i++)
            DeviceAt(i)!.Write(s.AsSpan());
    }

    /// <summary>Writes a string followed by CRLF to all registered devices.</summary>
    public void WriteLine(string s)
    {
        Write(s);
        Write("\r\n".AsSpan());
    }

    /// <summary>Writes CRLF to all registered devices.</summary>
    public void WriteLine()
    {
        Write("\r\n".AsSpan());
    }

    /// <summary>Flushes all registered devices.</summary>
    public void Flush()
    {
        for (int i = 0; i < _deviceCount; i++)
            DeviceAt(i)!.Flush();
    }

    /// <summary>Clears all registered devices.</summary>
    public void Clear()
    {
        for (int i = 0; i < _deviceCount; i++)
            DeviceAt(i)!.Clear();
    }

    /// <summary>Sets the cursor position on all registered devices.</summary>
    public void SetCursorPosition(int left, int top)
    {
        for (int i = 0; i < _deviceCount; i++)
            DeviceAt(i)!.SetCursorPosition(left, top);
    }

    /// <summary>Gets the cursor position from the active input device.</summary>
    public void GetCursorPosition(out int left, out int top)
    {
        if (ActiveInput != null)
        {
            ActiveInput.GetCursorPosition(out left, out top);
        }
        else
        {
            left = 0; top = 0;
        }
    }

    /// <summary>Sets colors on all registered devices.</summary>
    public void SetColors(int foreground, int background)
    {
        for (int i = 0; i < _deviceCount; i++)
            DeviceAt(i)!.SetColors(foreground, background);
    }
}
