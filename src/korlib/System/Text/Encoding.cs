// NeutrinoOS korlib - System.Text.Encoding (minimal)
//
// Phase 2: a minimal character-encoding base class sufficient for
// Console.OutputEncoding / Console.InputEncoding and for common
// Encoding.GetBytes / GetString usage in console applications.
// This is NOT the full .NET encoding subsystem: code pages, fallback
// objects, encoder/decoder state, and stream adapters are not implemented.

using System.Runtime.InteropServices;

namespace System.Text;

/// <summary>
/// Represents a character encoding. NeutrinoOS implements only a minimal
/// subset of the official .NET BCL surface (see Phase 2 deviations in the
/// project docs): <see cref="UTF8"/> is fully functional, while ASCII and
/// Latin-1 conversions fall back to UTF-8 semantics.
/// </summary>
public abstract class Encoding
{
    /// <summary>The UTF-8 encoding instance.</summary>
    public static Encoding UTF8 { get; } = new UTF8Encoding(false);

    /// <summary>The ASCII encoding instance (mapped to UTF-8 in Phase 2).</summary>
    public static Encoding ASCII { get; } = new UTF8Encoding(false);

    /// <summary>Gets the name of this encoding.</summary>
    public abstract string EncodingName { get; }

    /// <summary>Gets the number of bytes produced by encoding the string.</summary>
    public abstract int GetByteCount(string s);

    /// <summary>Encodes the string into a new byte array.</summary>
    public abstract byte[] GetBytes(string s);

    /// <summary>Encodes the character range into a new byte array.</summary>
    public abstract byte[] GetBytes(char[] chars, int index, int count);

    /// <summary>Decodes the byte array into a new string.</summary>
    public abstract string GetString(byte[] bytes);

    /// <summary>Decodes the byte range into a new string.</summary>
    public abstract string GetString(byte[] bytes, int index, int count);

    /// <summary>Gets the number of characters produced by decoding the byte array.</summary>
    public abstract int GetCharCount(byte[] bytes);

    /// <summary>Returns the name of this encoding.</summary>
    public override string ToString() => EncodingName;
}
