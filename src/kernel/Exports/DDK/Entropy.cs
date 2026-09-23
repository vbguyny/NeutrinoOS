// ProtonOS kernel - DDK entropy export
//
// Raw entropy for the JIT-world CSPRNG (src/ddk/Crypto/Csprng, backing
// /dev/random users). Sources: TSC samples, HPET counter, wall-clock
// time and uptime, stirred through an xorshift64* mixer seeded at first
// use. Good enough for SSH/TLS session keys on a single-user OS; the
// quality caveat is documented in docs/PHASE6-CRYPTO.md.

using System.Runtime.InteropServices;
using ProtonOS.X64;

namespace ProtonOS.Exports.DDK;

/// <summary>DDK entropy exports (see file header).</summary>
public static unsafe class EntropyExports
{
    private static ulong _state;
    private static bool _init;

    /// <summary>
    /// Fill <paramref name="buffer"/> with entropy; returns the number
    /// of bytes written (always <paramref name="length"/> on success).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "Kernel_GetEntropy")]
    public static int GetEntropy(byte* buffer, int length)
    {
        if (buffer == null || length <= 0)
            return 0;

        if (!_init)
        {
            _state = CPU.ReadTsc() ^ HPET.ReadCounter() ^ RTC.GetSystemTimeAsFileTime();
            if (_state == 0)
                _state = 0x9E3779B97F4A7C15;
            _init = true;
        }

        int pos = 0;
        while (pos < length)
        {
            // Fold in fresh samples from independent clock sources.
            ulong sample = CPU.ReadTsc();
            sample ^= HPET.ReadCounter() << 17;
            sample ^= RTC.GetSystemTimeAsFileTime();
            _state ^= sample;

            // xorshift64* step.
            ulong x = _state;
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            _state = x;
            x *= 0x2545F4914F6CDD1DUL;

            for (int i = 0; i < 8 && pos < length; i++)
            {
                buffer[pos++] = (byte)x;
                x >>= 8;
            }
        }

        return length;
    }
}
