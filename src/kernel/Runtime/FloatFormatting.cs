// NeutrinoOS kernel - floating point to string formatting
//
// Tomlyn builds the text of a FloatValueSyntax token with
// TomlFormatHelper.ToString(double) -> double.ToString("R", InvariantCulture)
// (and float.ToString("g9", ...) for Single), which the JIT resolves to the
// AOT bridges in AotMethodRegistry.PrimitiveHelpers. korlib's own
// Double.ToString()/Single.ToString() return the placeholder literals
// "Double"/"Single", so the conversion has to live here.
//
// The exact decimal expansion of the IEEE-754 value is generated with a
// small fixed-size bignum (no floating-point arithmetic is trusted for the
// digits), rounded to 17 (double) / 9 (float) significant digits, and then
// stripped of trailing zeros. 17 significant digits always round-trip a
// double and 9 always round-trip a float, so "R" semantics are satisfied.

namespace NeutrinoOS.Runtime;

public static unsafe class FloatFormatting
{
    // 64 x 32 = 2048 bits. The worst case is the smallest subnormal scaled by
    // 10^324 (~1077 bits) against 2^1074, and the largest double (1024 bits).
    private const int Limbs = 64;

    /// <summary>Double.ToString()-equivalent text (up to 17 significant digits).</summary>
    public static string FormatDouble(double value)
    {
        ulong bits = *(ulong*)&value;
        ulong absBits = bits & 0x7FFFFFFFFFFFFFFFUL;
        if (absBits > 0x7FF0000000000000UL) return "NaN";
        if (absBits == 0x7FF0000000000000UL)
            return (bits >> 63) != 0 ? "-Infinity" : "Infinity";

        bool negative = (bits >> 63) != 0;
        int exp = (int)((bits >> 52) & 0x7FFUL);
        ulong frac = bits & 0x000FFFFFFFFFFFFFUL;
        if (exp == 0 && frac == 0)
            return negative ? "-0" : "0";

        ulong mantissa;
        int e2;
        if (exp == 0)
        {
            mantissa = frac;      // subnormal
            e2 = -1074;
        }
        else
        {
            mantissa = frac | (1UL << 52);
            e2 = exp - 1075;      // value = mantissa * 2^e2
        }
        return FormatCore(mantissa, e2, negative, 17);
    }

    /// <summary>Single.ToString()-equivalent text (up to 9 significant digits).</summary>
    public static string FormatSingle(float value)
    {
        uint bits = *(uint*)&value;
        uint absBits = bits & 0x7FFFFFFF;
        if (absBits > 0x7F800000) return "NaN";
        if (absBits == 0x7F800000)
            return (bits >> 31) != 0 ? "-Infinity" : "Infinity";

        bool negative = (bits >> 31) != 0;
        int exp = (int)((bits >> 23) & 0xFF);
        uint frac = bits & 0x007FFFFF;
        if (exp == 0 && frac == 0)
            return negative ? "-0" : "0";

        ulong mantissa;
        int e2;
        if (exp == 0)
        {
            mantissa = frac;      // subnormal
            e2 = -149;
        }
        else
        {
            mantissa = frac | 0x00800000u;
            e2 = exp - 150;       // value = mantissa * 2^e2
        }
        return FormatCore(mantissa, e2, negative, 9);
    }

    // =========================================================================
    // Format-specifier support: F/f, N/n, E/e, G/g (+ optional precision).
    // Null, empty, "R" and unknown specifiers keep the shortest round-trip form
    // above, which is what .NET's "R" and default ToString() produce.
    // =========================================================================

    /// <summary>Double.ToString(format, provider)-equivalent text.</summary>
    public static string FormatDouble(double value, string? format)
        => FormatWithSpecifier(*(ulong*)&value, false, format);

    /// <summary>Single.ToString(format, provider)-equivalent text.</summary>
    public static string FormatSingle(float value, string? format)
    {
        // .NET widens a Single to Double when "G" asks for more significant
        // digits than a float has (G10..G17 print the exact expanded value).
        ParseFormatSpecifier(format, out char spec, out int precision);
        if (spec == 'G' && precision > 9)
            return FormatDouble((double)value, format);

        return FormatWithSpecifier((ulong)(*(uint*)&value), true, format);
    }

    private static string FormatWithSpecifier(ulong bits, bool isSingle, string? format)
    {
        ParseFormatSpecifier(format, out char spec, out int precision, out bool lowerExponent);

        int signShift = isSingle ? 31 : 63;
        ulong absBits = isSingle ? (bits & 0x7FFFFFFF) : (bits & 0x7FFFFFFFFFFFFFFFUL);
        ulong infBits = isSingle ? 0x7F800000UL : 0x7FF0000000000000UL;
        if (absBits > infBits) return "NaN";
        if (absBits == infBits)
            return (bits >> signShift) != 0 ? "-Infinity" : "Infinity";

        bool negative = (bits >> signShift) != 0;

        ulong mantissa;
        int e2;
        int sigDigits;
        if (isSingle)
        {
            int exp = (int)((bits >> 23) & 0xFF);
            uint frac = (uint)bits & 0x007FFFFF;
            sigDigits = 9;
            if (exp == 0 && frac == 0)
                return FormatZero(negative, spec, precision);
            if (exp == 0) { mantissa = frac; e2 = -149; }
            else { mantissa = frac | 0x00800000u; e2 = exp - 150; }
        }
        else
        {
            int exp = (int)((bits >> 52) & 0x7FFUL);
            ulong frac = bits & 0x000FFFFFFFFFFFFFUL;
            sigDigits = 17;
            if (exp == 0 && frac == 0)
                return FormatZero(negative, spec, precision);
            if (exp == 0) { mantissa = frac; e2 = -1074; }
            else { mantissa = frac | (1UL << 52); e2 = exp - 1075; }
        }

        if (spec == '\0')
            return FormatCore(mantissa, e2, negative, sigDigits, lowerExponent ? 'e' : 'E');

        return FormatSpecified(mantissa, e2, negative, spec, precision, sigDigits);
    }

    /// <summary>
    /// Reads "[specifier][digits]". spec is 'F'/'N'/'E'/'G' (E/G keep their case
    /// for the exponent letter), or '\0' for "shortest round-trip".
    /// </summary>
    private static void ParseFormatSpecifier(string? format, out char spec, out int precision)
        => ParseFormatSpecifier(format, out spec, out precision, out _);

    private static void ParseFormatSpecifier(string? format, out char spec, out int precision,
                                             out bool lowerExponent)
    {
        spec = '\0';
        precision = -1;
        lowerExponent = false;
        if (format == null || format.Length == 0)
            return;

        char letter = '\0';
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c >= '0' && c <= '9')
            {
                if (precision < 0) precision = 0;
                if (precision < 1000) precision = precision * 10 + (c - '0');
                continue;
            }
            if (letter == '\0')
                letter = c;
        }

        lowerExponent = letter == 'g' || letter == 'e' || letter == 'f' || letter == 'n';
        if (letter == 'F' || letter == 'f') spec = 'F';
        else if (letter == 'N' || letter == 'n') spec = 'N';
        else if (letter == 'E') spec = 'E';
        else if (letter == 'e') spec = 'e';
        else if ((letter == 'G' || letter == 'g') && precision >= 0) spec = letter;
        // "G"/"g" without a precision is the shortest round-trip form, which is
        // exactly the FormatCore path (spec stays '\0'), matching .NET.
    }

    private static string FormatZero(bool negative, char spec, int precision)
    {
        if (spec == '\0' || spec == 'G' || spec == 'g')
            return negative ? "-0" : "0";

        int p = precision < 0 ? (spec == 'E' || spec == 'e' ? 6 : 2) : precision;
        if (p > 99) p = 99;

        char* buf = stackalloc char[120];
        int w = 0;
        if (negative) buf[w++] = '-';
        buf[w++] = '0';
        if (p > 0)
        {
            buf[w++] = '.';
            for (int i = 0; i < p; i++) buf[w++] = '0';
        }
        if (spec == 'E' || spec == 'e')
        {
            buf[w++] = spec;
            AppendExponent(buf, ref w, 0, 3);
        }
        return new string(buf, 0, w);
    }

    private static void AppendExponent(char* buf, ref int w, int exp10, int minDigits)
    {
        if (exp10 < 0) { buf[w++] = '-'; exp10 = -exp10; }
        else buf[w++] = '+';

        int digits = 1;
        for (int v = exp10; v >= 10; v /= 10) digits++;
        if (digits < minDigits) digits = minDigits;
        for (int i = digits - 1; i >= 0; i--)
            buf[w++] = (char)('0' + (exp10 / Pow10Int(i)) % 10);
    }

    private static int Pow10Int(int exp)
    {
        int v = 1;
        for (int i = 0; i < exp; i++) v *= 10;
        return v;
    }

    /// <summary>
    /// Exact fixed/scientific rendering with rounding. mantissa*2^e2 is the
    /// absolute value of a non-zero finite number.
    /// </summary>
    private static string FormatSpecified(ulong mantissa, int e2, bool negative,
                                          char spec, int precision, int sigDigits)
    {
        uint* n = stackalloc uint[Limbs];
        uint* d = stackalloc uint[Limbs];
        uint* a = stackalloc uint[Limbs];
        uint* dd = stackalloc uint[Limbs];
        uint* r = stackalloc uint[Limbs];
        uint* t = stackalloc uint[Limbs];
        uint* u = stackalloc uint[Limbs];

        // value = N / D, exactly.
        int na, nd;
        if (e2 >= 0)
        {
            na = Shl(n, SetU64(n, mantissa), e2);
            nd = SetU64(d, 1);
        }
        else
        {
            na = SetU64(n, mantissa);
            nd = SetPow2(d, -e2);
        }

        // decExp = floor(log10(value)); A / Dd = value * 10^-decExp in [1, 10).
        int diff = BitLen(n, na) - BitLen(d, nd);
        int k = -Mul30103(diff);
        while (CompareScaled(n, na, k, d, nd, 1) < 0) k++;
        while (CompareScaled(n, na, k, d, nd, 10) >= 0) k--;

        int na2, nd2;
        if (k >= 0)
        {
            na2 = MulPow10(a, Copy(n, na, a), k);
            nd2 = Copy(d, nd, dd);
        }
        else
        {
            na2 = Copy(n, na, a);
            nd2 = MulPow10(dd, Copy(d, nd, dd), -k);
        }
        int decExp = -k;

        char* digits = stackalloc char[420];
        char* outBuf = stackalloc char[540];
        int clen;
        int outExp = decExp;

        if (spec == 'F' || spec == 'N')
        {
            int p = precision < 0 ? 2 : precision;
            if (p > 99) p = 99;

            int needed = decExp + 1 + p;      // digits covering the 10^-p place
            if (needed > 400) needed = 400;

            if (needed <= 0)
            {
                // Below half of the last place: compare 2*A*10^(decExp+p) with Dd.
                // An exact tie rounds to the even digit, which here is 0.
                int m = -(decExp + p);
                int nu = MulSmall(u, Copy(a, na2, u), 2);
                int ndd = MulPow10(r, Copy(dd, nd2, r), m);
                clen = 0;
                if (Cmp(u, nu, r, ndd) > 0)
                {
                    digits[0] = '1';
                    clen = 1;
                    outExp = -p;
                }
            }
            else
            {
                clen = DivToDigits(a, na2, dd, nd2, digits, needed, r, t, out bool carried);
                if (carried) outExp = decExp + 1;
            }

            return RenderFixed(digits, clen, outExp, negative, p, spec == 'N', outBuf);
        }

        if (spec == 'E' || spec == 'e')
        {
            int p = precision < 0 ? 6 : precision;
            if (p > 99) p = 99;
            int sig = p + 1;

            clen = DivToDigits(a, na2, dd, nd2, digits, sig, r, t, out bool carried);
            if (carried) outExp = decExp + 1;

            int w = 0;
            if (negative) outBuf[w++] = '-';
            outBuf[w++] = digits[0];
            if (clen > 1)
            {
                outBuf[w++] = '.';
                for (int i = 1; i < clen; i++) outBuf[w++] = digits[i];
            }
            outBuf[w++] = spec;                      // 'E' or 'e'
            AppendExponent(outBuf, ref w, outExp, 3);
            return new string(outBuf, 0, w);
        }

        // 'G'/'g': precision is significant digits (shortest form when absent).
        {
            int sig = precision <= 0 ? sigDigits : (precision > sigDigits ? sigDigits : precision);
            clen = DivToDigits(a, na2, dd, nd2, digits, sig, r, t, out bool carried);
            if (carried) outExp = decExp + 1;
            while (clen > 1 && digits[clen - 1] == '0') clen--;

            if (outExp < -4 || outExp >= sig)
            {
                int w = 0;
                if (negative) outBuf[w++] = '-';
                outBuf[w++] = digits[0];
                if (clen > 1)
                {
                    outBuf[w++] = '.';
                    for (int i = 1; i < clen; i++) outBuf[w++] = digits[i];
                }
                outBuf[w++] = spec == 'g' ? 'e' : 'E';
                AppendExponent(outBuf, ref w, outExp, 2);
                return new string(outBuf, 0, w);
            }

            int frac = clen - 1 - outExp;
            if (frac < 0) frac = 0;
            return RenderFixed(digits, clen, outExp, negative, frac, false, outBuf);
        }
    }

    /// <summary>
    /// Exact long division of A/Dd to `ndigits` digits, rounding the last digit
    /// half away from zero. Returns the number of digits written; a carry out of
    /// the leading digit (999 -> 1000) yields a single '1' and sets `carried`,
    /// in which case the caller bumps the decimal exponent.
    /// </summary>
    private static int DivToDigits(uint* a, int na, uint* dd, int nd, char* digits,
                                   int ndigits, uint* r, uint* t, out bool carried)
    {
        carried = false;
        if (ndigits <= 0) return 0;

        int nrem = Copy(a, na, r);
        for (int i = 0; i < ndigits; i++)
        {
            int dig = 0;
            while (Cmp(r, nrem, dd, nd) >= 0)
            {
                nrem = Sub(r, nrem, dd, nd);
                dig++;
            }
            digits[i] = (char)('0' + dig);
            if (i + 1 < ndigits)
                nrem = MulSmall(r, nrem, 10);
        }

        // Round: exactly like .NET, decimal ties go to the EVEN digit
        // (0.5:F0 -> "0", 2.5:F0 -> "2", 3.25:E1 -> "3.2").
        int nt = MulSmall(t, Copy(r, nrem, t), 2);
        int cmp = Cmp(t, nt, dd, nd);
        bool up = cmp > 0 || (cmp == 0 && ((digits[ndigits - 1] - '0') & 1) != 0);
        if (!up)
            return ndigits;

        int j = ndigits - 1;
        while (j >= 0 && digits[j] == '9')
        {
            digits[j] = '0';
            j--;
        }
        if (j >= 0)
        {
            digits[j]++;
            return ndigits;
        }
        // Carry out of the leading digit (999 -> 1000). Keep the requested digit
        // count so fixed/scientific renderers still pad ("9.999:E2" must print
        // 1.00E+001, not 1E+001); the caller bumps the exponent.
        digits[0] = '1';
        for (int i = 1; i < ndigits; i++)
            digits[i] = '0';
        carried = true;
        return ndigits;
    }

    /// <summary>
    /// Renders significant `digits` (first at place 10^outExp) in fixed point
    /// with exactly `frac` decimals, optionally grouping the integer part in
    /// threes.
    /// </summary>
    private static string RenderFixed(char* digits, int clen, int outExp, bool negative,
                                      int frac, bool grouping, char* outBuf)
    {
        int w = 0;
        if (negative) outBuf[w++] = '-';

        int intDigits = outExp + 1;
        if (intDigits <= 0)
        {
            outBuf[w++] = '0';
        }
        else
        {
            for (int i = 0; i < intDigits; i++)
            {
                outBuf[w++] = i < clen ? digits[i] : '0';
                int remaining = intDigits - 1 - i;
                if (grouping && remaining > 0 && remaining % 3 == 0)
                    outBuf[w++] = ',';
            }
        }

        if (frac > 0)
        {
            outBuf[w++] = '.';
            for (int place = -1; place >= -frac; place--)
            {
                int idx = outExp - place;
                outBuf[w++] = (idx >= 0 && idx < clen) ? digits[idx] : '0';
            }
        }

        return new string(outBuf, 0, w);
    }

    // =========================================================================
    // Core conversion: value = mantissa * 2^e2, positive and non-zero.
    // =========================================================================
    private static string FormatCore(ulong mantissa, int e2, bool negative, int sigDigits)
        => FormatCore(mantissa, e2, negative, sigDigits, 'E');

    private static string FormatCore(ulong mantissa, int e2, bool negative, int sigDigits, char expChar)
    {
        uint* n = stackalloc uint[Limbs];
        uint* d = stackalloc uint[Limbs];
        uint* a = stackalloc uint[Limbs];
        uint* dd = stackalloc uint[Limbs];
        uint* r = stackalloc uint[Limbs];
        uint* t = stackalloc uint[Limbs];
        uint* c = stackalloc uint[Limbs];
        uint* hi = stackalloc uint[Limbs];
        uint* lo = stackalloc uint[Limbs];
        uint* half = stackalloc uint[Limbs];

        // value = N / D with both sides exact integers.
        int na, nd;
        if (e2 >= 0)
        {
            na = SetU64(n, mantissa);
            na = Shl(n, na, e2);
            nd = SetU64(d, 1);
        }
        else
        {
            na = SetU64(n, mantissa);
            nd = SetPow2(d, -e2);
        }

        // value = 2^(bitsN - bitsD), so decimal exponent ~ (bitsN-bitsD)*log10(2).
        int diff = BitLen(n, na) - BitLen(d, nd);
        int k = -Mul30103(diff);   // value * 10^k ~= 1

        // Exact correction: 10^(-k) <= value < 10^(-k+1).
        while (CompareScaled(n, na, k, d, nd, 1) < 0) k++;
        while (CompareScaled(n, na, k, d, nd, 10) >= 0) k--;

        // A / Dd in [1, 10): A = N * 10^k (or Dd = D * 10^-k).
        int na2, nd2;
        if (k >= 0)
        {
            na2 = Copy(n, na, a);
            na2 = MulPow10(a, na2, k);
            nd2 = Copy(d, nd, dd);
        }
        else
        {
            na2 = Copy(n, na, a);
            nd2 = Copy(d, nd, dd);
            nd2 = MulPow10(dd, nd2, -k);
        }

        // Shortest round-trip search. For each candidate digit count len the
        // exact quotient is divided to len digits, then the two neighbouring
        // len-digit decimals (round down and round up) are tried, nearest
        // first, and the first one that maps back to this value is used. The
        // first len that succeeds is what "R"/"G17"/"G9" print; 17/9 digits
        // always succeed, so the search is bounded.
        int decExp = -k;              // value = d.ddd x 10^decExp
        char* digits = stackalloc char[20];
        char* cand = stackalloc char[20];

        for (int len = 1; len <= sigDigits; len++)
        {
            // Exact long division of A / Dd, len digits, remainder left in r.
            int nrem = Copy(a, na2, r);
            for (int i = 0; i < len; i++)
            {
                int dig = 0;
                while (Cmp(r, nrem, dd, nd2) >= 0)
                {
                    nrem = Sub(r, nrem, dd, nd2);
                    dig++;
                }
                cand[i] = (char)('0' + dig);
                if (i + 1 < len)
                    nrem = MulSmall(r, nrem, 10);
            }

            // Which neighbour is nearer: compare 2 * remainder with the divisor.
            int nt = Copy(r, nrem, t);
            nt = MulSmall(t, nt, 2);
            int halfCmp = Cmp(t, nt, dd, nd2);
            bool nearestUp = halfCmp > 0 ||
                             (halfCmp == 0 && ((cand[len - 1] - '0') & 1) != 0);

            // Variant 0 is the nearer neighbour, variant 1 the other one. The
            // interval is asymmetric on a binade boundary, so a rejected
            // nearest neighbour does not imply the other one is out too.
            for (int variant = 0; variant < 2; variant++)
            {
                bool roundUp = variant == 0 ? nearestUp : !nearestUp;
                int clen = len;
                int y = decExp - len + 1;   // place value of the last digit
                for (int i = 0; i < len; i++) digits[i] = cand[i];
                if (roundUp)
                {
                    int i = len - 1;
                    while (i >= 0 && digits[i] == '9')
                    {
                        digits[i] = '0';
                        i--;
                    }
                    if (i >= 0)
                    {
                        digits[i]++;
                    }
                    else
                    {
                        // 99..9 rounded up carries into a new leading digit:
                        // the candidate is 1 x 10^(decExp + 1).
                        digits[0] = '1';
                        clen = 1;
                        y = decExp + 1;
                    }
                }

                if (len == sigDigits)
                    return Render(digits, clen, y + clen - 1, negative, sigDigits, expChar);

                int ncx = SetFromDigits(digits, clen, c);
                if (InsideRoundingInterval(c, ncx, y, mantissa, e2, half,
                                           sigDigits == 17 ? 52 : 23))
                {
                    return Render(digits, clen, y + clen - 1, negative, sigDigits, expChar);
                }
            }
        }

        return "0";   // unreachable: len == sigDigits always returns
    }

    /// <summary>
    /// True when C*10^y rounds back to value = N*2^E, i.e. it lies inside the
    /// interval (value - half an ulp, value + half an ulp). The endpoints are
    /// reachable only when the mantissa is even (ties-to-even lands on this
    /// value), and the lower endpoint is only half a ulp away when the mantissa
    /// sits exactly on a binade boundary.
    /// </summary>
    private static bool InsideRoundingInterval(uint* c, int nc, int y, ulong N, int E, uint* half, int implicitBit)
    {
        // Scale every term by 10^max(-y,0) * 2^max(1-E,0) so all three sides are
        // integers. At most one of the two scale factors is ever large.
        int tensBinary = y > 0 ? 0 : -y;       // scales the binary side
        int tensDecimal = y > 0 ? y : 0;       // scales the decimal side
        int shiftDecimal = 1 - E > 0 ? 1 - E : 0;
        int shiftBinary = E > 1 ? E : 1;
        int halfBit = E - 1 > 0 ? E - 1 : 0;   // 2^halfBit is half an ulp
        bool binade = N == (1UL << implicitBit);

        uint* xs = stackalloc uint[Limbs];
        int nxs = Copy(c, nc, xs);
        nxs = MulPow10(xs, nxs, tensDecimal);
        nxs = Shl(xs, nxs, shiftDecimal);

        uint* hi = stackalloc uint[Limbs];
        int nhi = SetU64(hi, N);
        nhi = Shl(hi, nhi, shiftBinary);
        nhi = AddPow2(hi, nhi, halfBit);

        uint* lo = stackalloc uint[Limbs];
        int nlo = SetU64(lo, N);
        nlo = Shl(lo, nlo, shiftBinary);
        int nhalf = SetPow2(half, halfBit);
        nlo = Sub(lo, nlo, half, nhalf);

        if (binade)
        {
            // Exactly on a power of two the gap below is half the gap above, so
            // double the other two terms to keep everything integral.
            nxs = Shl(xs, nxs, 1);
            nhi = Shl(hi, nhi, 1);
            nlo = Shl(lo, nlo, 1);
            nlo = AddPow2(lo, nlo, halfBit);
        }

        nhi = MulPow10(hi, nhi, tensBinary);
        nlo = MulPow10(lo, nlo, tensBinary);

        int cLow = Cmp(xs, nxs, lo, nlo);
        int cHigh = Cmp(xs, nxs, hi, nhi);
        if (cLow < 0 || cHigh > 0) return false;
        if ((N & 1) == 0) return true;         // even mantissa: endpoints round here
        return cLow > 0 && cHigh < 0;
    }

    private static string Render(char* digits, int ndig, int decExp, bool negative, int sciExp, char expChar)
    {
        char* outBuf = stackalloc char[40];
        int p = 0;
        if (negative) outBuf[p++] = '-';

        // Matches .NET's shortest round-trip ("G17" for double, "G9" for
        // single) notation choice: fixed point for -4 <= exp < sciExp,
        // scientific outside it.
        if (decExp < -4 || decExp >= sciExp)
        {
            // Scientific: d.dddE+XX
            outBuf[p++] = digits[0];
            if (ndig > 1)
            {
                outBuf[p++] = '.';
                for (int i = 1; i < ndig; i++) outBuf[p++] = digits[i];
            }
            outBuf[p++] = expChar;
            int e = decExp;
            if (e < 0)
            {
                outBuf[p++] = '-';
                e = -e;
            }
            else
            {
                outBuf[p++] = '+';
            }
            if (e >= 100) outBuf[p++] = (char)('0' + e / 100);
            outBuf[p++] = (char)('0' + (e / 10) % 10);
            outBuf[p++] = (char)('0' + e % 10);
        }
        else if (decExp >= 0)
        {
            // Fixed point with an integer part.
            int intLen = decExp + 1;
            for (int i = 0; i < intLen; i++)
                outBuf[p++] = i < ndig ? digits[i] : '0';
            if (ndig > intLen)
            {
                outBuf[p++] = '.';
                for (int i = intLen; i < ndig; i++) outBuf[p++] = digits[i];
            }
        }
        else
        {
            // 0.000ddd
            outBuf[p++] = '0';
            outBuf[p++] = '.';
            for (int i = 0; i < -decExp - 1; i++) outBuf[p++] = '0';
            for (int i = 0; i < ndig; i++) outBuf[p++] = digits[i];
        }

        return new string(outBuf, 0, p);
    }

    // =========================================================================
    // Minimal fixed-size unsigned bignum helpers (little-endian 32-bit limbs).
    // =========================================================================

    private static void Clear(uint* a)
    {
        for (int i = 0; i < Limbs; i++) a[i] = 0;
    }

    private static int SetU64(uint* a, ulong v)
    {
        Clear(a);
        if (v == 0) return 0;
        a[0] = (uint)v;
        a[1] = (uint)(v >> 32);
        return a[1] != 0 ? 2 : 1;
    }

    private static int SetPow2(uint* a, int bit)
    {
        Clear(a);
        a[bit >> 5] = (uint)1 << (bit & 31);
        return (bit >> 5) + 1;
    }

    private static int Trim(uint* a, int n)
    {
        while (n > 0 && a[n - 1] == 0) n--;
        return n;
    }

    private static int Copy(uint* src, int n, uint* dst)
    {
        for (int i = 0; i < n; i++) dst[i] = src[i];
        for (int i = n; i < Limbs; i++) dst[i] = 0;
        return n;
    }

    private static int Shl(uint* a, int n, int bits)
    {
        int limbs = bits >> 5;
        int sh = bits & 31;
        if (sh != 0)
        {
            uint carry = 0;
            for (int i = 0; i < n; i++)
            {
                uint cur = a[i];
                a[i] = (cur << sh) | carry;
                carry = cur >> (32 - sh);
            }
            if (carry != 0)
            {
                a[n] = carry;
                n++;
            }
        }
        if (limbs != 0)
        {
            for (int i = n - 1; i >= 0; i--) a[i + limbs] = a[i];
            for (int i = 0; i < limbs; i++) a[i] = 0;
            n += limbs;
        }
        return Trim(a, n);
    }

    private static int MulSmall(uint* a, int n, uint m)
    {
        ulong carry = 0;
        for (int i = 0; i < n; i++)
        {
            ulong cur = (ulong)a[i] * m + carry;
            a[i] = (uint)cur;
            carry = cur >> 32;
        }
        while (carry != 0)
        {
            a[n] = (uint)carry;
            n++;
            carry >>= 32;
        }
        // A top limb can become zero when the product wraps a limb boundary
        // (e.g. 0x80000000 * 10 == 0x500000000). Cmp relies on n being tight.
        return Trim(a, n);
    }

    private static int MulPow10(uint* a, int n, int k)
    {
        while (k >= 9)
        {
            n = MulSmall(a, n, 1000000000u);
            k -= 9;
        }
        if (k > 0)
        {
            uint m = 1;
            for (int i = 0; i < k; i++) m *= 10;
            n = MulSmall(a, n, m);
        }
        return n;
    }

    private static int AddSmall(uint* a, int n, uint v)
    {
        ulong carry = v;
        for (int i = 0; i < n && carry != 0; i++)
        {
            ulong cur = (ulong)a[i] + carry;
            a[i] = (uint)cur;
            carry = cur >> 32;
        }
        if (carry != 0)
        {
            a[n] = (uint)carry;
            n++;
        }
        return Trim(a, n);
    }

    private static int AddPow2(uint* a, int n, int bit)
    {
        int idx = bit >> 5;
        while (n < idx) a[n++] = 0;
        ulong carry = (uint)1 << (bit & 31);
        int i = idx;
        while (carry != 0)
        {
            if (i >= n)
            {
                a[i] = 0;
                n = i + 1;
            }
            ulong cur = (ulong)a[i] + carry;
            a[i] = (uint)cur;
            carry = cur >> 32;
            i++;
        }
        return Trim(a, n);
    }

    private static int SetFromDigits(char* s, int len, uint* a)
    {
        Clear(a);
        int n = 0;
        for (int i = 0; i < len; i++)
        {
            n = MulSmall(a, n, 10);
            n = AddSmall(a, n, (uint)(s[i] - '0'));
        }
        return n;
    }

    private static int Cmp(uint* a, int na, uint* b, int nb)
    {
        if (na != nb) return na > nb ? 1 : -1;
        for (int i = na - 1; i >= 0; i--)
        {
            if (a[i] != b[i]) return a[i] > b[i] ? 1 : -1;
        }
        return 0;
    }

    private static int Sub(uint* a, int na, uint* b, int nb)
    {
        long borrow = 0;
        for (int i = 0; i < na; i++)
        {
            long cur = (long)a[i] - (i < nb ? b[i] : 0) - borrow;
            if (cur < 0)
            {
                cur += 0x100000000L;
                borrow = 1;
            }
            else
            {
                borrow = 0;
            }
            a[i] = (uint)cur;
        }
        return Trim(a, na);
    }

    private static int BitLen(uint* a, int n)
    {
        if (n == 0) return 0;
        uint top = a[n - 1];
        int bits = 0;
        while (top != 0)
        {
            top >>= 1;
            bits++;
        }
        return (n - 1) * 32 + bits;
    }

    /// <summary>Compares N*10^k against D*mul.</summary>
    private static int CompareScaled(uint* n, int na, int k, uint* d, int nd, uint mul)
    {
        uint* t = stackalloc uint[Limbs];
        if (k >= 0)
        {
            int nt = Copy(n, na, t);
            nt = MulPow10(t, nt, k);
            uint* u = stackalloc uint[Limbs];
            int nu = Copy(d, nd, u);
            if (mul != 1) nu = MulSmall(u, nu, mul);
            return Cmp(t, nt, u, nu);
        }
        else
        {
            int nt = Copy(d, nd, t);
            nt = MulPow10(t, nt, -k);
            if (mul != 1) nt = MulSmall(t, nt, mul);
            return Cmp(n, na, t, nt);
        }
    }

    /// <summary>floor(v * 0.30103) - log10(2) scaled, integer only.</summary>
    private static int Mul30103(int v)
    {
        long num = (long)v * 30103L;
        if (num >= 0) return (int)(num / 100000L);
        return (int)((num - 99999L) / 100000L);
    }
}
