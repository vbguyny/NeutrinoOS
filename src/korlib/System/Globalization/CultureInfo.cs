// NeutrinoOS korlib - CultureInfo stub
// Minimal stub for culture info - required by reflection APIs.

namespace System.Globalization
{
    /// <summary>
    /// Provides information about a specific culture.
    /// This is a minimal stub for reflection support.
    /// </summary>
    public class CultureInfo
    {
        private static readonly CultureInfo _invariantCulture = new CultureInfo("", false);
        private static readonly CultureInfo _currentCulture = _invariantCulture;
        private static readonly CultureInfo _currentUICulture = _invariantCulture;

        private readonly string _name;
        private readonly bool _useUserOverride;
        private NumberFormatInfo? _numberFormat;
        private CompareInfo? _compareInfo;
        private TextInfo? _textInfo;

        public CultureInfo(string name)
            : this(name, true)
        {
        }

        public CultureInfo(string name, bool useUserOverride)
        {
            _name = name ?? string.Empty;
            _useUserOverride = useUserOverride;
        }

        public static CultureInfo InvariantCulture => _invariantCulture;
        public static CultureInfo CurrentCulture => _currentCulture;
        public static CultureInfo CurrentUICulture => _currentUICulture;

        public string Name => _name;
        public bool UseUserOverride => _useUserOverride;

        /// <summary>
        /// Parent culture: the name with its last '-suffix' removed ("en-US" ->
        /// "en"); the invariant culture is its own parent. Humanizer's
        /// LocaliserRegistry walks parents looking for a registered converter.
        /// </summary>
        public CultureInfo Parent
        {
            get
            {
                if (_name.Length == 0) return _invariantCulture;
                int dash = _name.LastIndexOf('-');
                if (dash <= 0) return _invariantCulture;
                return new CultureInfo(_name.Substring(0, dash), false);
            }
        }

        /// <summary>
        /// ISO 639-1 code of the language part ("en-US" -> "en", "de" -> "de").
        /// The invariant culture reports "iv", like .NET.
        /// </summary>
        public string TwoLetterISOLanguageName
        {
            get
            {
                if (_name.Length == 0) return "iv";
                int dash = _name.IndexOf('-');
                string lang = dash < 0 ? _name : _name.Substring(0, dash);
                return lang.Length <= 2 ? lang : lang.Substring(0, 2);
            }
        }

        /// <summary>ISO 639-2 code; korlib only knows the two-letter form.</summary>
        public string ThreeLetterISOLanguageName => TwoLetterISOLanguageName;

        /// <summary>Humanizer resolves converters by this name.</summary>
        public string EnglishName => _name.Length == 0 ? "Invariant Language (Invariant Country)" : _name;

        public string DisplayName => EnglishName;

        public string NativeName => EnglishName;

        public NumberFormatInfo NumberFormat => _numberFormat ??= new NumberFormatInfo();

        public CompareInfo CompareInfo => _compareInfo ??= CompareInfo.Invariant;

        public TextInfo TextInfo => _textInfo ??= TextInfo.Invariant;

        public bool IsNeutralCulture => _name.Length == 0 || _name.IndexOf('-') < 0;

        public override string ToString() => _name;
    }
}
