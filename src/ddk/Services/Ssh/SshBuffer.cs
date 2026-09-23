// ProtonOS DDK - SSH wire buffers (Phase 6)
//
// SshWriter/SshReader implement the SSH data-type encodings (RFC 4251):
// byte, boolean, uint32, string, mpint and name-lists, on top of plain
// byte arrays.

using System;

namespace ProtonOS.DDK.Services.Ssh;

/// <summary>Growable write buffer for SSH payloads.</summary>
public sealed class SshWriter
{
    private byte[] _data;
    private int _length;

    /// <summary>Create a writer with a starting capacity.</summary>
    public SshWriter(int capacity)
    {
        _data = new byte[capacity < 64 ? 64 : capacity];
        _length = 0;
    }

    /// <summary>Bytes written so far.</summary>
    public int Length => _length;

    /// <summary>The backing buffer (valid up to Length).</summary>
    public byte[] Data => _data;

    /// <summary>Copy of the written bytes.</summary>
    public byte[] ToArray()
    {
        var result = new byte[_length];
        for (int i = 0; i < _length; i++)
            result[i] = _data[i];
        return result;
    }

    private void Ensure(int extra)
    {
        if (_length + extra <= _data.Length)
            return;
        int cap = _data.Length * 2;
        while (cap < _length + extra)
            cap *= 2;
        var bigger = new byte[cap];
        for (int i = 0; i < _length; i++)
            bigger[i] = _data[i];
        _data = bigger;
    }

    /// <summary>Write one byte.</summary>
    public void WriteByte(byte b)
    {
        Ensure(1);
        _data[_length++] = b;
    }

    /// <summary>Write a boolean.</summary>
    public void WriteBool(bool v) => WriteByte(v ? (byte)1 : (byte)0);

    /// <summary>Write a big-endian uint32.</summary>
    public void WriteU32(uint v)
    {
        Ensure(4);
        _data[_length++] = (byte)(v >> 24);
        _data[_length++] = (byte)(v >> 16);
        _data[_length++] = (byte)(v >> 8);
        _data[_length++] = (byte)v;
    }

    /// <summary>Write raw bytes.</summary>
    public void WriteRaw(byte[] data, int offset, int count)
    {
        Ensure(count);
        for (int i = 0; i < count; i++)
            _data[_length++] = data[offset + i];
    }

    /// <summary>Write a length-prefixed string.</summary>
    public void WriteString(byte[] data)
    {
        WriteU32((uint)data.Length);
        WriteRaw(data, 0, data.Length);
    }

    /// <summary>Write a length-prefixed ASCII string.</summary>
    public void WriteString(string s)
    {
        WriteU32((uint)s.Length);
        Ensure(s.Length);
        for (int i = 0; i < s.Length; i++)
            _data[_length++] = (byte)s[i];
    }

    /// <summary>Write a big-endian integer as an mpint (adds the
    /// two's-complement zero byte when the top bit is set).</summary>
    public void WriteMpint(byte[] value)
    {
        if (value.Length == 0)
        {
            WriteU32(0);
            return;
        }
        bool pad = (value[0] & 0x80) != 0;
        WriteU32((uint)(value.Length + (pad ? 1 : 0)));
        if (pad)
            _data[_length++] = 0;
        WriteRaw(value, 0, value.Length);
    }
}

/// <summary>Reader over an SSH payload.</summary>
public sealed class SshReader
{
    private readonly byte[] _data;
    private int _pos;

    /// <summary>Create a reader over the given bytes.</summary>
    public SshReader(byte[] data)
    {
        _data = data;
        _pos = 0;
    }

    /// <summary>Create a reader over a slice.</summary>
    public SshReader(byte[] data, int offset, int length)
    {
        _data = new byte[length];
        for (int i = 0; i < length; i++)
            _data[i] = data[offset + i];
        _pos = 0;
    }

    /// <summary>Bytes not yet consumed.</summary>
    public int Remaining => _data.Length - _pos;

    /// <summary>Current offset.</summary>
    public int Position => _pos;

    /// <summary>Read one byte; 0 when exhausted.</summary>
    public byte ReadByte()
        => _pos < _data.Length ? _data[_pos++] : (byte)0;

    /// <summary>Read a boolean.</summary>
    public bool ReadBool() => ReadByte() != 0;

    /// <summary>Read a big-endian uint32.</summary>
    public uint ReadU32()
    {
        uint v = 0;
        for (int i = 0; i < 4; i++)
            v = (v << 8) | ReadByte();
        return v;
    }

    /// <summary>Read a length-prefixed string; null when malformed.</summary>
    public byte[] ReadString()
    {
        uint len = ReadU32();
        if (len > (uint)Remaining)
            return null;
        var result = new byte[len];
        for (uint i = 0; i < len; i++)
            result[i] = _data[_pos++];
        return result;
    }

    /// <summary>Read a length-prefixed string as ASCII; null when malformed.</summary>
    public string ReadAscii()
    {
        var bytes = ReadString();
        if (bytes == null)
            return null;
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
            chars[i] = (char)bytes[i];
        return new string(chars);
    }

    /// <summary>Read a name-list as a string; null when malformed.</summary>
    public string ReadNameList() => ReadAscii();
}

/// <summary>Name-list helpers for algorithm negotiation.</summary>
public static class SshNames
{
    /// <summary>Index of the next comma at or after <paramref name="start"/>,
    /// or -1. (String.IndexOf(char,int) is unresolvable in the guest JIT.)</summary>
    private static int NextComma(string s, int start)
    {
        for (int i = start; i < s.Length; i++)
        {
            if (s[i] == ',')
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Pick the first entry of <paramref name="client"/> (the client's
    /// preference order) that also appears in <paramref name="ours"/>.
    /// </summary>
    public static string PickFirstMutual(string client, string[] ours)
    {
        if (client == null)
            return null;
        int start = 0;
        while (start <= client.Length)
        {
            int comma = NextComma(client, start);
            string item = comma < 0
                ? client.Substring(start, client.Length - start)
                : client.Substring(start, comma - start);
            if (item.Length > 0)
            {
                for (int i = 0; i < ours.Length; i++)
                {
                    if (StrEq(item, ours[i]))
                        return item;
                }
            }
            if (comma < 0)
                break;
            start = comma + 1;
        }
        return null;
    }

    /// <summary>True when the list contains the item.</summary>
    public static bool Contains(string list, string item)
    {
        if (list == null)
            return false;
        int start = 0;
        while (start <= list.Length)
        {
            int comma = NextComma(list, start);
            string part = comma < 0
                ? list.Substring(start, list.Length - start)
                : list.Substring(start, comma - start);
            if (StrEq(part, item))
                return true;
            if (comma < 0)
                break;
            start = comma + 1;
        }
        return false;
    }

    private static bool StrEq(string a, string b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }

    /// <summary>The first comma-separated entry of a name-list (null when empty).</summary>
    public static string FirstItem(string list)
    {
        if (list == null || list.Length == 0)
            return null;
        int comma = NextComma(list, 0);
        return comma < 0 ? list : list.Substring(0, comma);
    }

    /// <summary>Case-sensitive equality (exposed for negotiation code).</summary>
    public static bool Eq(string a, string b) => StrEq(a, b);
}
