// NeutrinoOS Phase 5 utility: uptime - print system uptime
//
// usage: uptime
//   Reads the kernel tick counter through the DDK timer export and prints
//   "up HH:MM:SS (N seconds)".

using System;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Kernel;

namespace NeutrinoOS.Utility.Uptime;

/// <summary>The uptime utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; always returns 0.</summary>
    public static int Main(string[] args)
    {
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: uptime",
                "  Print how long NeutrinoOS has been running.");
        }
        if (args.Length > 0)
            return Util.Fail("uptime", "usage: uptime");

        ulong totalSeconds = Timer.GetUptimeSeconds();
        ulong hours = totalSeconds / 3600;
        ulong minutes = (totalSeconds % 3600) / 60;
        ulong seconds = totalSeconds % 60;

        var sb = new System.Text.StringBuilder();
        sb.Append("up ");
        Append2(sb, hours);
        sb.Append(':');
        Append2(sb, minutes);
        sb.Append(':');
        Append2(sb, seconds);
        sb.Append(" (");
        sb.Append(totalSeconds.ToString());
        sb.Append(" seconds)");
        Console.WriteLine(sb.ToString());
        return 0;
    }

    private static void Append2(System.Text.StringBuilder sb, ulong value)
    {
        if (value < 10)
            sb.Append('0');
        sb.Append(value);
    }
}
