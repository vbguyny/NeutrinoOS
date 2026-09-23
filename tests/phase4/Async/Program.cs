// NeutrinoOS Phase 4 test app 5 - Async/await
//
// Exercises the korlib System.Threading.Tasks implementation: Task.Run,
// Task<T>.Result, Task.Wait, Task.Delay, and an async method with await.
//
// LIMITATION (documented in docs/PHASE4-DESIGN.md): korlib's Task is a
// minimal SYNCHRONOUS implementation - Task.Run executes inline on the
// calling thread, Task.Delay blocks, and awaits complete synchronously.
// The async/await state machine still exercises the compiler-generated
// MoveNext pattern through the Tier-0 JIT, which is the point of the test.

using System;
using System.Threading.Tasks;

namespace Phase4.AsyncApp;

public static class Program
{
    private static int _failures;

    public static int Main()
    {
        // -------- Task.Run with a result --------
        Task<int> doubled = Task.Run(() => 21 * 2);
        int result = doubled.Result;
        Check("Task.Run + Result", result == 42);

        // -------- Task.Wait --------
        bool ran = false;
        Task t = Task.Run(() => { ran = true; });
        t.Wait();
        Check("Task.Run + Wait", ran);

        // -------- async method with await Task.Delay --------
        int awaited = DelayAndDouble(21).Result;
        Check("await Task.Delay", awaited == 42);

        // -------- async chain (two awaits) --------
        int chained = AddThenScale(10).Result;
        if (chained != 30)
            Console.WriteLine("[async] chained returned " + chained);
        Check("chained awaits", chained == 30);

        // -------- FromResult --------
        Check("Task.FromResult", Task.FromResult(7).Result == 7);

        // -------- async void-style completion via Wait --------
        bool completed = false;
        RunCompletionFlag(() => { completed = true; }).Wait();
        Check("async completion", completed);

        if (_failures == 0)
        {
            Console.WriteLine("[async] PASS");
            return 0;
        }
        Console.WriteLine("[async] FAIL " + _failures);
        return 1;
    }

    private static async Task<int> DelayAndDouble(int x)
    {
        await Task.Delay(5);
        return x * 2;
    }

    private static async Task<int> AddThenScale(int x)
    {
        int a = await Add(x, 5);
        int r = a * 2;
        if (a != 15)
            Console.WriteLine("[async] Add returned " + a);
        if (r != 30)
            Console.WriteLine("[async] r=" + r + " a=" + a + " x=" + x);
        return r;
    }

    private static async Task<int> Add(int a, int b)
    {
        if (a != 10 || b != 5)
            Console.WriteLine("[async] Add args " + a + "," + b);
        await Task.Delay(1);
        return a + b;
    }

    private static async Task RunCompletionFlag(Action action)
    {
        await Task.Delay(1);
        action();
    }

    private static void Check(string name, bool ok)
    {
        if (ok)
        {
            Console.WriteLine("[async] ok: " + name);
        }
        else
        {
            _failures++;
            Console.WriteLine("[async] FAIL: " + name);
        }
    }
}
