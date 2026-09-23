// ProtonOS DDK - Kernel entropy source wrapper
// DllImport wrapper for the kernel entropy export (RTC / TSC / device
// timing jitter, stirred by the kernel).

using System;
using System.Runtime.InteropServices;

namespace ProtonOS.DDK.Kernel;

/// <summary>DDK wrapper for the kernel entropy API.</summary>
public static class Entropy
{
    /// <summary>
    /// Fills the buffer with kernel entropy; returns the number of
    /// bytes actually written.
    /// </summary>
    [DllImport("*", EntryPoint = "Kernel_GetEntropy")]
    public static extern unsafe int GetEntropy(byte* buffer, int length);
}
