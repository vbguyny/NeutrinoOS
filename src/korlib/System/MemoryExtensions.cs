// NeutrinoOS korlib - System.MemoryExtensions (M8: NuGet package support)
//
// The span/memory extension surface packages build against (M8:
// Microsoft.Extensions.Primitives' StringSegment uses the char-span
// helpers). The string overloads are COPY-BASED: the kernel's Memory<T>
// is array-backed (see Memory.cs) and strings are pinned-on-demand only
// through spans built here, so converting to char[] keeps reads correct
// at the cost of one copy per call site. Span-vs-span operations are
// ordinal character walks (documented comparison semantics: only
// Ordinal is exact; other StringComparison values fall back to ordinal).

namespace System;

/// <summary>Span/Memory helpers over strings and char spans (see header).</summary>
public static class MemoryExtensions
{
    // === string -> span/memory ===

    /// <summary>Read-only span over the whole string (points into the string's chars).</summary>
    public static unsafe ReadOnlySpan<char> AsSpan(this string? text)
    {
        if (text == null)
            return default;
        fixed (char* p = text)
            return new ReadOnlySpan<char>(p, text.Length);
    }

    /// <summary>Read-only span from start to end of the string.</summary>
    public static unsafe ReadOnlySpan<char> AsSpan(this string? text, int start)
    {
        if (text == null)
        {
            if (start != 0)
                throw new ArgumentOutOfRangeException("start");
            return default;
        }
        if ((uint)start > (uint)text.Length)
            throw new ArgumentOutOfRangeException("start");
        fixed (char* p = text)
            return new ReadOnlySpan<char>(p + start, text.Length - start);
    }

    /// <summary>Read-only span over a string range.</summary>
    public static unsafe ReadOnlySpan<char> AsSpan(this string? text, int start, int length)
    {
        if (text == null)
        {
            if (start != 0 || length != 0)
                throw new ArgumentOutOfRangeException("start");
            return default;
        }
        if ((uint)start > (uint)text.Length || (uint)length > (uint)(text.Length - start))
            throw new ArgumentOutOfRangeException("length");
        fixed (char* p = text)
            return new ReadOnlySpan<char>(p + start, length);
    }

    /// <summary>Read-only memory over the whole string (copy-based).</summary>
    public static ReadOnlyMemory<char> AsMemory(this string? text)
    {
        if (text == null)
            return ReadOnlyMemory<char>.Empty;
        return new ReadOnlyMemory<char>(text.ToCharArray());
    }

    /// <summary>Read-only memory from start to end of the string.</summary>
    public static ReadOnlyMemory<char> AsMemory(this string? text, int start)
    {
        if (text == null)
        {
            if (start != 0)
                throw new ArgumentOutOfRangeException("start");
            return ReadOnlyMemory<char>.Empty;
        }
        if ((uint)start > (uint)text.Length)
            throw new ArgumentOutOfRangeException("start");
        return new ReadOnlyMemory<char>(text.ToCharArray(), start, text.Length - start);
    }

    /// <summary>Read-only memory over a string range (BCL signature: (string, int, int)).</summary>
    public static ReadOnlyMemory<char> AsMemory(this string? text, int start, int length)
    {
        if (text == null)
        {
            if (start != 0 || length != 0)
                throw new ArgumentOutOfRangeException("start");
            return ReadOnlyMemory<char>.Empty;
        }
        if ((uint)start > (uint)text.Length || (uint)length > (uint)(text.Length - start))
            throw new ArgumentOutOfRangeException("length");
        return new ReadOnlyMemory<char>(text.ToCharArray(), start, length);
    }

    // === span-vs-span operations (ordinal) ===

    /// <summary>Ordinal equality of two char spans.</summary>
    public static bool Equals(this ReadOnlySpan<char> span, ReadOnlySpan<char> other)
    {
        if (span.Length != other.Length)
            return false;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] != other[i])
                return false;
        }
        return true;
    }

    /// <summary>Ordinal equality with a comparison hint (see file header).</summary>
    public static bool Equals(this ReadOnlySpan<char> span, ReadOnlySpan<char> other, StringComparison comparisonType)
    {
        return span.Equals(other);
    }

    /// <summary>Ordinal three-way comparison (-1/0/1).</summary>
    public static int CompareTo(this ReadOnlySpan<char> span, ReadOnlySpan<char> other)
    {
        int common = span.Length < other.Length ? span.Length : other.Length;
        for (int i = 0; i < common; i++)
        {
            char a = span[i];
            char b = other[i];
            if (a != b)
                return a < b ? -1 : 1;
        }
        if (span.Length == other.Length)
            return 0;
        return span.Length < other.Length ? -1 : 1;
    }

    /// <summary>Ordinal three-way comparison with a comparison hint.</summary>
    public static int CompareTo(this ReadOnlySpan<char> span, ReadOnlySpan<char> other, StringComparison comparisonType)
    {
        return span.CompareTo(other);
    }

    /// <summary>True when the span starts with the given value (ordinal).</summary>
    public static bool StartsWith(this ReadOnlySpan<char> span, ReadOnlySpan<char> value)
    {
        if (value.Length > span.Length)
            return false;
        for (int i = 0; i < value.Length; i++)
        {
            if (span[i] != value[i])
                return false;
        }
        return true;
    }

    /// <summary>StartsWith with a comparison hint (ordinal; see file header).</summary>
    public static bool StartsWith(this ReadOnlySpan<char> span, ReadOnlySpan<char> value, StringComparison comparisonType)
    {
        return span.StartsWith(value);
    }

    /// <summary>True when the span ends with the given value (ordinal).</summary>
    public static bool EndsWith(this ReadOnlySpan<char> span, ReadOnlySpan<char> value)
    {
        if (value.Length > span.Length)
            return false;
        int offset = span.Length - value.Length;
        for (int i = 0; i < value.Length; i++)
        {
            if (span[offset + i] != value[i])
                return false;
        }
        return true;
    }

    /// <summary>EndsWith with a comparison hint (ordinal; see file header).</summary>
    public static bool EndsWith(this ReadOnlySpan<char> span, ReadOnlySpan<char> value, StringComparison comparisonType)
    {
        return span.EndsWith(value);
    }

    /// <summary>First index of a char, or -1.</summary>
    public static int IndexOf(this ReadOnlySpan<char> span, char value)
    {
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == value)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// First index of a value in a span - the BCL's generic form
    /// (MemoryExtensions.IndexOf&lt;T&gt;(ReadOnlySpan&lt;T&gt;, T)).
    /// </summary>
    public static int IndexOf<T>(this ReadOnlySpan<T> span, T value) where T : IEquatable<T>
    {
        for (int i = 0; i < span.Length; i++)
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(span[i], value))
                return i;
        }
        return -1;
    }

    /// <summary>First index of a sub-span, or -1.</summary>
    public static int IndexOf(this ReadOnlySpan<char> span, ReadOnlySpan<char> value)
    {
        if (value.Length == 0)
            return 0;
        if (value.Length > span.Length)
            return -1;
        for (int i = 0; i <= span.Length - value.Length; i++)
        {
            bool found = true;
            for (int j = 0; j < value.Length; j++)
            {
                if (span[i + j] != value[j])
                {
                    found = false;
                    break;
                }
            }
            if (found)
                return i;
        }
        return -1;
    }

    /// <summary>Last index of a char, or -1.</summary>
    public static int LastIndexOf(this ReadOnlySpan<char> span, char value)
    {
        for (int i = span.Length - 1; i >= 0; i--)
        {
            if (span[i] == value)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Last index of a value in a span - the BCL's generic form
    /// (MemoryExtensions.LastIndexOf&lt;T&gt;(ReadOnlySpan&lt;T&gt;, T)).
    /// </summary>
    public static int LastIndexOf<T>(this ReadOnlySpan<T> span, T value) where T : IEquatable<T>
    {
        for (int i = span.Length - 1; i >= 0; i--)
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(span[i], value))
                return i;
        }
        return -1;
    }

    /// <summary>Last index of a sub-span, or -1.</summary>
    public static int LastIndexOf(this ReadOnlySpan<char> span, ReadOnlySpan<char> value)
    {
        if (value.Length == 0)
            return span.Length;
        if (value.Length > span.Length)
            return -1;
        for (int i = span.Length - value.Length; i >= 0; i--)
        {
            bool found = true;
            for (int j = 0; j < value.Length; j++)
            {
                if (span[i + j] != value[j])
                {
                    found = false;
                    break;
                }
            }
            if (found)
                return i;
        }
        return -1;
    }
}
