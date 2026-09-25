// NeutrinoOS Phase 8 npkg test fixture: dependency-chain A (top).
// Depends on tests.chain-b >=1.0.0.  Tier-0-JIT-safe.

using System;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("chain-a ok");
        return 0;
    }
}
