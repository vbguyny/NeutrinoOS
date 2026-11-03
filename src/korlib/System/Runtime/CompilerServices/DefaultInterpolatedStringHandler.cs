// NeutrinoOS korlib - DefaultInterpolatedStringHandler
//
// The handler the C# compiler targets for `$"..."` (C# 10+): a ref struct with
// a growable char buffer, so interpolation does not need StringBuilder.
//
// Format specifiers (`{v:X4}`, `{v:D6}`, `{v,10:X}`) are honoured for the
// integer types and char by generating the digits here. korlib's System.Int32 /
// Int64 / ... exist only in the kernel (Primitives.cs is excluded from
// korlib.dll) and the BCL ToString(string, IFormatProvider) overloads have no
// device bridge, so formatting them from korlib would not resolve at JIT time.
// Every overload - generic and non-generic - goes through FormatValue, so the
// specifier is honoured wherever Roslyn routes the value.
// Floating point honours F/f, N/n (grouped), E/e and G/g with an optional
// precision through the device's float bridge (FloatFormatting), which is reached
// by naming System.Double/Single::ToString(string, IFormatProvider) - the stubs in
// PrimitiveStubs.cs exist so korlib can name it, and the kernel maps the member to
// the real formatter. The provider is accepted for API compatibility and not used:
// korlib declares its own System.IFormatProvider (System/IFormatProvider.cs), a
// different type from the BCL one the bridged signatures take, and device number
// formatting is invariant.

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Provides a handler used by the language compiler to process interpolated strings into String instances.
    /// </summary>
    [InterpolatedStringHandler]
    public ref struct DefaultInterpolatedStringHandler
    {
        private char[] _chars;
        private int _pos;

        /// <summary>
        /// Creates a handler used to translate an interpolated string into a String.
        /// </summary>
        public DefaultInterpolatedStringHandler(int literalLength, int formattedCount)
        {
            // Estimate capacity: literal chars + some space per formatted value
            int estimatedCapacity = literalLength + (formattedCount * 16);
            _chars = new char[estimatedCapacity];
            _pos = 0;
        }

        /// <summary>
        /// Creates a handler for an interpolated string that carries a format
        /// provider (the shape Roslyn uses for provider-aware interpolation).
        /// Device number formatting is invariant, so the provider is accepted
        /// for API compatibility and not yet used (see the file header).
        /// </summary>
        public DefaultInterpolatedStringHandler(int literalLength, int formattedCount, IFormatProvider? provider)
            : this(literalLength, formattedCount)
        {
        }

        private void EnsureCapacity(int additionalChars)
        {
            if (_pos + additionalChars > _chars.Length)
            {
                int newCapacity = _chars.Length * 2;
                if (newCapacity < _pos + additionalChars)
                    newCapacity = _pos + additionalChars + 16;

                char[] newChars = new char[newCapacity];
                for (int i = 0; i < _pos; i++)
                    newChars[i] = _chars[i];
                _chars = newChars;
            }
        }

        /// <summary>
        /// Writes the specified string to the handler.
        /// </summary>
        public void AppendLiteral(string value)
        {
            if (value == null) return;
            EnsureCapacity(value.Length);
            for (int i = 0; i < value.Length; i++)
                _chars[_pos++] = value[i];
        }

        /// <summary>
        /// Writes the specified value to the handler.
        /// </summary>
        public void AppendFormatted<T>(T value)
        {
            if (value is null)
                return;

            string? str = FormatValue(value, null);
            if (str != null)
                AppendLiteral(str);
        }

        /// <summary>
        /// Writes the specified value to the handler with a format specifier.
        /// </summary>
        /// <remarks>
        /// Roslyn emits a *generic* instantiation for lowered interpolations
        /// (`AppendFormatted&lt;int&gt;(int, string)`), so the specifier reaches the
        /// value formatter here. `X`/`D` are honoured for the integer types and
        /// char (see FormatValue); floating point still ignores the specifier
        /// because korlib's System.Double is a stub and the device float bridges
        /// are not format-aware yet.
        /// </remarks>
        public void AppendFormatted<T>(T value, string? format)
        {
            if (value is null)
                return;

            string? str = FormatValue(value, format);
            if (str != null)
                AppendLiteral(str);
        }

        /// <summary>
        /// Writes the specified value to the handler with alignment.
        /// </summary>
        public void AppendFormatted<T>(T value, int alignment)
        {
            if (value is null)
                return;

            string? str = FormatValue(value, null);
            if (str != null)
                AppendPadded(str, alignment);
        }

        /// <summary>
        /// Writes the specified value to the handler with alignment and format.
        /// </summary>
        public void AppendFormatted<T>(T value, int alignment, string? format)
        {
            if (value is null)
                return;

            string? str = FormatValue(value, format);
            if (str != null)
                AppendPadded(str, alignment);
        }

        /// <summary>
        /// Writes the specified string to the handler.
        /// </summary>
        public void AppendFormatted(string? value)
        {
            if (value != null)
                AppendLiteral(value);
        }

        /// <summary>
        /// Writes the specified string to the handler with alignment.
        /// </summary>
        public void AppendFormatted(string? value, int alignment = 0, string? format = null)
        {
            if (value != null)
                AppendPadded(value, alignment);
        }

        /// <summary>
        /// Writes the specified object to the handler.
        /// </summary>
        public void AppendFormatted(object? value, int alignment = 0, string? format = null)
        {
            if (value is null)
                return;
            string? text = FormatValue(value, format);
            if (text is not null)
                AppendPadded(text, alignment);
        }

        /// <summary>
        /// Appends text padded with spaces to |alignment| characters (positive
        /// alignment right-aligns, negative left-aligns, 0 appends as-is).
        /// </summary>
        private void AppendPadded(string text, int alignment)
        {
            if (alignment > 0 && text.Length < alignment)
            {
                int padding = alignment - text.Length;
                EnsureCapacity(alignment);
                for (int i = 0; i < padding; i++)
                    _chars[_pos++] = ' ';
                for (int i = 0; i < text.Length; i++)
                    _chars[_pos++] = text[i];
            }
            else if (alignment < 0 && text.Length < -alignment)
            {
                int padding = -alignment - text.Length;
                EnsureCapacity(-alignment);
                for (int i = 0; i < text.Length; i++)
                    _chars[_pos++] = text[i];
                for (int i = 0; i < padding; i++)
                    _chars[_pos++] = ' ';
            }
            else
            {
                AppendLiteral(text);
            }
        }

        /// <summary>
        /// Produces the text for one formatted value.
        /// </summary>
        private static string? FormatValue<T>(T value, string? format)
        {
            if (value is null)
                return null;

            if (format is null || format.Length == 0)
                return value.ToString();

            // Integer types and char: the specifier (X/x, D/d, G/g) and its width
            // are honoured here - see the file header for why korlib cannot defer
            // to the kernel's Int32.ToString(string, IFormatProvider).
            if (value is int i)
                return FormatSigned(i, (uint)i, format);
            if (value is uint ui)
                return FormatUnsigned(ui, format);
            if (value is long l)
                return FormatSigned(l, (ulong)l, format);
            if (value is ulong ul)
                return FormatUnsigned(ul, format);
            if (value is short sh)
                return FormatSigned(sh, (ushort)sh, format);
            if (value is ushort ush)
                return FormatUnsigned(ush, format);
            if (value is sbyte sb)
                return FormatSigned(sb, (byte)sb, format);
            if (value is byte by)
                return FormatUnsigned(by, format);
            if (value is char ch)
                return FormatUnsigned(ch, format);

            // Floating point: the format (F/N/E/G with an optional precision) is
            // honoured by the device's float bridge - korlib names the
            // ToString(string, IFormatProvider) overload through the stub types in
            // PrimitiveStubs.cs, and AotMethodRegistry maps that member to
            // FloatFormatting (see Double_ToString_Format). The stub BODY never
            // runs; the kernel bridge does. The overload only exists in the
            // KORLIB_IL build (the stubs are IL-only), so the kernel's own AOT
            // copy of this file keeps the default text.
#if KORLIB_IL
            if (value is double dbl)
                return dbl.ToString(format, null);
            if (value is float flt)
                return flt.ToString(format, null);
#endif

            // Everything else: keep the default text.
            return value.ToString();
        }

        /// <summary>Parses "[specifier][width]" (e.g. "X4", "D6"; width optional).</summary>
        private static void ParseFormat(string? format, out char specifier, out int width)
        {
            specifier = '\0';
            width = 0;
            if (format is null || format.Length == 0)
                return;
            specifier = format[0];
            for (int i = 1; i < format.Length; i++)
            {
                char c = format[i];
                if (c >= '0' && c <= '9')
                    width = width * 10 + (c - '0');
            }
        }

        /// <summary>
        /// Formats a signed value: `value` is the number, `bits` its raw
        /// two's-complement pattern (what X/x renders, matching .NET).
        /// </summary>
        private static string FormatSigned(long value, ulong bits, string? format)
        {
            ParseFormat(format, out char specifier, out int width);

            if (specifier == 'X' || specifier == 'x')
                return Digits(bits, false, 16, width, specifier == 'X');

            bool negative = value < 0;
            // -(long.MinValue) overflows, so take the magnitude via ulong.
            ulong magnitude = negative ? (ulong)(-(value + 1)) + 1UL : (ulong)value;
            bool zeroPad = specifier == 'D' || specifier == 'd';
            return Digits(magnitude, negative, 10, zeroPad ? width : 0, true);
        }

        /// <summary>Formats an unsigned value ('X'/'x' hex, 'D'/'d' zero-padded decimal).</summary>
        private static string FormatUnsigned(ulong value, string? format)
        {
            ParseFormat(format, out char specifier, out int width);

            if (specifier == 'X' || specifier == 'x')
                return Digits(value, false, 16, width, specifier == 'X');

            bool zeroPad = specifier == 'D' || specifier == 'd';
            return Digits(value, false, 10, zeroPad ? width : 0, true);
        }

        /// <summary>
        /// Renders `value` in `radix`, zero-padded to `width`, with an optional
        /// leading '-'. Pure arithmetic - no dependency on the primitive types'
        /// ToString overloads, which are kernel-only / unbridged for formats.
        /// </summary>
        private static string Digits(ulong value, bool negative, int radix, int width, bool upper)
        {
            char[] scratch = new char[70];   // 64 bits in binary, 20 in decimal
            int pos = scratch.Length;

            if (value == 0)
            {
                scratch[--pos] = '0';
            }
            else
            {
                ulong radixValue = (ulong)radix;
                while (value > 0)
                {
                    int digit = (int)(value % radixValue);
                    value /= radixValue;
                    scratch[--pos] = (char)(digit < 10 ? '0' + digit : (upper ? 'A' : 'a') + (digit - 10));
                }
            }

            int digitCount = scratch.Length - pos;
            int pad = width > digitCount ? width - digitCount : 0;
            int total = digitCount + pad + (negative ? 1 : 0);

            char[] result = new char[total];
            int w = 0;
            if (negative)
                result[w++] = '-';
            for (int i = 0; i < pad; i++)
                result[w++] = '0';
            for (int i = pos; i < scratch.Length; i++)
                result[w++] = scratch[i];
            return new string(result, 0, total);
        }

        /// <summary>
        /// Gets the built String and clears the handler.
        /// </summary>
        public string ToStringAndClear()
        {
            string result = new string(_chars, 0, _pos);
            _pos = 0;
            return result;
        }

        /// <summary>
        /// Gets the built String.
        /// </summary>
        public override string ToString()
        {
            return new string(_chars, 0, _pos);
        }
    }
}
