// NeutrinoOS Phase 8 npkg test fixture: hello-utility 1.1 payload.
// The upgrade target of tests.hello-utility (1.0.0 -> 1.1.0).
// Tier-0-JIT-safe: simple strings and Console.WriteLine only.

using System;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("hello-utility ok (1.1)");
        return 0;
    }
}
