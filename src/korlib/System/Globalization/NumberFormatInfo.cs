// NeutrinoOS korlib - invariant NumberFormatInfo
//
// Humanizer's number-to-words converters read the separators and signs off
// CultureInfo.NumberFormat. The device exposes only the invariant culture, so
// these are the invariant values (.NET's InvariantInfo).

namespace System.Globalization
{
    public sealed class NumberFormatInfo
    {
        private static readonly NumberFormatInfo _invariant = new NumberFormatInfo();

        public static NumberFormatInfo InvariantInfo => _invariant;

        public static NumberFormatInfo CurrentInfo => _invariant;

        public NumberFormatInfo() { }

        public static NumberFormatInfo GetInstance(IFormatProvider? formatProvider)
        {
            if (formatProvider is CultureInfo culture && culture.NumberFormat != null)
                return culture.NumberFormat;
            return _invariant;
        }

        public string NumberDecimalSeparator { get; set; } = ".";
        public string NumberGroupSeparator { get; set; } = ",";
        public string PositiveSign { get; set; } = "+";
        public string NegativeSign { get; set; } = "-";
        public string DecimalSeparator => NumberDecimalSeparator;
        public string GroupSeparator => NumberGroupSeparator;
    }
}
