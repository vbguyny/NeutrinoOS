// NeutrinoOS korlib - invariant CompareInfo / TextInfo
//
// Packages call CultureInfo.CompareInfo and CultureInfo.TextInfo while
// normalising text (Humanizer's Humanize/Pascalize paths). The device has no
// culture data, so both are ordinal/invariant implementations.

namespace System.Globalization
{
    [Flags]
    public enum CompareOptions
    {
        None = 0,
        IgnoreCase = 1,
        IgnoreNonSpace = 2,
        IgnoreSymbols = 4,
        IgnoreKanaType = 8,
        IgnoreWidth = 16,
        Ordinal = 0x40000000,
        OrdinalIgnoreCase = 0x10000000,
    }

    public sealed class CompareInfo
    {
        private static readonly CompareInfo _invariant = new CompareInfo();

        internal static CompareInfo Invariant => _invariant;

        public string Name => "";

        public int Compare(string? string1, string? string2)
            => Compare(string1, string2, CompareOptions.None);

        public int Compare(string? string1, string? string2, CompareOptions options)
        {
            if (ReferenceEquals(string1, string2)) return 0;
            if (string1 == null) return -1;
            if (string2 == null) return 1;

            bool ignoreCase = (options & (CompareOptions.IgnoreCase | CompareOptions.OrdinalIgnoreCase)) != 0;
            int n = string1.Length < string2.Length ? string1.Length : string2.Length;
            for (int i = 0; i < n; i++)
            {
                char a = string1[i];
                char b = string2[i];
                if (ignoreCase) { a = Char.ToLower(a); b = Char.ToLower(b); }
                if (a != b) return a < b ? -1 : 1;
            }
            return string1.Length == string2.Length ? 0 : (string1.Length < string2.Length ? -1 : 1);
        }

        public int IndexOf(string source, string value)
            => IndexOf(source, value, CompareOptions.None);

        public int IndexOf(string source, string value, CompareOptions options)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (value.Length == 0) return 0;

            bool ignoreCase = (options & (CompareOptions.IgnoreCase | CompareOptions.OrdinalIgnoreCase)) != 0;
            for (int i = 0; i + value.Length <= source.Length; i++)
            {
                int j = 0;
                for (; j < value.Length; j++)
                {
                    char a = source[i + j];
                    char b = value[j];
                    if (ignoreCase) { a = Char.ToLower(a); b = Char.ToLower(b); }
                    if (a != b) break;
                }
                if (j == value.Length) return i;
            }
            return -1;
        }

        public bool IsPrefix(string source, string prefix) => IsPrefix(source, prefix, CompareOptions.None);

        public bool IsPrefix(string source, string prefix, CompareOptions options)
        {
            if (source == null || prefix == null) return false;
            if (prefix.Length > source.Length) return false;
            return Compare(source.Substring(0, prefix.Length), prefix, options) == 0;
        }

        public bool IsSuffix(string source, string suffix) => IsSuffix(source, suffix, CompareOptions.None);

        public bool IsSuffix(string source, string suffix, CompareOptions options)
        {
            if (source == null || suffix == null) return false;
            if (suffix.Length > source.Length) return false;
            return Compare(source.Substring(source.Length - suffix.Length), suffix, options) == 0;
        }
    }

    public sealed class TextInfo
    {
        private static readonly TextInfo _invariant = new TextInfo();

        internal static TextInfo Invariant => _invariant;

        public string ToUpper(string str)
        {
            if (str == null) throw new ArgumentNullException(nameof(str));
            var chars = new char[str.Length];
            for (int i = 0; i < str.Length; i++) chars[i] = Char.ToUpper(str[i]);
            return new string(chars);
        }

        public string ToLower(string str)
        {
            if (str == null) throw new ArgumentNullException(nameof(str));
            var chars = new char[str.Length];
            for (int i = 0; i < str.Length; i++) chars[i] = Char.ToLower(str[i]);
            return new string(chars);
        }

        public char ToUpper(char c) => Char.ToUpper(c);

        public char ToLower(char c) => Char.ToLower(c);

        public bool IsRightToLeft => false;
    }
}
