// NeutrinoOS korlib - DateTimeStyles enum
// (plain flags enum; the device has no culture data, styles are accepted
// and ignored by the datetime parsers)

namespace System.Globalization;

/// <summary>
/// Defines the formatting options that customize string parsing for some
/// date and time parsing methods.
/// </summary>
public enum DateTimeStyles
{
    None = 0,
    AllowLeadingWhite = 1,
    AllowTrailingWhite = 2,
    AllowInnerWhite = 4,
    AllowWhiteSpaces = 7,
    NoCurrentDateDefault = 8,
    AdjustToUniversal = 16,
    AssumeLocal = 32,
    AssumeUniversal = 64,
    RoundtripKind = 128,
}
