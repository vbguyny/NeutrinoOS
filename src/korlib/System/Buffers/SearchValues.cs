// NeutrinoOS korlib - System.Buffers.SearchValues
//
// Humanizer's generated regex runners call SearchValues.Create(ReadOnlySpan<char>)
// and then String/MemoryExtensions.IndexOfAny(span, SearchValues<char>). korlib
// has no vectorised character search, so SearchValues<char> is a plain sorted
// character set with a linear Contains.

namespace System.Buffers
{
    public static class SearchValues
    {
        public static SearchValues<char> Create(ReadOnlySpan<char> values)
        {
            var chars = new char[values.Length];
            int n = 0;
            for (int i = 0; i < values.Length; i++)
            {
                char c = values[i];
                bool dup = false;
                for (int j = 0; j < n; j++)
                {
                    if (chars[j] == c) { dup = true; break; }
                }
                if (!dup) chars[n++] = c;
            }
            var trimmed = new char[n];
            for (int i = 0; i < n; i++) trimmed[i] = chars[i];
            return new SearchValues<char>(trimmed);
        }

        public static SearchValues<byte> Create(ReadOnlySpan<byte> values)
        {
            var copy = new byte[values.Length];
            for (int i = 0; i < values.Length; i++) copy[i] = values[i];
            return new SearchValues<byte>(copy);
        }
    }

    public sealed class SearchValues<T>
    {
        private readonly T[] _values;

        internal SearchValues(T[] values) { _values = values; }

        public bool Contains(T value)
        {
            var cmp = System.Collections.Generic.EqualityComparer<T>.Default;
            for (int i = 0; i < _values.Length; i++)
            {
                if (cmp.Equals(_values[i], value)) return true;
            }
            return false;
        }
    }
}
