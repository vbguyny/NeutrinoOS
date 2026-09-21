// NeutrinoOS Phase 4 test app 8 - C# 14 features
//
// Exercises the C# 14 language features the Phase 4 audit tracks (see
// docs/PHASE4-JIT-COMPAT.md): field-backed properties (the `field`
// keyword), extension members, null-conditional assignment, simple
// lambda parameters with modifiers, partial properties, and unbound
// generics in nameof. Each feature reports ok/FAIL independently; exit
// code 0 when all pass.
//
// NOTE: implicit span conversions (`ReadOnlySpan<char> s = "abc"`) are
// NOT exercised here: korlib.dll (the IL BCL the JIT loads) does not yet
// ship Span<T>/ReadOnlySpan<T> - documented as a Phase 4 gap in
// docs/PHASE4-REPORT.md. The kernel AOT path does support spans.

using System;
using System.Collections.Generic;

namespace Phase4.CSharp14;

// ==================== field-backed property (C# 14) ====================

public class Counter
{
    private int _setCount;

    public int Value
    {
        get => field;
        set
        {
            field = value < 0 ? 0 : value; // `field` is the compiler-generated backing field
            _setCount++;
        }
    }

    public int SetCount => _setCount;
}

// ==================== partial property (C# 14) ====================

public partial class User
{
    public partial string Name { get; set; }
}

public partial class User
{
    public partial string Name
    {
        get => field;
        set => field = value == null ? "" : value;
    }
}

// ==================== extension members (C# 14) ====================

public static class StringExtensions
{
    extension(string s)
    {
        /// Extension property: vowel count.
        public int VowelCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u')
                        count++;
                }
                return count;
            }
        }

        /// Static extension member: repetitive string.
        public static string Repeat(string value, int times)
        {
            string result = "";
            for (int i = 0; i < times; i++)
                result += value;
            return result;
        }
    }
}

// ==================== simple lambda parameters with modifiers (C# 14) ====================

public delegate void RefIncrementer(ref int value);
public delegate bool RefPredicate<T>(ref T value);

public static class Program
{
    private static int _failures;

    public static unsafe int Main()
    {
        // -------- field-backed property --------
        try
        {
            Counter counter = new Counter();
            counter.Value = 42;
            counter.Value = -7; // clamps to 0
            Check("field-backed property", counter.Value == 0 && counter.SetCount == 2);
        }
        catch (Exception ex)
        {
            Fail("field-backed property", ex);
        }

        // -------- partial property --------
        try
        {
            User user = new User();
            user.Name = "Shane";
            Check("partial property get/set", user.Name == "Shane");
            user.Name = null;
            Check("partial property null-coalesce", user.Name == "");
        }
        catch (Exception ex)
        {
            Fail("partial property", ex);
        }

        // -------- extension members --------
        try
        {
            string word = "neutrino";
            Check("extension property", word.VowelCount == 4); // e,u,i,o
            Check("static extension member", StringExtensions.Repeat("ab", 3) == "ababab");
        }
        catch (Exception ex)
        {
            Fail("extension members", ex);
        }

        // -------- null-conditional assignment (C# 14) --------
        try
        {
            User? none = null;
            none?.Name = "ignored"; // must not throw

            User present = new User();
            present?.Name = "assigned";
            Check("null-conditional assignment", present.Name == "assigned");
        }
        catch (Exception ex)
        {
            Fail("null-conditional assignment", ex);
        }

        // -------- lambda parameters with modifiers, no explicit types --------
        try
        {
            RefIncrementer increment = (ref x) => x += 1;
            int value = 41;
            increment(ref value);
            Check("lambda (ref x)", value == 42);

            RefPredicate<int> positive = (ref x) => x > 0;
            int positiveValue = 5;
            Check("lambda (ref x) predicate", positive(ref positiveValue));
        }
        catch (Exception ex)
        {
            Fail("lambda parameter modifiers", ex);
        }

        // -------- unbound generic in nameof --------
        try
        {
            string name = nameof(List<>);
            Check("nameof(List<>)", name == "List");
        }
        catch (Exception ex)
        {
            Fail("nameof(List<>)", ex);
        }

        // -------- span conversions: not exercised (see file header) --------
        Console.WriteLine("[csharp14] note: implicit span conversions skipped (no Span<T> in korlib IL)");

        if (_failures == 0)
        {
            Console.WriteLine("[csharp14] PASS");
            return 0;
        }
        Console.WriteLine("[csharp14] FAIL " + _failures);
        return 1;
    }

    private static void Check(string name, bool ok)
    {
        if (ok)
        {
            Console.WriteLine("[csharp14] ok: " + name);
        }
        else
        {
            _failures++;
            Console.WriteLine("[csharp14] FAIL: " + name);
        }
    }

    private static void Fail(string name, Exception ex)
    {
        _failures++;
        Console.WriteLine("[csharp14] FAIL: " + name + " (" + ex.GetType().Name + ")");
    }
}
