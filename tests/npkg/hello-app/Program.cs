// NeutrinoOS Phase 8 npkg test fixture: hello-app payload.
// Tier-0-JIT-safe: simple strings and Console.WriteLine only.

using System;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("hello-app ok");
        return 0;
    }
}
