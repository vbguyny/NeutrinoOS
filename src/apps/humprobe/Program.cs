// humprobe - M8.1 Humanizer boundary probe (docs/NUGET.md).
//   H1: Humanizer.NumberToWordsExtension.ToWords(3231)
//   H2: "PascalCaseInputString".Humanize()
// The Humanizer.Core BCL surface (regex, culture, enum, builder) differs
// from the Tomlyn one; this app tracks that boundary on its own.
// Each section runs under try/catch so one failure does not mask the rest.
using System;
using Humanizer;

static class Probe
{
    static void Main(string[] args)
    {
        Console.WriteLine("humprobe start");
        // R: korlib surface probes. These reach the same types Humanizer uses
        // directly from this assembly, so they separate "korlib lacks the
        // type/member" from "Humanizer cannot see it through its assembly ref".
        Run("R1-regex", R1);
        Run("R2-pair", R2);
        Run("H1-to-words", H1);
        Run("H2-humanize", H2);
        Console.WriteLine("humprobe done");
    }

    static void R1()
    {
        var re = new System.Text.RegularExpressions.Regex(@"\p{Lu}?\p{Ll}+");
        Console.WriteLine("R1: ismatch=" + re.IsMatch("abc"));
        Console.WriteLine("R1: replace=" + re.Replace("a-b-c", "X"));
        Console.WriteLine("R1: matches=" + re.Matches("aa bb").Count);
        Console.WriteLine("R1: infinite=" + System.Text.RegularExpressions.Regex.InfiniteMatchTimeout.Ticks);
    }

    static void R2()
    {
        var kv = new System.Collections.Generic.KeyValuePair<string, int>("k", 7);
        Console.WriteLine("R2: key=" + kv.Key + " value=" + kv.Value);
    }

    static void Run(string name, Action section)
    {
        Console.WriteLine("== " + name);
        try
        {
            section();
            Console.WriteLine("== " + name + " OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine("== " + name + " EXC: " + ex.Message);
        }
    }

    static void H1()
    {
        Console.WriteLine("H1: words=" + Humanizer.NumberToWordsExtension.ToWords(3231));
    }

    static void H2()
    {
        Console.WriteLine("H2: humanized=" + "PascalCaseInputString".Humanize());
    }
}
