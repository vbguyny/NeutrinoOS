// NeutrinoOS Phase 8 npkg test fixture: dependency-chain C (leaf, no deps).
// Tier-0-JIT-safe.

using System;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("chain-c ok");
        return 0;
    }
}
