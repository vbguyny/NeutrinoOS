// NeutrinoOS Phase 8 npkg test fixture: conflict-w payload.
// Depends on conflict-x (libz =1.0.0) AND conflict-y (libz =2.0.0): the
// resolver must reject the resulting plan with a conflicting version
// requirements error.
// Tier-0-JIT-safe: simple strings and Console.WriteLine only.

using System;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("conflict-w ok");
        return 0;
    }
}
