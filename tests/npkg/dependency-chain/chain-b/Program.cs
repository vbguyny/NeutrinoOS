// NeutrinoOS Phase 8 npkg test fixture: dependency-chain B (middle).
// Depends on tests.chain-c =1.0.0.  Tier-0-JIT-safe.

using System;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("chain-b ok");
        return 0;
    }
}
