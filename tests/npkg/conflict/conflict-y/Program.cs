// NeutrinoOS Phase 8 npkg test fixture: conflict-y.
// Requires tests.libz =2.0.0, so it cannot co-resolve with conflict-x
// (which requires tests.libz =1.0.0).  Tier-0-JIT-safe.

using System;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("conflict-y ok");
        return 0;
    }
}
