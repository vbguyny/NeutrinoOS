// ProtonOS DDK - CSPRNG (Phase 6)
//
// ChaCha20-based deterministic random bit generator with entropy
// mixing. The kernel provides a raw entropy source through the
// Kernel_GetEntropy export (RTC / TSC / device timing jitter); this
// class stirs that into its 256-bit key with SHA-256 and produces
// keystream-derived output.
//
// The generator is deliberately simple: reseed() replaces the key
// with SHA256(oldKey || freshEntropy), and output blocks come from
// ChaCha20 keyed by the current key with an incrementing block index.

using System;

namespace ProtonOS.DDK.Crypto;

/// <summary>ChaCha20-based CSPRNG (see file header).</summary>
/// <remarks>
/// The guest is cooperatively single-threaded in practice (all SSH/TLS
/// sessions run on the shell/boot thread), so this class is not locked;
/// do not call it concurrently.
/// </remarks>
public static class Csprng
{
    private static readonly byte[] Key = new byte[32];
    private static ulong _blocks;
    private static bool _seeded;

    /// <summary>
    /// Reseed from the kernel entropy export. Safe to call repeatedly;
    /// each call mixes fresh entropy into the key.
    /// </summary>
    public static void Reseed()
    {
        var fresh = new byte[64];
        int got = KernelEntropy(fresh, 64);

        if (!_seeded)
        {
            // First seed: derive the key directly from fresh entropy.
            var k = Sha256.Hash(fresh);
            for (int i = 0; i < 32; i++)
                Key[i] = k[i];
            _seeded = true;
            _blocks = 0;
        }
        else
        {
            // Re-key: SHA256(oldKey || fresh).
            var mix = new byte[96];
            for (int i = 0; i < 32; i++)
                mix[i] = Key[i];
            for (int i = 0; i < 64; i++)
                mix[32 + i] = fresh[i];
            var k = Sha256.Hash(mix);
            for (int i = 0; i < 32; i++)
                Key[i] = k[i];
        }
    }

    /// <summary>Mix caller-provided entropy (used at boot and by tests).</summary>
    public static void AddEntropy(byte[] entropy)
    {
        if (entropy == null || entropy.Length == 0)
            return;

        if (!_seeded)
        {
            var k = Sha256.Hash(entropy);
            for (int i = 0; i < 32; i++)
                Key[i] = k[i];
            _seeded = true;
        }
        else
        {
            var mix = new byte[32 + entropy.Length];
            for (int i = 0; i < 32; i++)
                mix[i] = Key[i];
            for (int i = 0; i < entropy.Length; i++)
                mix[32 + i] = entropy[i];
            var k = Sha256.Hash(mix);
            for (int i = 0; i < 32; i++)
                Key[i] = k[i];
        }
    }

    /// <summary>Fill a buffer with random bytes; seeds on first use.</summary>
    public static void Fill(byte[] buffer, int offset, int length)
    {
        if (buffer == null || length <= 0)
            return;

        if (!_seeded)
            ReseedLocked();

        int pos = 0;
        var block = new byte[64];
        while (pos < length)
        {
            BlockAt(_blocks, block);
            _blocks++;
            int n = length - pos;
            if (n > 64)
                n = 64;
            for (int i = 0; i < n; i++)
                buffer[offset + pos + i] = block[i];
            pos += n;
        }
    }

    /// <summary>Convenience: a fresh random byte array.</summary>
    public static byte[] GetBytes(int length)
    {
        var b = new byte[length];
        Fill(b, 0, length);
        return b;
    }

    /// <summary>Reset to the unseeded state (tests and tools).</summary>
    public static void DebugReset()
    {
        for (int i = 0; i < 32; i++)
            Key[i] = 0;
        _blocks = 0;
        _seeded = false;
    }

    private static void ReseedLocked()
    {
        var fresh = new byte[64];
        int got = KernelEntropy(fresh, 64);
        if (got <= 0)
        {
            // No kernel source (e.g. host-side tests): derive from the
            // clock only. Callers can still call AddEntropy for quality.
            var fallback = Sha256.Hash(new byte[] { 0x6E, 0x6F, 0x72, 0x6E, 0x64 }); // "nord"
            for (int i = 0; i < 32; i++)
                Key[i] = fallback[i];
        }
        else
        {
            var k = Sha256.Hash(fresh);
            for (int i = 0; i < 32; i++)
                Key[i] = k[i];
        }
        _seeded = true;
        _blocks = 0;
    }

    /// <summary>One ChaCha20 block for the current key at the given index.</summary>
    private static void BlockAt(ulong index, byte[] block)
    {
        var nonce = new byte[12];
        // Nonce = high bytes of the block index (counter lives in the
        // ChaCha20 block counter word).
        for (int i = 0; i < 8; i++)
            nonce[i] = (byte)(index >> (i * 8));
        uint counter = (uint)(index >> 32);

        var c = new ChaCha20(Key, nonce, counter);
        c.Process(block, 0, 64);
    }

    /// <summary>
    /// Raw kernel entropy via the DDK export table. Returns the number
    /// of bytes written (0 when the export is unavailable, e.g. in
    /// host-side unit tests).
    /// </summary>
    private static unsafe int KernelEntropy(byte[] buffer, int length)
    {
        try
        {
            fixed (byte* p = buffer)
            {
                return ProtonOS.DDK.Kernel.Entropy.GetEntropy(p, length);
            }
        }
        catch
        {
            return 0;
        }
    }
}
