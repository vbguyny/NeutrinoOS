// NeutrinoOS Phase 4 test app 4 - Collections and LINQ
//
// Exercises List<T>, Dictionary<K,V>, HashSet<T>, Queue/Stack and the
// korlib System.Linq operators (Where, Select, SelectMany, OrderBy,
// ThenBy, GroupBy, Join, Sum, Min, Max, Average, Distinct, Take, Skip,
// Concat, Any/All/Count, First/FirstOrDefault, ToList/ToArray).
// Exit code 0 when every check passed.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Phase4.LinqApp;

public static class Program
{
    private static int _failures;

    public static int Main()
    {
        // ==================== List<T> + LINQ ====================
        List<int> numbers = new List<int>();
        numbers.Add(5);
        numbers.Add(3);
        numbers.Add(9);
        numbers.Add(1);
        numbers.Add(3);
        numbers.Add(7);
        numbers.Add(2);
        numbers.Add(8);
        numbers.Add(4);
        numbers.Add(6);

        Check("List.Count", numbers.Count == 10);

        int whereSum = numbers.Where(n => n > 3).Sum();
        Check("Where + Sum", whereSum == 39); // 5+9+7+8+4+6

        int[] sorted = numbers.OrderBy(n => n).ToArray();
        Check("OrderBy", sorted[0] == 1 && sorted[9] == 9 && sorted[4] == 4);

        int[] descending = numbers.OrderByDescending(n => n).Take(3).ToArray();
        Check("OrderByDescending + Take", descending[0] == 9 && descending[2] == 7);

        List<int> evens = numbers.Where(n => n % 2 == 0).ToList();
        Check("Where -> ToList", evens.Count == 4 && evens[0] == 2);

        Check("First", numbers.First() == 5);
        Check("FirstOrDefault (predicate)", numbers.FirstOrDefault(n => n > 100) == 0);
        Check("Any", numbers.Any());
        Check("Any (predicate)", numbers.Any(n => n == 9));
        Check("All", numbers.All(n => n > 0));
        Check("Count (predicate)", numbers.Count(n => n >= 5) == 5);
        Check("Min/Max", numbers.Min() == 1 && numbers.Max() == 9);
        Check("Average", numbers.Average() == 4.8);
        Check("Distinct", numbers.Distinct().Count() == 9);
        Check("Skip", numbers.Skip(1).First() == 3);
        Check("Contains", numbers.Contains(7) && !numbers.Contains(42));
        Check("Concat", numbers.Concat(new int[] { 100, 200 }).Count() == 12);

        // Select + SelectMany
        List<string> labels = numbers.Select(n => "n" + n.ToString()).ToList();
        Check("Select", labels.Count == 10 && labels[0] == "n5");

        int[][] jagged = new int[][] { new int[] { 1, 2 }, new int[] { 3, 4, 5 } };
        int selectManyCount = jagged.SelectMany(a => a).Count();
        Check("SelectMany", selectManyCount == 5);
        Check("SelectMany sum", jagged.SelectMany(a => a).Sum() == 15);

        // ==================== Ordering with secondary keys ====================
        List<string> words = new List<string>();
        words.Add("delta");
        words.Add("alpha");
        words.Add("charlie");
        words.Add("bravo");
        words.Add("alps");
        words.Add("dome");

        List<string> byLengthThenAlpha = words.OrderBy(w => w.Length).ThenBy(w => w).ToList();
        Check("OrderBy + ThenBy", byLengthThenAlpha[0] == "alps" && byLengthThenAlpha[1] == "dome" &&
                                  byLengthThenAlpha[2] == "alpha" && byLengthThenAlpha[4] == "delta" && byLengthThenAlpha[5] == "charlie");

        // ==================== GroupBy ====================
        int groups = 0;
        int fiveLetterGroups = 0;
        foreach (IGrouping<int, string> group in words.GroupBy(w => w.Length))
        {
            groups++;
            if (group.Key == 5)
                fiveLetterGroups = group.Count();
        }
        Check("GroupBy (3 groups by length)", groups == 3 && fiveLetterGroups == 3);
        Check("GroupBy first key", words.GroupBy(w => w.Length).First().Key == 5);

        // ==================== Join ====================
        List<string> keys = new List<string>();
        keys.Add("alpha");
        keys.Add("delta");
        keys.Add("missing");

        var joined = words.Join(keys, w => w, k => k, (w, k) => w + ":" + k).ToList();
        Check("Join", joined.Count == 2 && joined[0] == "delta:delta" && joined[1] == "alpha:alpha");

        // ==================== Dictionary<K,V> ====================
        Dictionary<string, int> counts = new Dictionary<string, int>();
        counts["apple"] = 3;
        counts.Add("banana", 5);
        counts["cherry"] = 7;
        int value;
        bool found = counts.TryGetValue("banana", out value);
        Check("Dictionary.TryGetValue", found && value == 5);
        Check("Dictionary.Count", counts.Count == 3);
        Check("Dictionary indexer", counts["cherry"] == 7);
        Check("Dictionary ContainsKey", counts.ContainsKey("apple") && !counts.ContainsKey("fig"));

        int dictTotal = 0;
        foreach (KeyValuePair<string, int> kv in counts)
            dictTotal += kv.Value;
        Check("Dictionary enumerate", dictTotal == 15);

        // ==================== HashSet / Queue / Stack ====================
        HashSet<int> set = new HashSet<int>();
        set.Add(1);
        set.Add(2);
        set.Add(1);
        Check("HashSet dedupes", set.Count == 2 && set.Contains(2));

        Queue<int> queue = new Queue<int>();
        queue.Enqueue(10);
        queue.Enqueue(20);
        Check("Queue FIFO", queue.Dequeue() == 10 && queue.Count == 1);

        Stack<int> stack = new Stack<int>();
        stack.Push(1);
        stack.Push(2);
        Check("Stack LIFO", stack.Pop() == 2 && stack.Count == 1);

        // ==================== String helpers used by LINQ apps ====================
        string joinedText = string.Join(",", new string[] { "a", "b", "c" });
        Check("string.Join", joinedText == "a,b,c");

        string[] split = "x,y,z".Split(',');
        Check("string.Split", split.Length == 3 && split[2] == "z");

        if (_failures == 0)
        {
            Console.WriteLine("[linq] PASS");
            return 0;
        }
        Console.WriteLine("[linq] FAIL " + _failures);
        return 1;
    }

    private static void Check(string name, bool ok)
    {
        if (ok)
        {
            Console.WriteLine("[linq] ok: " + name);
        }
        else
        {
            _failures++;
            Console.WriteLine("[linq] FAIL: " + name);
        }
    }
}
