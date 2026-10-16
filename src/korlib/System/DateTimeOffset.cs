// NeutrinoOS korlib - DateTimeOffset (minimal)
// A point in time together with its offset from UTC. Packages (Tomlyn's
// TOML datetime model) construct these from a DateTime + TimeSpan and read
// the DateTime/offset back out. TryParseExact handles the ISO/TOML subset
// the same way the kernel's DateTime.TryParseExact bridge does.

using System.Globalization;

namespace System;

/// <summary>
/// Represents a point in time, typically expressed as a date and time of day,
/// relative to Coordinated Universal Time (UTC).
/// </summary>
public readonly struct DateTimeOffset : IEquatable<DateTimeOffset>, IComparable<DateTimeOffset>, IComparable
{
    private readonly DateTime _dateTime;
    private readonly short _offsetMinutes;

    public static readonly DateTimeOffset MinValue = new DateTimeOffset(DateTime.MinValue, TimeSpan.Zero);
    public static readonly DateTimeOffset MaxValue = new DateTimeOffset(DateTime.MaxValue, TimeSpan.Zero);

    /// <summary>Initializes a DateTimeOffset using the given DateTime (offset zero).</summary>
    public DateTimeOffset(DateTime dateTime)
    {
        _dateTime = dateTime;
        _offsetMinutes = 0;
    }

    /// <summary>Initializes a DateTimeOffset from a DateTime and its UTC offset.</summary>
    public DateTimeOffset(DateTime dateTime, TimeSpan offset)
    {
        _dateTime = dateTime;
        _offsetMinutes = (short)offset.TotalMinutes;
    }

    public DateTime DateTime => _dateTime;
    public TimeSpan Offset => TimeSpan.FromMinutes(_offsetMinutes);
    public TimeSpan TimeOfDay => _dateTime.TimeOfDay;
    public long Ticks => _dateTime.Ticks - (long)_offsetMinutes * TimeSpan.TicksPerMinute;

    public DateTime UtcDateTime =>
        new DateTime(_dateTime.Ticks - (long)_offsetMinutes * TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    public DateTime LocalDateTime => UtcDateTime.ToLocalTime();

    public static implicit operator DateTimeOffset(DateTime dateTime) => new DateTimeOffset(dateTime);

    public DateTimeOffset ToUniversalTime() => new DateTimeOffset(UtcDateTime, TimeSpan.Zero);

    public DateTimeOffset ToOffset(TimeSpan offset) =>
        new DateTimeOffset(new DateTime(UtcDateTime.Ticks + (long)offset.TotalMinutes * TimeSpan.TicksPerMinute), offset);

    private static bool IsDigitChar(char c) => c >= '0' && c <= '9';

    private static int ParseNDigits(string s, int pos, int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
        {
            if (pos + i >= s.Length || !IsDigitChar(s[pos + i]))
                return -1;
            value = value * 10 + (s[pos + i] - '0');
        }
        return value;
    }

    /// <summary>
    /// Parses the ISO/TOML datetime subset (date-only, time-only, combined
    /// forms with optional fractional seconds and Z/±HH:MM offsets). The
    /// format string is a hint; unsupported inputs return false.
    /// </summary>
    public static bool TryParseExact(string? input, string? format, IFormatProvider? provider,
                                     DateTimeStyles styles, out DateTimeOffset result)
    {
        result = default;
        if (input == null || input.Length < 5)
            return false;

        string s = input;
        int year = 0, month = 0, day = 0, hour = 0, minute = 0, second = 0, millis = 0;
        bool hasDate = false, hasTime = false;
        var kind = DateTimeKind.Unspecified;
        int offsetMinutes = 0;
        int p = 0;

        if (s.Length >= 10 && IsDigitChar(s[0]) && IsDigitChar(s[1]) && IsDigitChar(s[2]) &&
            IsDigitChar(s[3]) && s[4] == '-')
        {
            year = ParseNDigits(s, 0, 4);
            month = ParseNDigits(s, 5, 2);
            day = ParseNDigits(s, 8, 2);
            if (year < 0 || month < 1 || month > 12 || day < 1 || day > 31)
                return false;
            hasDate = true;
            p = 10;
            if (p < s.Length && (s[p] == 'T' || s[p] == 't' || s[p] == ' '))
                p++;
        }

        if (p + 8 <= s.Length && IsDigitChar(s[p]) && IsDigitChar(s[p + 1]) && s[p + 2] == ':')
        {
            hour = ParseNDigits(s, p, 2);
            minute = ParseNDigits(s, p + 3, 2);
            second = ParseNDigits(s, p + 6, 2);
            if (hour > 23 || minute > 59 || second > 60)
                return false;
            hasTime = true;
            p += 8;

            if (p < s.Length && s[p] == '.')
            {
                p++;
                int ms = 0, digits = 0;
                while (p < s.Length && IsDigitChar(s[p]) && digits < 3)
                {
                    ms = ms * 10 + (s[p] - '0');
                    p++;
                    digits++;
                }
                while (p < s.Length && IsDigitChar(s[p]))
                    p++;
                while (digits < 3)
                {
                    ms *= 10;
                    digits++;
                }
                millis = ms;
            }

            if (p < s.Length && (s[p] == 'Z' || s[p] == 'z'))
            {
                kind = DateTimeKind.Utc;
                p++;
            }
            else if (p < s.Length && (s[p] == '+' || s[p] == '-'))
            {
                bool negative = s[p] == '-';
                int offH = ParseNDigits(s, p + 1, 2);
                int offM = ParseNDigits(s, p + 4, 2);
                if (offH < 0 || offM < 0)
                    return false;
                offsetMinutes = offH * 60 + offM;
                if (negative)
                    offsetMinutes = -offsetMinutes;
                p += 6;
            }
        }

        if (p != s.Length || (!hasDate && !hasTime))
            return false;

        DateTime dt = hasDate
            ? new DateTime(year, month, day, hour, minute, second, millis, kind)
            : new DateTime(1, 1, 1, hour, minute, second, millis, kind);

        result = new DateTimeOffset(dt, TimeSpan.FromMinutes(offsetMinutes));
        return true;
    }

    public override string ToString() => _dateTime.ToString();
    public string ToString(string? format, IFormatProvider? provider) => _dateTime.ToString();

    public bool Equals(DateTimeOffset other) => UtcDateTime.Ticks == other.UtcDateTime.Ticks;
    public override bool Equals(object? obj) => obj is DateTimeOffset other && Equals(other);
    public override int GetHashCode() => (int)UtcDateTime.Ticks;

    public int CompareTo(DateTimeOffset other) => UtcDateTime.Ticks.CompareTo(other.UtcDateTime.Ticks);

    public int CompareTo(object? obj)
    {
        if (obj == null)
            return 1;
        if (obj is DateTimeOffset other)
            return CompareTo(other);
        throw new ArgumentException("Object must be a DateTimeOffset");
    }

    public static bool operator ==(DateTimeOffset left, DateTimeOffset right) => left.Equals(right);
    public static bool operator !=(DateTimeOffset left, DateTimeOffset right) => !left.Equals(right);
    public static bool operator <(DateTimeOffset left, DateTimeOffset right) => left.UtcDateTime.Ticks < right.UtcDateTime.Ticks;
    public static bool operator <=(DateTimeOffset left, DateTimeOffset right) => left.UtcDateTime.Ticks <= right.UtcDateTime.Ticks;
    public static bool operator >(DateTimeOffset left, DateTimeOffset right) => left.UtcDateTime.Ticks > right.UtcDateTime.Ticks;
    public static bool operator >=(DateTimeOffset left, DateTimeOffset right) => left.UtcDateTime.Ticks >= right.UtcDateTime.Ticks;
}
