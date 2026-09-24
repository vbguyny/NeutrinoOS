// NeutrinoOS Phase 5 utility: sleep - suspend for N seconds
//
// usage: sleep N
//   N is a whole number of seconds (also accepts Ns / Nms suffixes for
//   convenience). Uses the korlib Thread.Sleep kernel bridge.

using System;
using System.Threading;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Sleep;

/// <summary>The sleep utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 for invalid arguments.</summary>
    public static int Main(string[] args)
    {
        if (ProtonOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            return Util.Help(
                "usage: sleep N",
                "  Suspend for N seconds; also accepts Ns and Nms suffixes.");
        }

        if (args.Length != 1)
            return Util.Fail("sleep", "usage: sleep N");

        string arg = args[0];
        int milliseconds;

        if (arg.EndsWith("ms") && arg.Length > 2)
        {
            if (!Util.TryParseInt(arg.Substring(0, arg.Length - 2), out milliseconds))
                return Util.Fail("sleep", arg + ": invalid time");
        }
        else if (arg.EndsWith("s") && arg.Length > 1)
        {
            if (!Util.TryParseInt(arg.Substring(0, arg.Length - 1), out int seconds))
                return Util.Fail("sleep", arg + ": invalid time");
            milliseconds = seconds * 1000;
        }
        else
        {
            if (!Util.TryParseInt(arg, out int seconds))
                return Util.Fail("sleep", arg + ": invalid time");
            milliseconds = seconds * 1000;
        }

        if (milliseconds < 0)
            return Util.Fail("sleep", "invalid time");

        if (milliseconds > 0)
        {
            // Sleep in 100 ms slices so very long sleeps stay responsive to
            // the kernel's timer and the value does not overflow the API.
            while (milliseconds > 0)
            {
                int slice = milliseconds > 100 ? 100 : milliseconds;
                Thread.Sleep(slice);
                milliseconds -= slice;
            }
        }
        return 0;
    }
}
