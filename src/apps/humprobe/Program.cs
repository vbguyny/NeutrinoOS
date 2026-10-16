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
        Run("H1-to-words", H1);
        Run("H2-humanize", H2);
        Console.WriteLine("humprobe done");
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
