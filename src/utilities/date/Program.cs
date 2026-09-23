// NeutrinoOS Phase 5 utility: date - print the current date and time
//
// usage: date
//   Reads the CMOS RTC through the kernel export (Kernel_GetWallClock),
//   formatted as "YYYY-MM-DD HH:MM:SS".

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Kernel;

namespace NeutrinoOS.Utility.Date;

/// <summary>The date utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 when no RTC is available.</summary>
    public static int Main(string[] args)
    {
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: date",
                "  Print the current date and time from the CMOS RTC",
                "  (YYYY-MM-DD HH:MM:SS).");
        }
        if (args.Length > 0)
            return Util.Fail("date", "usage: date");

        if (!SysInfo.TryGetWallClock(out int year, out int month, out int day,
                out int hour, out int minute, out int second))
        {
            return Util.Fail("date", "no real-time clock available");
        }

        var sb = new System.Text.StringBuilder();
        Append4(sb, year);
        sb.Append('-');
        Append2(sb, month);
        sb.Append('-');
        Append2(sb, day);
        sb.Append(' ');
        Append2(sb, hour);
        sb.Append(':');
        Append2(sb, minute);
        sb.Append(':');
        Append2(sb, second);
        Console.WriteLine(sb.ToString());
        return 0;
    }

    private static void Append2(System.Text.StringBuilder sb, int value)
    {
        if (value < 10)
            sb.Append('0');
        sb.Append(value);
    }

    private static void Append4(System.Text.StringBuilder sb, int value)
    {
        if (value < 1000)
            sb.Append('0');
        if (value < 100)
            sb.Append('0');
        if (value < 10)
            sb.Append('0');
        sb.Append(value);
    }
}
