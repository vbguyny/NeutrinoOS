// ProtonOS Kernel - minimal JSON reader for driver-package metadata.
//
// The shared NeutrinoOS.Packaging parser cannot be compiled into the bflat
// AOT kernel: its writer inspects values with `is bool`, which forces
// Boolean's MethodTable (and its ToString) into the image, where bflat's
// eager code generation for bool.ToString() fails ("Code generation failed
// for method 'bool.ToString()'").
//
// This reader parses objects/arrays/strings/numbers only. true/false/null
// are tolerated but become plain text slices, so no CLR bool value is ever
// materialized. It is intentionally small: driver manifests and
// installed.json need object trees, string members and string arrays with a
// handful of keys, which a linear scan over the object's members serves fine.

using System;

namespace ProtonOS.Drivers;

/// <summary>
/// One parsed JSON object: insertion-ordered keys with string, object or
/// array values (arrays hold string/object element slots).
/// </summary>
public sealed class MiniJsonObject
{
    // Every JSON value is a MiniJsonObject node tagged with Kind.
    // Discriminating by an int instead of isinst keeps the bflat IL
    // scanner away from System.Runtime.TypeCast helpers (korlib has no
    // IsInstanceOfAny, and array casts pull it in).
    internal const int KindNull = 0;
    internal const int KindString = 1;
    internal const int KindObject = 2;
    internal const int KindArray = 3;
    internal const int KindRaw = 4;

    internal int Kind;
    internal string Str;
    internal MiniJsonObject[] Items;

    private readonly string[] _keys;
    private readonly MiniJsonObject[] _values;
    private readonly int _count;

    internal MiniJsonObject(string[] keys, MiniJsonObject[] values, int count)
    {
        _keys = keys;
        _values = values;
        _count = count;
        Kind = KindObject;
    }

    private MiniJsonObject()
    {
        _keys = new string[0];
        _values = new MiniJsonObject[0];
        _count = 0;
    }

    // Node factory helpers (parser use).
    internal static MiniJsonObject NodeString(string s)
    {
        MiniJsonObject n = new MiniJsonObject();
        n.Kind = KindString;
        n.Str = s;
        return n;
    }

    internal static MiniJsonObject NodeRaw(string s)
    {
        MiniJsonObject n = new MiniJsonObject();
        n.Kind = KindRaw;
        n.Str = s;
        return n;
    }

    internal static MiniJsonObject NodeArray(MiniJsonObject[] items)
    {
        MiniJsonObject n = new MiniJsonObject();
        n.Kind = KindArray;
        n.Items = items;
        return n;
    }

    /// <summary>String member, or null when absent or not a string.</summary>
    public string GetString(string key)
    {
        MiniJsonObject v = Find(key);
        return (v != null && v.Kind == KindString) ? v.Str : null;
    }

    /// <summary>Object member, or null when absent or not an object.</summary>
    public MiniJsonObject GetObject(string key)
    {
        MiniJsonObject v = Find(key);
        return (v != null && v.Kind == KindObject) ? v : null;
    }

    /// <summary>
    /// String element array (non-string elements are skipped), or null when
    /// the member is absent or not an array.
    /// </summary>
    public string[] GetStringArray(string key)
    {
        MiniJsonObject v = Find(key);
        if (v == null || v.Kind != KindArray || v.Items == null)
            return null;

        MiniJsonObject[] items = v.Items;
        string[] result = new string[items.Length];
        int n = 0;
        for (int i = 0; i < items.Length; i++)
        {
            MiniJsonObject item = items[i];
            if (item != null && item.Kind == KindString)
                result[n++] = item.Str;
        }
        if (n == result.Length)
            return result;

        string[] exact = new string[n];
        for (int i = 0; i < n; i++)
            exact[i] = result[i];
        return exact;
    }

    /// <summary>Array member (element nodes), or null when absent.</summary>
    public MiniJsonObject[] GetObjectArray(string key)
    {
        MiniJsonObject v = Find(key);
        return (v != null && v.Kind == KindArray) ? v.Items : null;
    }

    /// <summary>True when the key is present.</summary>
    public bool Has(string key)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_keys[i] == key)
                return true;
        }
        return false;
    }

    private MiniJsonObject Find(string key)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_keys[i] == key)
                return _values[i];
        }
        return null;
    }
}

/// <summary>Recursive-descent reader producing MiniJsonObject trees.</summary>
public static class MiniJson
{
    /// <summary>
    /// Parse an object document. Throws FormatException on malformed input
    /// (same contract as the packaging Json parser for objects).
    /// </summary>
    public static MiniJsonObject Parse(string text)
    {
        if (text == null)
            throw new FormatException("MiniJson: no text");

        Parser parser = new Parser(text);
        parser.SkipWs();
        if (!parser.TryConsume('{'))
            throw new FormatException("MiniJson: root value must be an object");
        return parser.ParseObjectBody();
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s)
        {
            _s = s;
            _i = 0;
        }

        public void SkipWs()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                    _i++;
                else
                    break;
            }
        }

        public bool TryConsume(char c)
        {
            if (_i < _s.Length && _s[_i] == c)
            {
                _i++;
                return true;
            }
            return false;
        }

        /// <summary>Object body; the opening '{' is already consumed.</summary>
        public MiniJsonObject ParseObjectBody()
        {
            string[] keys = new string[8];
            MiniJsonObject[] values = new MiniJsonObject[8];
            int count = 0;

            SkipWs();
            if (TryConsume('}'))
                return new MiniJsonObject(new string[0], new MiniJsonObject[0], 0);

            while (true)
            {
                SkipWs();
                if (!TryConsume('"'))
                    throw new FormatException("MiniJson: expected a key string");
                string key = ParseStringBody();
                SkipWs();
                if (!TryConsume(':'))
                    throw new FormatException("MiniJson: expected ':'");
                SkipWs();

                MiniJsonObject value = ParseValue();

                if (count == keys.Length)
                {
                    string[] keys2 = new string[count * 2];
                    MiniJsonObject[] values2 = new MiniJsonObject[count * 2];
                    for (int i = 0; i < count; i++)
                    {
                        keys2[i] = keys[i];
                        values2[i] = values[i];
                    }
                    keys = keys2;
                    values = values2;
                }
                keys[count] = key;
                values[count] = value;
                count++;

                SkipWs();
                if (TryConsume(','))
                    continue;
                if (TryConsume('}'))
                    break;
                throw new FormatException("MiniJson: expected ',' or '}'");
            }

            return new MiniJsonObject(keys, values, count);
        }

        private MiniJsonObject ParseValue()
        {
            if (_i >= _s.Length)
                throw new FormatException("MiniJson: value expected");

            char c = _s[_i];
            if (c == '"')
            {
                _i++;
                return MiniJsonObject.NodeString(ParseStringBody());
            }
            if (c == '{')
            {
                _i++;
                return ParseObjectBody();
            }
            if (c == '[')
            {
                _i++;
                return ParseArrayBody();
            }

            // Numbers, true/false and null: keep the raw text slice.
            int start = _i;
            while (_i < _s.Length)
            {
                char d = _s[_i];
                if (d == ',' || d == '}' || d == ']' || d == ' ' || d == '\t' || d == '\n' || d == '\r')
                    break;
                _i++;
            }
            if (_i == start)
                throw new FormatException("MiniJson: empty value");
            return MiniJsonObject.NodeRaw(Slice(_s, start, _i - start));
        }

        /// <summary>Array body; the opening '[' is already consumed.</summary>
        private MiniJsonObject ParseArrayBody()
        {
            MiniJsonObject[] items = new MiniJsonObject[4];
            int n = 0;

            SkipWs();
            if (TryConsume(']'))
                return MiniJsonObject.NodeArray(new MiniJsonObject[0]);

            while (true)
            {
                SkipWs();
                MiniJsonObject value = ParseValue();
                if (n == items.Length)
                {
                    MiniJsonObject[] items2 = new MiniJsonObject[n * 2];
                    for (int i = 0; i < n; i++)
                        items2[i] = items[i];
                    items = items2;
                }
                items[n++] = value;

                SkipWs();
                if (TryConsume(','))
                    continue;
                if (TryConsume(']'))
                    break;
                throw new FormatException("MiniJson: expected ',' or ']'");
            }

            if (n == items.Length)
                return MiniJsonObject.NodeArray(items);
            MiniJsonObject[] exact = new MiniJsonObject[n];
            for (int i = 0; i < n; i++)
                exact[i] = items[i];
            return MiniJsonObject.NodeArray(exact);
        }

        /// <summary>String body; the opening '"' is already consumed.</summary>
        private string ParseStringBody()
        {
            char[] buf = new char[256];
            int n = 0;

            while (true)
            {
                if (_i >= _s.Length)
                    throw new FormatException("MiniJson: unterminated string");
                char c = _s[_i++];
                if (c == '"')
                    break;

                if (c == '\\')
                {
                    if (_i >= _s.Length)
                        throw new FormatException("MiniJson: bad escape");
                    char e = _s[_i++];
                    if (e == '"') c = '"';
                    else if (e == '\\') c = '\\';
                    else if (e == '/') c = '/';
                    else if (e == 'n') c = '\n';
                    else if (e == 't') c = '\t';
                    else if (e == 'r') c = '\r';
                    else if (e == 'b') c = '\b';
                    else if (e == 'f') c = '\f';
                    else if (e == 'u')
                    {
                        uint v = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            if (_i >= _s.Length)
                                throw new FormatException("MiniJson: bad \\u escape");
                            v = (v << 4) | HexDigit(_s[_i++]);
                        }
                        c = (char)v;
                    }
                    else
                        throw new FormatException("MiniJson: unknown escape");
                }

                if (n == buf.Length)
                {
                    char[] buf2 = new char[n * 2];
                    for (int i = 0; i < n; i++)
                        buf2[i] = buf[i];
                    buf = buf2;
                }
                buf[n++] = c;
            }

            char[] exact = new char[n];
            for (int i = 0; i < n; i++)
                exact[i] = buf[i];
            return new string(exact);
        }
    }

    private static uint HexDigit(char c)
    {
        if (c >= '0' && c <= '9') return (uint)(c - '0');
        if (c >= 'a' && c <= 'f') return (uint)(c - 'a' + 10);
        if (c >= 'A' && c <= 'F') return (uint)(c - 'A' + 10);
        throw new FormatException("MiniJson: bad hex digit");
    }

    private static string Slice(string s, int start, int length)
    {
        if (length <= 0)
            return "";
        char[] chars = new char[length];
        for (int i = 0; i < length; i++)
            chars[i] = s[start + i];
        return new string(chars);
    }
}
