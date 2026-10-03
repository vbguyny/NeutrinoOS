// NeutrinoOS Phase 5 utility: seq - print a sequence of numbers
//
// usage: seq last
//        seq first last
//        seq first increment last
//
// Prints one integer per line from first (default 1) toward last with
// the given increment (default 1; negative increments count down).

using System;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Seq;

/// <summary>The seq utility (see file header).</summary>
public static class Program
{
    /// <summary>Entry point; returns 1 on bad arguments.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;

        var operands = new System.Collections.Generic.List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--help" || a == "-h")
            {
                return Util.Help(
                    "usage: seq last",
                    "       seq first last",
                    "       seq first increment last",
                    "  Print integers from first (1) toward last, one per line.",
                    "  The increment may be negative to count down.");
            }
            if (a.Length > 1 && a[0] == '-' && !IsNumberLike(a))
                return Util.Fail("seq", a + ": unknown option");
            operands.Add(a);
        }

        if (operands.Count < 1 || operands.Count > 3)
            return Util.Fail("seq", "usage: seq [first [increment]] last");

        int first = 1;
        int increment = 1;
        int last;
        if (operands.Count == 1)
        {
            if (!Util.TryParseInt(operands[0], out last))
                return Util.Fail("seq", operands[0] + ": invalid number");
        }
        else if (operands.Count == 2)
        {
            if (!Util.TryParseInt(operands[0], out first) ||
                !Util.TryParseInt(operands[1], out last))
                return Util.Fail("seq", "invalid number");
        }
        else
        {
            if (!Util.TryParseInt(operands[0], out first) ||
                !Util.TryParseInt(operands[1], out increment) ||
                !Util.TryParseInt(operands[2], out last))
                return Util.Fail("seq", "invalid number");
        }

        if (increment == 0)
            return Util.Fail("seq", "increment must not be zero");

        if (increment > 0)
        {
            for (long v = first; v <= last; v += increment)
                Console.WriteLine(v.ToString());
        }
        else
        {
            for (long v = first; v >= last; v += increment)
                Console.WriteLine(v.ToString());
        }
        return 0;
    }

    /// <summary>True when the argument looks like a (possibly negative) number.</summary>
    private static bool IsNumberLike(string s)
    {
        for (int i = 1; i < s.Length; i++)
        {
            if (s[i] < '0' || s[i] > '9')
                return false;
        }
        return s.Length > 1;
    }
}
