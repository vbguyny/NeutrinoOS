// NeutrinoOS korlib - DateOnly / TimeOnly
//
// Humanizer's date/time converter registries reference these types at static
// init (Configurator..cctor builds all the registries), so they must exist even
// when only number-to-words is used. They are thin wrappers: DateOnly over a
// DateTime truncated to midnight, TimeOnly over a tick count within the day.

namespace System
{
    public readonly struct DateOnly : IEquatable<DateOnly>, IComparable<DateOnly>
    {
        private const long TicksPerDayLocal = 864000000000L;

        private readonly DateTime _date;

        private DateOnly(DateTime date) { _date = date.Date; }

        public DateOnly(int year, int month, int day)
        {
            _date = new DateTime(year, month, day);
        }

        public static DateOnly FromDateTime(DateTime dateTime) => new DateOnly(dateTime);

        public DateTime ToDateTime(TimeOnly time) => _date + time.ToTimeSpan();

        public DateTime ToDateTime(TimeOnly time, DateTimeKind kind) => _date + time.ToTimeSpan();

        public static DateOnly MinValue => new DateOnly(DateTime.MinValue);
        public static DateOnly MaxValue => new DateOnly(DateTime.MaxValue);

        public int Year => _date.Year;
        public int Month => _date.Month;
        public int Day => _date.Day;
        public int DayOfYear => _date.DayOfYear;
        public DayOfWeek DayOfWeek => _date.DayOfWeek;

        /// <summary>Days since 0001-01-01, matching .NET's DateOnly.DayNumber.</summary>
        public int DayNumber => (int)(_date.Ticks / TicksPerDayLocal);

        public static DateOnly FromDayNumber(int dayNumber) => new DateOnly(new DateTime((long)dayNumber * TicksPerDayLocal));

        public DateOnly AddDays(int value) => new DateOnly(_date.AddDays(value));

        public DateOnly AddMonths(int value) => new DateOnly(_date.AddMonths(value));

        public DateOnly AddYears(int value) => new DateOnly(_date.AddYears(value));

        public static bool operator ==(DateOnly a, DateOnly b) => a._date.Ticks == b._date.Ticks;
        public static bool operator !=(DateOnly a, DateOnly b) => a._date.Ticks != b._date.Ticks;
        public static bool operator >(DateOnly a, DateOnly b) => a._date.Ticks > b._date.Ticks;
        public static bool operator >=(DateOnly a, DateOnly b) => a._date.Ticks >= b._date.Ticks;
        public static bool operator <(DateOnly a, DateOnly b) => a._date.Ticks < b._date.Ticks;
        public static bool operator <=(DateOnly a, DateOnly b) => a._date.Ticks <= b._date.Ticks;

        public bool Equals(DateOnly other) => _date.Ticks == other._date.Ticks;
        public override bool Equals(object? obj) => obj is DateOnly d && Equals(d);
        public override int GetHashCode() => _date.GetHashCode();
        public int CompareTo(DateOnly other) => _date.Ticks.CompareTo(other._date.Ticks);

        public override string ToString() => _date.ToString();
    }

    public readonly struct TimeOnly : IEquatable<TimeOnly>, IComparable<TimeOnly>
    {
        private const long TicksPerHour = 36000000000L;
        private const long TicksPerMinute = 600000000L;
        private const long TicksPerSecond = 10000000L;
        private const long TicksPerDay = 864000000000L;

        private readonly long _ticks;

        public TimeOnly(long ticks) { _ticks = ticks % TicksPerDay; }

        public TimeOnly(int hour, int minute)
            : this(hour * TicksPerHour + minute * TicksPerMinute) { }

        public TimeOnly(int hour, int minute, int second)
            : this(hour * TicksPerHour + minute * TicksPerMinute + second * TicksPerSecond) { }

        public TimeOnly(int hour, int minute, int second, int millisecond)
            : this(hour * TicksPerHour + minute * TicksPerMinute + second * TicksPerSecond + millisecond * 10000L) { }

        public static TimeOnly FromDateTime(DateTime dateTime) => new TimeOnly(dateTime.Ticks % TicksPerDay);

        public static TimeOnly MinValue => new TimeOnly(0);
        public static TimeOnly MaxValue => new TimeOnly(TicksPerDay - 1);

        /// <summary>Ticks since midnight.</summary>
        public long Ticks => _ticks;

        public int Hour => (int)(_ticks / TicksPerHour);
        public int Minute => (int)(_ticks % TicksPerHour / TicksPerMinute);
        public int Second => (int)(_ticks % TicksPerMinute / TicksPerSecond);
        public int Millisecond => (int)(_ticks % TicksPerSecond / 10000);

        public TimeSpan ToTimeSpan() => new TimeSpan(_ticks);

        public TimeOnly AddHours(double value) => Add(TimeSpan.FromHours(value));
        public TimeOnly AddMinutes(double value) => Add(TimeSpan.FromMinutes(value));

        public TimeOnly Add(TimeSpan timeSpan)
        {
            long t = (_ticks + timeSpan.Ticks) % TicksPerDay;
            if (t < 0) t += TicksPerDay;
            return new TimeOnly(t);
        }

        public static bool operator ==(TimeOnly a, TimeOnly b) => a._ticks == b._ticks;
        public static bool operator !=(TimeOnly a, TimeOnly b) => a._ticks != b._ticks;
        public static bool operator >(TimeOnly a, TimeOnly b) => a._ticks > b._ticks;
        public static bool operator >=(TimeOnly a, TimeOnly b) => a._ticks >= b._ticks;
        public static bool operator <(TimeOnly a, TimeOnly b) => a._ticks < b._ticks;
        public static bool operator <=(TimeOnly a, TimeOnly b) => a._ticks <= b._ticks;

        public bool Equals(TimeOnly other) => _ticks == other._ticks;
        public override bool Equals(object? obj) => obj is TimeOnly t && Equals(t);
        public override int GetHashCode() => _ticks.GetHashCode();
        public int CompareTo(TimeOnly other) => _ticks.CompareTo(other._ticks);

        public override string ToString()
        {
            var sb = new Text.StringBuilder();
            if (Hour < 10) sb.Append('0');
            sb.Append(Hour.ToString());
            sb.Append(':');
            if (Minute < 10) sb.Append('0');
            sb.Append(Minute.ToString());
            return sb.ToString();
        }
    }
}
