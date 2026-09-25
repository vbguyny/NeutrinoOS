// NeutrinoOS Phase 8 - shared packaging library (NeutrinoOS.Packaging).
//
// Json: a minimal JSON DOM (JsonObject/JsonArray), a hand-written recursive
// descent parser and a deterministic writer.  Used for manifest.json,
// checksum metadata, repository.json and the installed database; runs both
// under the Tier-0 JIT (korlib subset) and desktop .NET, so no
// System.Text.Json, no CultureInfo and no BCL number formatting is allowed.

using System;
using System.Collections.Generic;
using System.Text;

namespace NeutrinoOS.Packaging
{
    /// <summary>
    /// Phase 8: insertion-ordered JSON object used for package manifests,
    /// repository indexes and configuration files. Keys keep their first
    /// insertion order; Set replaces the value while keeping the position.
    /// </summary>
    public sealed class JsonObject
    {
        // Phase 8: array-backed storage with a linear scan replaces the
        // original List/Dictionary pair. The Tier-0 JIT's generic
        // instantiation machinery crashed inside Dictionary/List callvirt
        // chains on the first on-device use (npkg repo list parse), so the
        // JSON DOM must not depend on those types at all.
        private string[] _keys = new string[4];
        private object[] _values = new object[4];
        private int _count;

        /// <summary>Phase 8: number of members in the object.</summary>
        public int Count
        {
            get { return _count; }
        }

        /// <summary>
        /// Phase 8: adds a member or replaces the value of an existing key
        /// (the original insertion position is preserved).
        /// </summary>
        public void Set(string key, object value)
        {
            if (key == null)
                throw new ArgumentNullException("key");

            for (int i = 0; i < _count; i++)
            {
                if (_keys[i] == key)
                {
                    _values[i] = value;
                    return;
                }
            }
            if (_count == _keys.Length)
                Grow();
            _keys[_count] = key;
            _values[_count] = value;
            _count++;
        }

        /// <summary>Phase 8: true when the key is present (even with a null value).</summary>
        public bool Has(string key)
        {
            if (key == null)
                return false;
            for (int i = 0; i < _count; i++)
            {
                if (_keys[i] == key)
                    return true;
            }
            return false;
        }

        /// <summary>Phase 8: raw member value (string/bool/long/double/JsonObject/JsonArray/null); null when missing.</summary>
        public object Get(string key)
        {
            if (key == null)
                return null;
            for (int i = 0; i < _count; i++)
            {
                if (_keys[i] == key)
                    return _values[i];
            }
            return null;
        }

        /// <summary>Phase 8: string member; null when absent or of another type.</summary>
        public string GetString(string key)
        {
            return GetString(key, null);
        }

        /// <summary>Phase 8: string member with a fallback for absent/other-typed values.</summary>
        public string GetString(string key, string defaultValue)
        {
            object value = Get(key);
            string text = value as string;
            return text == null ? defaultValue : text;
        }

        /// <summary>
        /// Phase 8: integer member (stored as long by the parser); whole
        /// doubles also convert. Falls back to defaultValue when absent or
        /// not numeric.
        /// </summary>
        public long GetLong(string key, long defaultValue)
        {
            object value = Get(key);
            if (value is long)
                return (long)value;
            if (value is int)
                return (long)(int)value;
            if (value is double)
            {
                double d = (double)value;
                if (d == (double)(long)d)
                    return (long)d;
            }
            return defaultValue;
        }

        /// <summary>Phase 8: boolean member; defaultValue when absent or of another type.</summary>
        public bool GetBool(string key, bool defaultValue)
        {
            object value = Get(key);
            if (value is bool)
                return (bool)value;
            return defaultValue;
        }

        /// <summary>Phase 8: nested object member; null when absent or of another type.</summary>
        public JsonObject GetObject(string key)
        {
            return Get(key) as JsonObject;
        }

        /// <summary>Phase 8: nested array member; null when absent or of another type.</summary>
        public JsonArray GetArray(string key)
        {
            return Get(key) as JsonArray;
        }

        /// <summary>Phase 8: all keys in insertion order (a copy).</summary>
        public string[] Keys()
        {
            string[] copy = new string[_count];
            for (int i = 0; i < _count; i++)
                copy[i] = _keys[i];
            return copy;
        }

        /// <summary>Phase 8: doubles the backing arrays (manual copy).</summary>
        private void Grow()
        {
            int cap = _keys.Length * 2;
            string[] newKeys = new string[cap];
            object[] newValues = new object[cap];
            for (int i = 0; i < _count; i++)
            {
                newKeys[i] = _keys[i];
                newValues[i] = _values[i];
            }
            _keys = newKeys;
            _values = newValues;
        }
    }

    /// <summary>
    /// Phase 8: JSON array used for "provides", payload lists and repository
    /// package lists. Values follow the same set as JsonObject.
    /// </summary>
    public sealed class JsonArray
    {
        // See JsonObject: array-backed storage; no List<T> use on purpose.
        private object[] _items = new object[4];
        private int _count;

        /// <summary>Phase 8: number of elements in the array.</summary>
        public int Count
        {
            get { return _count; }
        }

        /// <summary>Phase 8: appends an element (string/bool/long/double/JsonObject/JsonArray/null).</summary>
        public void Add(object value)
        {
            if (_count == _items.Length)
                Grow();
            _items[_count] = value;
            _count++;
        }

        /// <summary>Phase 8: element at an index; throws when the index is out of range.</summary>
        public object Get(int index)
        {
            if (index < 0 || index >= _count)
                throw new ArgumentOutOfRangeException("index");
            return _items[index];
        }

        /// <summary>Phase 8: doubles the backing array (manual copy).</summary>
        private void Grow()
        {
            int cap = _items.Length * 2;
            object[] items = new object[cap];
            for (int i = 0; i < _count; i++)
                items[i] = _items[i];
            _items = items;
        }
    }

    /// <summary>
    /// Phase 8: JSON parser/writer for the packaging formats. The parser is
    /// strict (throws System.FormatException with a character position on
    /// any syntax error) and the writer is deterministic so that manifest
    /// bytes - and therefore Ed25519 signatures - are reproducible.
    /// </summary>
    public static class Json
    {
        /// <summary>
        /// Phase 8: parses JSON text into a JsonObject or JsonArray tree
        /// (numbers integral and within long range become long, otherwise
        /// double; null/bool/string as expected). Scalar top-level values
        /// are rejected; malformed input throws FormatException.
        /// </summary>
        public static object Parse(string text)
        {
            if (text == null)
                throw new ArgumentNullException("text");

            Parser parser = new Parser(text);
            object value = parser.ParseValue();
            parser.SkipWhitespace();
            if (!parser.AtEnd)
                throw parser.Error("unexpected trailing characters");
            if (!(value is JsonObject) && !(value is JsonArray))
                throw parser.Error("top-level JSON value must be an object or an array");
            return value;
        }

        /// <summary>Phase 8: compact serialization (no whitespace).</summary>
        public static string Write(object value)
        {
            return Write(value, false);
        }

        /// <summary>
        /// Phase 8: serialization with optional 2-space pretty printing;
        /// long/double scalars use TextConv/manual formatting so the output
        /// is culture-independent.
        /// </summary>
        public static string Write(object value, bool pretty)
        {
            StringBuilder sb = new StringBuilder(256);
            WriteValue(sb, value, pretty, 0);
            return sb.ToString();
        }

        /// <summary>
        /// Phase 8: escapes a string as a JSON string literal (quotes,
        /// backslash, control characters as \b \f \n \r \t or \uXXXX);
        /// null becomes the JSON literal null.
        /// </summary>
        public static string EscapeString(string s)
        {
            if (s == null)
                return "null";

            StringBuilder sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"')
                    sb.Append("\\\"");
                else if (c == '\\')
                    sb.Append("\\\\");
                else if (c == '\b')
                    sb.Append("\\b");
                else if (c == '\f')
                    sb.Append("\\f");
                else if (c == '\n')
                    sb.Append("\\n");
                else if (c == '\r')
                    sb.Append("\\r");
                else if (c == '\t')
                    sb.Append("\\t");
                else if (c < ' ')
                {
                    sb.Append("\\u");
                    AppendHex4(sb, c);
                }
                else
                {
                    sb.Append(c);
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value, bool pretty, int indent)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }
            if (value is bool)
            {
                sb.Append((bool)value ? "true" : "false");
                return;
            }
            if (value is string)
            {
                sb.Append(EscapeString((string)value));
                return;
            }
            if (value is long)
            {
                sb.Append(TextConv.LongToString((long)value));
                return;
            }
            if (value is int)
            {
                sb.Append(TextConv.LongToString((long)(int)value));
                return;
            }
            if (value is double)
            {
                sb.Append(DoubleToString((double)value));
                return;
            }
            if (value is JsonObject)
            {
                WriteObject(sb, (JsonObject)value, pretty, indent);
                return;
            }
            if (value is JsonArray)
            {
                WriteArray(sb, (JsonArray)value, pretty, indent);
                return;
            }
            throw new FormatException("Json.Write: unsupported value type");
        }

        private static void WriteObject(StringBuilder sb, JsonObject obj, bool pretty, int indent)
        {
            sb.Append('{');
            string[] keys = obj.Keys();
            for (int i = 0; i < keys.Length; i++)
            {
                if (i > 0)
                    sb.Append(',');
                if (pretty)
                {
                    sb.Append('\n');
                    AppendIndent(sb, indent + 1);
                }
                sb.Append(EscapeString(keys[i]));
                sb.Append(':');
                if (pretty)
                    sb.Append(' ');
                WriteValue(sb, obj.Get(keys[i]), pretty, indent + 1);
            }
            if (pretty && keys.Length > 0)
            {
                sb.Append('\n');
                AppendIndent(sb, indent);
            }
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, JsonArray arr, bool pretty, int indent)
        {
            sb.Append('[');
            for (int i = 0; i < arr.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                if (pretty)
                {
                    sb.Append('\n');
                    AppendIndent(sb, indent + 1);
                }
                WriteValue(sb, arr.Get(i), pretty, indent + 1);
            }
            if (pretty && arr.Count > 0)
            {
                sb.Append('\n');
                AppendIndent(sb, indent);
            }
            sb.Append(']');
        }

        private static void AppendIndent(StringBuilder sb, int indent)
        {
            for (int i = 0; i < indent; i++)
                sb.Append("  ");
        }

        private static void AppendHex4(StringBuilder sb, int value)
        {
            sb.Append(HexDigit((value >> 12) & 0xF));
            sb.Append(HexDigit((value >> 8) & 0xF));
            sb.Append(HexDigit((value >> 4) & 0xF));
            sb.Append(HexDigit(value & 0xF));
        }

        private static char HexDigit(int v)
        {
            return v < 10 ? (char)('0' + v) : (char)('a' + (v - 10));
        }

        /// <summary>
        /// Phase 8: double formatting without double.ToString(): whole values
        /// within long range take the long path, everything else uses a
        /// simple fixed format (integer part, '.', up to 6 fractional digits,
        /// trailing zeros stripped). Non-finite values serialize as 0.
        /// </summary>
        private static string DoubleToString(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
                return "0"; // JSON has no non-finite numbers
            if (v == 0.0)
                return "0";

            bool negative = v < 0;
            if (negative)
                v = -v;

            // Scale huge magnitudes down so the integer part fits in a long;
            // every double >= 2^53 is an integer, so no fraction is lost.
            int zeros = 0;
            while (v >= 9.0e15 && zeros < 320)
            {
                v = v / 10.0;
                zeros++;
            }
            if (v >= 9.0e15)
                return "0"; // defensive: unreachable for finite doubles

            long integerPart = (long)v;
            double fraction = v - (double)integerPart;

            long fracDigits = (long)(fraction * 1000000.0 + 0.5);
            if (fracDigits >= 1000000)
            {
                fracDigits = 0;
                integerPart++;
            }

            int width = 6;
            while (fracDigits > 0 && fracDigits % 10 == 0)
            {
                fracDigits = fracDigits / 10;
                width--;
            }

            StringBuilder sb = new StringBuilder(24);
            if (negative)
                sb.Append('-');
            sb.Append(TextConv.LongToString(integerPart));
            if (fracDigits > 0)
            {
                sb.Append('.');
                int digitCount = 0;
                long t = fracDigits;
                while (t > 0)
                {
                    digitCount++;
                    t = t / 10;
                }
                for (int i = 0; i < width - digitCount; i++)
                    sb.Append('0');
                sb.Append(TextConv.LongToString(fracDigits));
            }
            for (int i = 0; i < zeros; i++)
                sb.Append('0');
            return sb.ToString();
        }

        /// <summary>
        /// Phase 8: strict recursive descent parser over the JSON text.
        /// Position information is embedded in every FormatException.
        /// </summary>
        private sealed class Parser
        {
            private readonly string _text;
            private int _i;

            public Parser(string text)
            {
                _text = text;
                _i = 0;
            }

            public bool AtEnd
            {
                get { return _i >= _text.Length; }
            }

            public FormatException Error(string message)
            {
                return new FormatException("JSON: " + message + " at position " + TextConv.LongToString((long)_i));
            }

            public void SkipWhitespace()
            {
                while (_i < _text.Length)
                {
                    char c = _text[_i];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                        _i++;
                    else
                        break;
                }
            }

            public object ParseValue()
            {
                SkipWhitespace();
                if (AtEnd)
                    throw Error("unexpected end of input");

                char c = _text[_i];
                if (c == '{')
                    return ParseObject();
                if (c == '[')
                    return ParseArray();
                if (c == '"')
                    return ParseString();
                if (c == 't')
                {
                    ExpectLiteral("true");
                    return true;
                }
                if (c == 'f')
                {
                    ExpectLiteral("false");
                    return false;
                }
                if (c == 'n')
                {
                    ExpectLiteral("null");
                    return null;
                }
                if (c == '-' || (c >= '0' && c <= '9'))
                    return ParseNumber();
                throw Error("unexpected character");
            }

            private object ParseObject()
            {
                _i++; // '{'
                JsonObject obj = new JsonObject();
                SkipWhitespace();
                if (!AtEnd && _text[_i] == '}')
                {
                    _i++;
                    return obj;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || _text[_i] != '"')
                        throw Error("expected object key");
                    string key = ParseString();
                    SkipWhitespace();
                    if (AtEnd || _text[_i] != ':')
                        throw Error("expected ':' after object key");
                    _i++;
                    object value = ParseValue();
                    obj.Set(key, value);
                    SkipWhitespace();
                    if (AtEnd)
                        throw Error("unterminated object");
                    if (_text[_i] == ',')
                    {
                        _i++;
                        continue;
                    }
                    if (_text[_i] == '}')
                    {
                        _i++;
                        return obj;
                    }
                    throw Error("expected ',' or '}' in object");
                }
            }

            private object ParseArray()
            {
                _i++; // '['
                JsonArray arr = new JsonArray();
                SkipWhitespace();
                if (!AtEnd && _text[_i] == ']')
                {
                    _i++;
                    return arr;
                }

                while (true)
                {
                    object value = ParseValue();
                    arr.Add(value);
                    SkipWhitespace();
                    if (AtEnd)
                        throw Error("unterminated array");
                    if (_text[_i] == ',')
                    {
                        _i++;
                        continue;
                    }
                    if (_text[_i] == ']')
                    {
                        _i++;
                        return arr;
                    }
                    throw Error("expected ',' or ']' in array");
                }
            }

            private string ParseString()
            {
                _i++; // '"'
                StringBuilder sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd)
                        throw Error("unterminated string");
                    char c = _text[_i];
                    _i++;
                    if (c == '"')
                        return sb.ToString();
                    if (c == '\\')
                    {
                        if (AtEnd)
                            throw Error("unterminated escape sequence");
                        char esc = _text[_i];
                        _i++;
                        if (esc == '"')
                            sb.Append('"');
                        else if (esc == '\\')
                            sb.Append('\\');
                        else if (esc == '/')
                            sb.Append('/');
                        else if (esc == 'b')
                            sb.Append('\b');
                        else if (esc == 'f')
                            sb.Append('\f');
                        else if (esc == 'n')
                            sb.Append('\n');
                        else if (esc == 'r')
                            sb.Append('\r');
                        else if (esc == 't')
                            sb.Append('\t');
                        else if (esc == 'u')
                        {
                            sb.Append(ParseUnicodeEscape());
                        }
                        else
                        {
                            throw Error("invalid escape sequence");
                        }
                    }
                    else if (c < ' ')
                    {
                        throw Error("unescaped control character in string");
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }

            private char ParseUnicodeEscape()
            {
                int value = 0;
                for (int k = 0; k < 4; k++)
                {
                    if (AtEnd)
                        throw Error("truncated \\u escape");
                    char c = _text[_i];
                    _i++;
                    int digit;
                    if (c >= '0' && c <= '9')
                        digit = c - '0';
                    else if (c >= 'a' && c <= 'f')
                        digit = c - 'a' + 10;
                    else if (c >= 'A' && c <= 'F')
                        digit = c - 'A' + 10;
                    else
                        throw Error("invalid \\u escape");
                    value = (value << 4) | digit;
                }
                return (char)value;
            }

            private object ParseNumber()
            {
                int start = _i;
                bool negative = false;
                if (_text[_i] == '-')
                {
                    negative = true;
                    _i++;
                    if (AtEnd)
                        throw Error("invalid number");
                }

                if (AtEnd)
                    throw Error("invalid number");
                if (_text[_i] == '0')
                {
                    _i++;
                    if (!AtEnd && _text[_i] >= '0' && _text[_i] <= '9')
                        throw Error("leading zeros are not allowed");
                }
                else if (_text[_i] >= '1' && _text[_i] <= '9')
                {
                    while (!AtEnd && _text[_i] >= '0' && _text[_i] <= '9')
                        _i++;
                }
                else
                {
                    throw Error("invalid number");
                }

                bool isInteger = true;
                if (!AtEnd && _text[_i] == '.')
                {
                    isInteger = false;
                    _i++;
                    if (AtEnd || _text[_i] < '0' || _text[_i] > '9')
                        throw Error("invalid fraction");
                    while (!AtEnd && _text[_i] >= '0' && _text[_i] <= '9')
                        _i++;
                }
                if (!AtEnd && (_text[_i] == 'e' || _text[_i] == 'E'))
                {
                    isInteger = false;
                    _i++;
                    if (!AtEnd && (_text[_i] == '+' || _text[_i] == '-'))
                        _i++;
                    if (AtEnd || _text[_i] < '0' || _text[_i] > '9')
                        throw Error("invalid exponent");
                    while (!AtEnd && _text[_i] >= '0' && _text[_i] <= '9')
                        _i++;
                }

                int end = _i;
                if (isInteger)
                {
                    long integerValue;
                    if (TextConv.TryParseLong(_text.Substring(start, end - start), out integerValue))
                        return integerValue;
                }
                return ParseDouble(start, end, negative);
            }

            /// <summary>Manually converts a validated number token to double.</summary>
            private double ParseDouble(int start, int end, bool negative)
            {
                int i = start;
                if (_text[i] == '-')
                    i++;

                double mantissa = 0.0;
                int exponent = 0;
                int skippedIntegerDigits = 0;
                while (i < end && _text[i] >= '0' && _text[i] <= '9')
                {
                    if (mantissa < 1.0e15)
                        mantissa = mantissa * 10.0 + (double)(_text[i] - '0');
                    else
                        skippedIntegerDigits++;
                    i++;
                }
                if (i < end && _text[i] == '.')
                {
                    i++;
                    while (i < end && _text[i] >= '0' && _text[i] <= '9')
                    {
                        if (mantissa < 1.0e15)
                        {
                            mantissa = mantissa * 10.0 + (double)(_text[i] - '0');
                            exponent--;
                        }
                        i++;
                    }
                }
                exponent += skippedIntegerDigits;
                if (i < end && (_text[i] == 'e' || _text[i] == 'E'))
                {
                    i++;
                    int expSign = 1;
                    if (i < end && _text[i] == '+')
                        i++;
                    else if (i < end && _text[i] == '-')
                    {
                        expSign = -1;
                        i++;
                    }
                    int expValue = 0;
                    while (i < end && _text[i] >= '0' && _text[i] <= '9')
                    {
                        if (expValue < 10000)
                            expValue = expValue * 10 + (_text[i] - '0');
                        i++;
                    }
                    exponent += expSign * expValue;
                }

                double result = mantissa;
                if (exponent > 308)
                    exponent = 308;
                else if (exponent < -308)
                    exponent = -308;
                if (exponent > 0)
                {
                    for (int k = 0; k < exponent; k++)
                        result = result * 10.0;
                }
                else if (exponent < 0)
                {
                    for (int k = 0; k < -exponent; k++)
                        result = result / 10.0;
                }
                return negative ? -result : result;
            }

            private void ExpectLiteral(string literal)
            {
                for (int k = 0; k < literal.Length; k++)
                {
                    if (AtEnd || _text[_i] != literal[k])
                        throw Error("invalid literal");
                    _i++;
                }
            }
        }
    }
}
