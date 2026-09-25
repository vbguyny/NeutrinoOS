// ProtonOS Kernel - shared hex formatting for built-in driver logging.
//
// Drivers log through IDriverServices.Log with pre-built strings; this is
// the small shared helper they use for addresses and lengths (bflat-safe:
// char arrays + the proven string(char[]) constructor).

namespace ProtonOS.Drivers.Builtin;

/// <summary>Hex formatting helper for built-in driver log messages.</summary>
internal static class BuiltinHex
{
    /// <summary>Formats a value as uppercase hex without leading zeros.</summary>
    public static string Hex(ulong value)
    {
        char[] buf = new char[16];
        for (int i = 15; i >= 0; i--)
        {
            buf[i] = HexDigit((int)(value & 0xF));
            value >>= 4;
        }
        int start = 0;
        while (start < 15 && buf[start] == '0')
            start++;
        char[] result = new char[16 - start];
        for (int i = 0; i < result.Length; i++)
            result[i] = buf[start + i];
        return new string(result);
    }

    private static char HexDigit(int v)
    {
        v &= 0xF;
        return v < 10 ? (char)('0' + v) : (char)('A' + (v - 10));
    }
}
